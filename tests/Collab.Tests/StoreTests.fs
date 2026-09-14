module Collab.Tests.DurableState

open System
open System.IO
open Microsoft.Data.Sqlite
open Xunit
open Collab.Domain
open Collab.Daemon

let private now = DateTimeOffset.Parse "2026-09-14T12:00:00Z"
let private clock = { new Clock with member _.Now() = now }
let private name raw = AgentName.create raw |> Result.defaultWith (fun e -> failwithf "%A" e)
let private a = { Harness = OpenCode; Session = SessionId "a"; Scope = Scope "/project" }
let private b = { a with Session = SessionId "b" }
let private run operation = Async.RunSynchronously(operation, 5000)
let private equal (expected: 'T) (actual: 'T) = Assert.Equal<'T>(expected, actual)
let private withDatabase test =
    let dir = Path.Combine(Path.GetTempPath(), "collab-tests-" + Guid.NewGuid().ToString "N")
    Directory.CreateDirectory dir |> ignore
    try test (Path.Combine(dir, "state.sqlite"))
    finally Directory.Delete(dir, true)
let private sample =
    let state, _ = Router.claim now (name "Red") a RouterState.empty
    Router.send now Limits.defaults a.Scope (name "Red")
        { To = name "Blue"; Body = "hello"; Urgency = Interrupt } state |> fst

[<Fact>]
let ``state survives closing and reopening sqlite`` () = withDatabase (fun path ->
    do
        use store = new SqliteStateStore(path)
        (store :> StateStore).Save(sample, now, "accepted")
    use reopened = new SqliteStateStore(path)
    equal sample ((reopened :> StateStore).Load())
    equal (UnixFileMode.UserRead ||| UnixFileMode.UserWrite) (File.GetUnixFileMode path))

[<Fact>]
let ``all delivery statuses round trip without changing message identity`` () =
    for status in [ Waiting now; Delivering b; Uncertain(b, "response lost") ] do
        let state = { sample with Pending = sample.Pending |> List.map (fun m -> { m with Status = status }) }
        equal state (StateWire.decode(StateWire.encode state))

[<Fact>]
let ``restarting with an in flight delivery retains it as uncertain`` () = withDatabase (fun path ->
    let state = { sample with Pending = sample.Pending |> List.map (fun m -> { m with Status = Delivering b }) }
    use store = new SqliteStateStore(path)
    let storage = store :> StateStore
    storage.Save(state, now, "delivery started")
    let engine = Engine(clock, Limits.defaults, store = storage)
    let recovered = run (engine.Snapshot())
    equal state.Pending.Head.Envelope recovered.Pending.Head.Envelope
    match recovered.Pending.Head.Status with
    | Uncertain(endpoint, _) -> equal b endpoint
    | other -> failwithf "unsafe recovery: %A" other
    equal recovered (storage.Load()))

[<Fact>]
let ``an audit failure rolls back the state write`` () = withDatabase (fun path ->
    use store = new SqliteStateStore(path)
    let storage = store :> StateStore
    storage.Save(sample, now, "accepted")
    use connection = new SqliteConnection($"Data Source={path};Pooling=False")
    connection.Open()
    use trigger = connection.CreateCommand()
    trigger.CommandText <- "CREATE TRIGGER reject_audit BEFORE INSERT ON audit BEGIN SELECT RAISE(ABORT,'injected failure'); END;"
    trigger.ExecuteNonQuery() |> ignore
    Assert.Throws<SqliteException>(fun () -> storage.Save(RouterState.empty, now, "must fail")) |> ignore
    equal sample (storage.Load()))

[<Fact>]
let ``failed persistence rejects acknowledgement and leaves the engine responsive`` () =
    let mutable fail = true
    let mutable saved = RouterState.empty
    let store =
        { new StateStore with
            member _.Load() = saved
            member _.Save(state, _, _) =
                if fail then failwith "disk full"
                saved <- state }
    let engine = Engine(clock, Limits.defaults, store = store)
    Assert.ThrowsAny<Exception>(fun () -> run (engine.Claim(name "Red", a)) |> ignore) |> ignore
    equal RouterState.empty (run (engine.Snapshot()))
    fail <- false
    run (engine.Claim(name "Red", a)) |> ignore
    equal saved (run (engine.Snapshot()))
    Assert.Single saved.Registrations |> ignore

[<Fact>]
let ``unsupported state versions fail explicitly`` () =
    Assert.ThrowsAny<Exception>(fun () -> StateWire.decode "{\"version\":2}" |> ignore) |> ignore

[<Fact>]
let ``positive admission reconciliation is durable and matches the endpoint`` () = withDatabase (fun path ->
    let state = { sample with Pending = sample.Pending |> List.map (fun m -> { m with Status = Uncertain(b, "lost") }) }
    use store = new SqliteStateStore(path)
    let storage = store :> StateStore
    storage.Save(state, now, "unknown")
    let engine = Engine(clock, Limits.defaults, store = storage)
    run (engine.Reconciled(a, state.Pending.Head.Envelope))
    equal state (run (engine.Snapshot()))
    run (engine.Reconciled(b, state.Pending.Head.Envelope))
    Assert.Empty (storage.Load().Pending))

[<Fact>]
let ``audit history is bounded while latest state remains durable`` () = withDatabase (fun path ->
    use store = new SqliteStateStore(path)
    let storage = store :> StateStore
    for index in 1 .. 70 do storage.Save(sample, now.AddSeconds(float index), string index)
    use connection = new SqliteConnection($"Data Source={path};Pooling=False")
    connection.Open()
    use count = connection.CreateCommand()
    count.CommandText <- "SELECT COUNT(*) FROM audit"
    equal 64L (count.ExecuteScalar() :?> int64)
    equal sample (storage.Load()))

[<Fact>]
let ``automatic names and provisional status persist`` () = withDatabase (fun path ->
    let state, _ = Router.announce now a RouterState.empty
    use store = new SqliteStateStore(path)
    let storage = store :> StateStore
    storage.Save(state, now, "automatic registration")
    let restarted = Engine(clock, Limits.defaults, store = storage)
    equal state (run (restarted.Snapshot()))
    let state, intents = run (restarted.Snapshot()) |> Router.announce now a
    equal state (storage.Load())
    Assert.Empty intents)
