module Collab.Tests.IdentityOwnership

open System
open Xunit
open Collab.Domain
open Collab.Daemon

let private now = DateTimeOffset.Parse "2026-09-14T12:00:00Z"
let private name raw = AgentName.create raw |> Result.defaultWith (fun error -> failwithf "%A" error)
let private endpoint session directory =
    { Harness = OpenCode; Session = SessionId session; Scope = Scope directory }
let private a = endpoint "a" "/project"
let private b = endpoint "b" "/project"
let private red = name "RedStone"
let private blue = name "BlueJay"
let private equal (expected: 'T) (actual: 'T) = Assert.Equal<'T>(expected, actual)
let private claimed agent endpoint = Router.claim now agent endpoint RouterState.empty |> fst

[<Fact>]
let ``a live peer cannot take an owned mailbox`` () =
    let state = claimed red a
    let state', intents = Router.claim now (name "redstone") b state
    equal state state'
    equal [ Decline(NameInUse(name "redstone")) ] intents
    equal None (RouterState.boundTo b state')

[<Fact>]
let ``repeating hello preserves spelling binding time and first seen`` () =
    let state = claimed red a
    let state', intents = Router.claim (now.AddMinutes 1.) (name "REDSTONE") a state
    equal state state'
    equal [ CompleteClaim(red, a) ] intents

[<Fact>]
let ``hello renames a peer while preserving its sender identity and old address`` () =
    let state = claimed red a
    let original = (RouterState.boundTo a state).Value
    let state', intents = Router.claim now blue a state
    equal [ CompleteClaim(blue, a) ] intents
    let renamed = (RouterState.boundTo a state').Value
    equal original.Id renamed.Id
    equal blue renamed.Name
    equal (Some renamed) (RouterState.lookup a.Scope red state')

[<Fact>]
let ``a refused rename cannot drain another mailbox's waiting mail`` () =
    let state = claimed red a
    let state, _ = Router.claim now blue b state
    let state, _ =
        Router.send now Limits.defaults a.Scope red
            { To = blue; Body = "waiting for BlueJay"; Urgency = AtTurnBoundary } state
    let state', intents = Router.claim now blue a state
    equal state state'
    equal [ Decline(NameInUse blue) ] intents
    equal 1 state'.Pending.Length

[<Fact>]
let ``unrelated projects can claim the same name`` () =
    let elsewhere = endpoint "elsewhere" "/other-project"
    let state = claimed red a
    let state', intents = Router.claim now red elsewhere state
    equal [ CompleteClaim(red, elsewhere) ] intents
    equal 2 state'.Registrations.Count
    equal (Some red) (RouterState.boundTo a state' |> Option.map _.Name)
    equal (Some red) (RouterState.boundTo elsewhere state' |> Option.map _.Name)

[<Fact>]
let ``a new agent reusing a name cannot inherit a deleted agents mail`` () =
    let state = claimed red a
    let state, _ = Router.claim now blue b state
    let state, released = Router.observe (SessionEnded a) state
    equal [ ReleaseBinding red ] released
    let state, parked =
        Router.send now Limits.defaults b.Scope blue
            { To = red; Body = "resume the schema work"; Urgency = AtTurnBoundary } state
    Assert.True(parked |> List.exists (function Park _ -> true | _ -> false))
    let restarted = endpoint "a-restarted" "/project"
    let later = now.AddSeconds 10.
    let state', intents = Router.claim later red restarted state
    equal [ CompleteClaim(red, restarted) ] intents
    equal 1 state'.Pending.Length
    equal (Waiting now) state'.Pending.Head.Status
    let registration = RouterState.lookup restarted.Scope red state' |> Option.get
    equal later registration.FirstSeen
    equal (Bound(restarted, later)) registration.Binding
    equal None (RouterState.boundTo a state')

[<Fact>]
let ``delayed deletion of an old session cannot release its new owner`` () =
    let state = claimed red a
    let state, _ = Router.observe (SessionEnded a) state
    let restarted = endpoint "a-restarted" "/project"
    let state, _ = Router.claim now red restarted state
    let state', intents = Router.observe (SessionEnded a) state
    equal state state'
    Assert.Empty intents
    equal (Some red) (RouterState.boundTo restarted state' |> Option.map _.Name)

[<Fact>]
let ``session deletion releases every legacy alias and leaves peers alone`` () =
    let state = claimed red a
    let state, _ = Router.claim now blue b state
    let alias = name "LegacyAlias"
    let registration = { Id = PeerId.create(); ShortId = PeerAddress.random (); Aliases = []; Name = alias; Scope = a.Scope; Binding = Bound(a, now); FirstSeen = now }
    let legacy =
        { state with Registrations = Map.add (RouterState.key a.Scope alias) registration state.Registrations }
    let state', intents = Router.observe (SessionEnded a) legacy
    equal (Set.ofList [ ReleaseBinding red; ReleaseBinding alias ]) (Set.ofList intents)
    equal None (RouterState.boundTo a state')
    equal (Some blue) (RouterState.boundTo b state' |> Option.map _.Name)
    for agent in [ red; alias ] do
        equal (Some Unbound) (RouterState.lookup a.Scope agent state' |> Option.map _.Binding)

[<Theory>]
[<InlineData(" RedStone")>]
[<InlineData("RedStone ")>]
[<InlineData("Red Stone")>]
[<InlineData("RedStone\n")>]
let ``names containing whitespace are rejected rather than rewritten`` raw =
    match AgentName.create raw with
    | Error(IllegalCharacters _) -> ()
    | other -> failwithf "Expected illegal characters, got %A" other

[<Theory>]
[<InlineData("")>]
[<InlineData(" ")>]
[<InlineData(null)>]
let ``empty names remain errors`` raw = equal (Error Empty) (AgentName.create raw)

[<Fact>]
let ``simultaneous engine claims produce one owner and one refusal`` () =
    let clock = { new Clock with member _.Now() = now }
    let engine = Engine(clock, Limits.defaults)
    let results = [ engine.Claim(red, a); engine.Claim(red, b) ] |> Async.Parallel |> Async.RunSynchronously
    let accepted = results |> Array.choose (function [ CompleteClaim(_, owner) ] -> Some owner | _ -> None)
    let refused = results |> Array.filter (function [ Decline(NameInUse _) ] -> true | _ -> false)
    equal 1 accepted.Length
    equal 1 refused.Length
    let owner = accepted[0]
    let loser = if owner = a then b else a
    let view = engine.Roster owner |> Async.RunSynchronously
    equal (Some red) (view.Caller |> Option.map _.Name)
    equal 1 view.Everyone.Length
    let request = { To = blue; Body = "hello"; Urgency = AtTurnBoundary }
    equal Anonymous (engine.Send(loser, request) |> Async.RunSynchronously)

[<Fact>]
let ``a rename stamps new sends with the current name and stable ID`` () =
    let clock = { new Clock with member _.Now() = now }
    let engine = Engine(clock, Limits.defaults)
    engine.Claim(red, a) |> Async.RunSynchronously |> ignore
    engine.Claim(blue, b) |> Async.RunSynchronously |> ignore
    equal [ CompleteClaim(name "AnotherName", a) ]
        (engine.Claim(name "AnotherName", a) |> Async.RunSynchronously)
    let request = { To = blue; Body = "schema is ready"; Urgency = AtTurnBoundary }
    match engine.Send(a, request) |> Async.RunSynchronously with
    | Decided [ PushTo(destination, envelope) ] ->
        equal b destination
        equal (name "AnotherName") envelope.From
        Assert.True envelope.FromPeer.IsSome
        equal blue envelope.To
        equal request.Body envelope.Body
    | other -> failwithf "Expected a stamped delivery, got %A" other
