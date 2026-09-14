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
    Assert.ThrowsAny<Exception>(fun () -> StateWire.decode "{\"version\":5}" |> ignore) |> ignore

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
let ``automatic names and peer IDs persist`` () = withDatabase (fun path ->
    let state, _ = Router.announce now a RouterState.empty
    use store = new SqliteStateStore(path)
    let storage = store :> StateStore
    storage.Save(state, now, "automatic registration")
    let restarted = Engine(clock, Limits.defaults, store = storage)
    equal state (run (restarted.Snapshot()))
    let state, intents = run (restarted.Snapshot()) |> Router.announce now a
    equal state (storage.Load())
    Assert.Empty intents)

let private legacyPayload state =
    let root = System.Text.Json.Nodes.JsonNode.Parse(StateWire.encode state)
    root["version"] <- System.Text.Json.Nodes.JsonValue.Create 1
    for node in root["registrations"].AsArray() do
        node.AsObject().Remove "peerId" |> ignore
        node.AsObject().Remove "aliases" |> ignore
    for mail in root["pending"].AsArray() do
        for key in [ "fromPeer"; "toPeer"; "legacyFormat" ] do mail["envelope"].AsObject().Remove key |> ignore
    root.ToJsonString()

[<Fact>]
let ``legacy migration pins identities without changing admitted wire text`` () =
    let target = { a with Session = SessionId "target" }
    let state, _ = Router.claim now (name "Blue") target sample
    let payload = legacyPayload state
    let migrated = StateWire.decode payload
    equal migrated (StateWire.decode payload)
    let original = state.Pending.Head.Envelope
    let pinned = migrated.Pending.Head.Envelope
    Assert.True pinned.FromPeer.IsSome
    Assert.True pinned.ToPeer.IsSome
    Assert.True pinned.LegacyFormat
    let oldWire = Collab.Adapters.OpenCode.Mapping.render { original with FromPeer = None; FromAddress = None; ToPeer = None }
    let newWire = Collab.Adapters.OpenCode.Mapping.render pinned
    equal oldWire newWire

[<Fact>]
let ``sqlite atomically upgrades a legacy snapshot and persists assigned IDs`` () = withDatabase (fun path ->
    do
        use store = new SqliteStateStore(path)
        (store :> StateStore).Save(sample, now, "old state")
    do
        use connection = new SqliteConnection($"Data Source={path};Pooling=False")
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- "UPDATE state SET payload=$payload WHERE id=1"
        command.Parameters.AddWithValue("$payload", legacyPayload sample) |> ignore
        command.ExecuteNonQuery() |> ignore
    let first =
        use store = new SqliteStateStore(path)
        (store :> StateStore).Load()
    use reopened = new SqliteStateStore(path)
    equal first ((reopened :> StateStore).Load())
    use connection = new SqliteConnection($"Data Source={path};Pooling=False")
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "SELECT payload FROM state WHERE id=1"
    let root = System.Text.Json.Nodes.JsonNode.Parse(command.ExecuteScalar() :?> string)
    equal 4 (root["version"].GetValue<int>()))

[<Fact>]
let ``renamed identity and aliases survive engine restart`` () = withDatabase (fun path ->
    use store = new SqliteStateStore(path)
    let storage = store :> StateStore
    let engine = Engine(clock, Limits.defaults, store = storage)
    run (engine.Claim(name "Red", a)) |> ignore
    let original = (run (engine.Snapshot()) |> RouterState.boundTo a).Value
    run (engine.Claim(name "Crimson", a)) |> ignore
    let restarted = Engine(clock, Limits.defaults, store = storage)
    let snapshot = run (restarted.Snapshot())
    let renamed = (RouterState.boundTo a snapshot).Value
    equal original.Id renamed.Id
    equal (name "Crimson") renamed.Name
    equal (Some renamed) (RouterState.lookup a.Scope (name "Red") snapshot)
    equal (Some renamed) (RouterState.byId a.Scope original.Id snapshot))

[<Fact>]
let ``version two migration preserves full IDs and already submitted message text`` () =
    let state = Router.claim now (name "Red") a RouterState.empty |> fst
    let state, _ = Router.send now Limits.defaults a.Scope (name "Red") { To = name "Blue"; Body = "old text"; Urgency = AtTurnBoundary } state
    let root = System.Text.Json.Nodes.JsonNode.Parse(StateWire.encode state)
    root["version"] <- System.Text.Json.Nodes.JsonValue.Create 2
    for r in root["registrations"].AsArray() do r.AsObject().Remove("shortId") |> ignore
    for m in root["pending"].AsArray() do m["envelope"].AsObject().Remove("fromAddress") |> ignore
    let migrated = StateWire.decode(root.ToJsonString())
    let again = StateWire.decode(root.ToJsonString())
    equal migrated again
    let peer = (RouterState.boundTo a migrated).Value
    equal (RouterState.boundTo a state).Value.Id peer.Id
    equal (Some peer) (RouterState.lookup a.Scope (name peer.ShortId) migrated)
    equal (Some peer) (RouterState.byId a.Scope peer.Id migrated)
    let oldEnvelope = { state.Pending.Head.Envelope with FromAddress = None }
    equal (Collab.Adapters.OpenCode.Mapping.render oldEnvelope) (Collab.Adapters.OpenCode.Mapping.render migrated.Pending.Head.Envelope)
    equal migrated (StateWire.decode(StateWire.encode migrated))

[<Fact>]
let ``migration resolves matching ID prefixes and rejects duplicate stored short IDs`` () =
    let state = Router.claim now (name "Red") a RouterState.empty |> fst |> Router.claim now (name "Blue") b |> fst
    let root = System.Text.Json.Nodes.JsonNode.Parse(StateWire.encode state)
    root["version"] <- System.Text.Json.Nodes.JsonValue.Create 2
    let nodes = root["registrations"].AsArray()
    nodes[0]["peerId"] <- System.Text.Json.Nodes.JsonValue.Create "peer-12345678000000000000000000000001"
    nodes[1]["peerId"] <- System.Text.Json.Nodes.JsonValue.Create "peer-12345678000000000000000000000002"
    for node in nodes do node.AsObject().Remove("shortId") |> ignore
    let migrated = StateWire.decode(root.ToJsonString())
    let addresses = migrated.Registrations |> Map.toList |> List.map (snd >> _.ShortId)
    equal 2 (Set.ofList addresses |> Set.count)
    Assert.Contains("12345678", addresses)
    equal migrated (StateWire.decode(root.ToJsonString()))
    let invalid = System.Text.Json.Nodes.JsonNode.Parse(StateWire.encode migrated)
    let duplicate = invalid["registrations"].AsArray()[1]
    duplicate["shortId"] <- System.Text.Json.Nodes.JsonValue.Create addresses.Head
    Assert.ThrowsAny<Exception>(fun () -> StateWire.decode(invalid.ToJsonString()) |> ignore) |> ignore

[<Fact>]
let ``version three removes prefixes but preserves old addresses and submitted text`` () =
    let state = Router.claim now (name "Red") a RouterState.empty |> fst
    let state, _ = Router.send now Limits.defaults a.Scope (name "Red") { To = name "Blue"; Body = "in flight"; Urgency = AtTurnBoundary } state
    let original = (RouterState.boundTo a state).Value
    let root = System.Text.Json.Nodes.JsonNode.Parse(StateWire.encode state)
    root["version"] <- System.Text.Json.Nodes.JsonValue.Create 3
    let registration = root["registrations"].AsArray()[0]
    registration["shortId"] <- System.Text.Json.Nodes.JsonValue.Create("peer-" + original.ShortId)
    let mail = root["pending"].AsArray()[0]
    mail["envelope"]["fromAddress"] <- System.Text.Json.Nodes.JsonValue.Create("peer-" + original.ShortId)
    let migrated = StateWire.decode(root.ToJsonString())
    let peer = (RouterState.boundTo a migrated).Value
    equal original.Id peer.Id
    equal original.ShortId peer.ShortId
    for address in [ peer.ShortId; "peer-" + peer.ShortId; PeerId.value peer.Id ] do
        equal (Some peer) (RouterState.lookup a.Scope (name address) migrated)
    let before = { state.Pending.Head.Envelope with FromAddress = Some("peer-" + original.ShortId) }
    equal (Collab.Adapters.OpenCode.Mapping.render before) (Collab.Adapters.OpenCode.Mapping.render migrated.Pending.Head.Envelope)
    equal migrated (StateWire.decode(StateWire.encode migrated))

let private legacyNamedSnapshot (version: int) (raw: string) (alias: bool) =
    let state = Router.claim now (name "Red") a RouterState.empty |> fst |> Router.claim now (name "Blue") b |> fst
    let root = System.Text.Json.Nodes.JsonNode.Parse(StateWire.encode state)
    root["version"] <- System.Text.Json.Nodes.JsonValue.Create version
    for node in root["registrations"].AsArray() do
        if node["name"].GetValue<string>() = "Red" then
            if alias then
                let aliases = System.Text.Json.Nodes.JsonArray()
                aliases.Add(System.Text.Json.Nodes.JsonValue.Create raw)
                node["aliases"] <- aliases
            else node["name"] <- System.Text.Json.Nodes.JsonValue.Create raw
        if version = 1 then
            for key in [ "peerId"; "aliases"; "shortId" ] do node.AsObject().Remove key |> ignore
        elif version = 2 then node.AsObject().Remove "shortId" |> ignore
        elif version = 3 then
            node["shortId"] <- System.Text.Json.Nodes.JsonValue.Create("peer-" + node["shortId"].GetValue<string>())
    root.ToJsonString()

[<Theory>]
[<InlineData(1, "deadbeef")>]
[<InlineData(1, "peer-deadbeef")>]
[<InlineData(1, "peer-12345678000000000000000000000001")>]
[<InlineData(2, "deadbeef")>]
[<InlineData(2, "peer-deadbeef")>]
[<InlineData(3, "deadbeef")>]
[<InlineData(4, "deadbeef")>]
let ``legacy ID shaped names stay addressable and preserve sender identity`` version raw =
    let state = StateWire.decode(legacyNamedSnapshot version raw false)
    let sender = (RouterState.boundTo a state).Value
    equal (name raw) sender.Name
    equal (Some sender) (RouterState.lookup a.Scope (name(raw.ToUpperInvariant())) state)
    let repeated, intents = Router.claim now sender.Name a state
    equal state repeated
    equal [ CompleteClaim(sender.Name, a) ] intents
    let sent, _ = Router.sendAs now Limits.defaults sender { To = name "Blue"; Body = "legacy sender"; Urgency = AtTurnBoundary } state
    let envelope = sent.Pending.Head.Envelope
    equal (Some sender.Id) envelope.FromPeer
    equal (Some sender.ShortId) envelope.FromAddress
    let received, _ = Router.send now Limits.defaults b.Scope (name "Blue") { To = name raw; Body = "legacy recipient"; Urgency = AtTurnBoundary } state
    equal (Some sender.Id) received.Pending.Head.Envelope.ToPeer

[<Theory>]
[<InlineData(2, "deadbeef")>]
[<InlineData(2, "peer-deadbeef")>]
[<InlineData(3, "deadbeef")>]
[<InlineData(4, "peer-12345678000000000000000000000001")>]
let ``legacy ID shaped aliases survive rename and can become the name again`` version raw =
    let state = StateWire.decode(legacyNamedSnapshot version raw true)
    let original = (RouterState.boundTo a state).Value
    equal (Some original) (RouterState.lookup a.Scope (name raw) state)
    let renamed, _ = Router.claim now (name "Crimson") a state
    let restored, intents = Router.claim now (name raw) a renamed
    let peer = (RouterState.boundTo a restored).Value
    equal original.Id peer.Id
    equal (name raw) peer.Name
    equal [ CompleteClaim(peer.Name, a) ] intents
    equal (Some peer) (RouterState.lookup a.Scope (name raw) restored)
    equal restored (StateWire.decode(StateWire.encode restored))

[<Fact>]
let ``legacy hex name migration persists and engine sender identity survives restart`` () = withDatabase (fun path ->
    use storage = new SqliteStateStore(path)
    let original = legacyNamedSnapshot 3 "deadbeef" false
    do
        use connection = new SqliteConnection($"Data Source={path}")
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- "INSERT INTO state(id,payload) VALUES(1,$payload)"
        command.Parameters.AddWithValue("$payload", original) |> ignore
        command.ExecuteNonQuery() |> ignore
    let engine = Engine(clock, Limits.defaults, store = storage)
    let before = (run (engine.Roster a)).Caller.Value
    match run (engine.Send(a, { To = name "Blue"; Body = "persisted legacy sender"; Urgency = AtTurnBoundary })) with
    | Decided [ PushTo(_, envelope) ] ->
        equal (Some before.Id) envelope.FromPeer
        equal (Some before.ShortId) envelope.FromAddress
    | result -> failwithf "unexpected %A" result
    let restart = Engine(clock, Limits.defaults, store = storage)
    let state = run (restart.Snapshot())
    equal (Some before) (RouterState.lookup a.Scope (name "deadbeef") state)
    equal (Some before.Id) state.Pending.Head.Envelope.FromPeer
    use connection = new SqliteConnection($"Data Source={path}")
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "SELECT payload FROM state WHERE id=1"
    let root = System.Text.Json.Nodes.JsonNode.Parse(string (command.ExecuteScalar()))
    equal 4 (root["version"].GetValue<int>()))

[<Fact>]
let ``ambiguous legacy migration leaves database payload and audit unchanged`` () = withDatabase (fun path ->
    use storage = new SqliteStateStore(path)
    let root = System.Text.Json.Nodes.JsonNode.Parse(legacyNamedSnapshot 3 "deadbeef" false)
    for node in root["registrations"].AsArray() do
        if node["name"].GetValue<string>() = "Blue" then
            node["shortId"] <- System.Text.Json.Nodes.JsonValue.Create "peer-deadbeef"
    let payload = root.ToJsonString()
    use connection = new SqliteConnection($"Data Source={path}")
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "INSERT INTO state(id,payload) VALUES(1,$payload)"
    command.Parameters.AddWithValue("$payload", payload) |> ignore
    command.ExecuteNonQuery() |> ignore
    let error = Assert.ThrowsAny<Exception>(fun () -> (storage :> StateStore).Load() |> ignore)
    Assert.Contains("Ambiguous legacy address 'deadbeef'", error.Message)
    Assert.Contains("Database was not migrated", error.Message)
    command.CommandText <- "SELECT payload FROM state WHERE id=1"
    equal payload (string (command.ExecuteScalar()))
    command.CommandText <- "SELECT COUNT(*) FROM audit"
    equal 0L (command.ExecuteScalar() :?> int64))

[<Fact>]
let ``legacy prefixed hex alias reserves its normalized ID during allocation`` () =
    let root = System.Text.Json.Nodes.JsonNode.Parse(legacyNamedSnapshot 2 "peer-12345678" true)
    for node in root["registrations"].AsArray() do
        if node["name"].GetValue<string>() = "Blue" then
            node["peerId"] <- System.Text.Json.Nodes.JsonValue.Create "peer-12345678000000000000000000000001"
    let state = StateWire.decode(root.ToJsonString())
    let sender = (RouterState.boundTo a state).Value
    let recipient = (RouterState.boundTo b state).Value
    Assert.NotEqual<string>("12345678", recipient.ShortId)
    equal (Some sender) (RouterState.lookup a.Scope (name "peer-12345678") state)
