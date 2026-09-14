module Collab.Tests.OutboxDelivery

open System
open Xunit
open Collab.Domain
open Collab.Daemon

let private now = DateTimeOffset.Parse "2026-09-14T12:00:00Z"
let private name raw = AgentName.create raw |> Result.defaultWith (fun e -> failwithf "%A" e)
let private red, blue = name "Red", name "Blue"
let private a = { Harness = OpenCode; Session = SessionId "a"; Scope = Scope "/project" }
let private b = { a with Session = SessionId "b" }
let private request body = { To = blue; Body = body; Urgency = AtTurnBoundary }
let private equal (expected: 'T) (actual: 'T) = Assert.Equal<'T>(expected, actual)
let private run operation = Async.RunSynchronously operation
let private senderOnly = Router.claim now red a RouterState.empty |> fst
let private both = Router.claim now blue b senderOnly |> fst

[<Fact>]
let ``direct delivery stays in the outbox until admission is acknowledged`` () =
    let state, intents = Router.send now Limits.defaults a.Scope red (request "hello") both
    let mail = Assert.Single state.Pending
    equal (Delivering b) mail.Status
    equal [ PushTo(b, mail.Envelope) ] intents
    let state = Router.completed now b mail.Envelope Admitted state
    Assert.Empty state.Pending

[<Fact>]
let ``a missing session releases its name and retains the original message`` () =
    let state, _ = Router.send now Limits.defaults a.Scope red (request "hello") both
    let mail = state.Pending.Head
    let state = Router.completed now b mail.Envelope (NotAdmitted(SessionGone b)) state
    equal (Waiting now) state.Pending.Head.Status
    equal mail.Envelope state.Pending.Head.Envelope
    equal (Some Unbound) (RouterState.lookup b.Scope blue state |> Option.map _.Binding)
    let restarted = { b with Session = SessionId "b-restarted" }
    let state, intents = Router.claim now blue restarted state
    equal [ CompleteClaim(blue, restarted); PushTo(restarted, mail.Envelope) ] intents
    equal (Delivering restarted) state.Pending.Head.Status

[<Fact>]
let ``known pre-request outages retry later without changing the message ID`` () =
    let state, _ = Router.send now Limits.defaults a.Scope red (request "hello") both
    let mail = state.Pending.Head
    let state = Router.completed now b mail.Envelope (NotAdmitted(HarnessUnreachable "discovery failed")) state
    let state, early = Router.expire (now.AddSeconds 4.) state
    Assert.Empty early
    let state, ready = Router.expire (now.AddSeconds 5.) state
    equal [ PushTo(b, mail.Envelope) ] ready
    equal (Delivering b) state.Pending.Head.Status

[<Fact>]
let ``uncertain admission is retained and blocks later messages to that mailbox`` () =
    let state, _ = Router.send now Limits.defaults a.Scope red (request "first") both
    let first = state.Pending.Head.Envelope
    let state = Router.completed now b first (NotAdmitted(AdmissionUnknown "response lost")) state
    let state, _ = Router.send now Limits.defaults a.Scope red (request "second") state
    let state, intents = Router.expire (now.AddSeconds 10.) state
    Assert.Contains(state.Pending, fun p -> p.Envelope.Id = first.Id && p.Status = Uncertain(b, "response lost"))
    Assert.DoesNotContain(intents, fun i -> match i with PushTo(e, _) | Flush(e, _) -> e = b | _ -> false)
    equal 1 (state.Pending |> List.filter (fun p -> p.Purpose = FailureNotice) |> List.length)
    let later, _ = Router.expire (now.AddDays 1.) state
    Assert.Contains(later.Pending, fun p -> p.Envelope.Id = first.Id)

[<Fact>]
let ``rejected admission becomes a retained failure notice`` () =
    let state, _ = Router.send now Limits.defaults a.Scope red (request "hello") both
    let original = state.Pending.Head.Envelope
    let state = Router.completed now b original (NotAdmitted(HarnessRejected(400, "invalid input"))) state
    let notice = Assert.Single state.Pending
    equal FailureNotice notice.Purpose
    equal red notice.Envelope.To
    Assert.Contains("invalid input", notice.Envelope.Body)
    Assert.DoesNotContain(state.Pending, fun p -> p.Envelope.Id = original.Id)

[<Fact>]
let ``claim at the deadline does not deliver expired mail`` () =
    let state, _ = Router.send now Limits.defaults a.Scope red (request "expired") senderOnly
    let original = state.Pending.Head.Envelope
    let state, intents = Router.claim (now.AddSeconds 90.) blue b state
    equal [ CompleteClaim(blue, b) ] intents
    let notice = Assert.Single state.Pending
    equal FailureNotice notice.Purpose
    equal red notice.Envelope.To
    Assert.DoesNotContain(state.Pending, fun p -> p.Envelope.Id = original.Id)

[<Fact>]
let ``expiry notices wait for absent senders without bouncing forever`` () =
    let state, _ = Router.send now Limits.defaults a.Scope red (request "expired") senderOnly
    let state, _ = Router.observe (SessionEnded a) state
    let state, intents = Router.expire (now.AddSeconds 90.) state
    Assert.Empty intents
    equal FailureNotice state.Pending.Head.Purpose
    let state, intents = Router.expire (now.AddSeconds 180.) state
    Assert.Empty state.Pending
    Assert.Empty intents

[<Fact>]
let ``expiry never races an in-flight admission`` () =
    let state, _ = Router.send now Limits.defaults a.Scope red (request "hello") both
    let mail = state.Pending.Head
    let state, intents = Router.expire (now.AddSeconds 100.) state
    equal [ mail ] state.Pending
    Assert.Empty intents
    Assert.Empty((Router.completed (now.AddSeconds 101.) b mail.Envelope Admitted state).Pending)

[<Fact>]
let ``wrong-endpoint and repeated acknowledgements cannot remove unrelated mail`` () =
    let state, _ = Router.send now Limits.defaults a.Scope red (request "hello") both
    let envelope = state.Pending.Head.Envelope
    equal state (Router.completed now a envelope Admitted state)
    let admitted = Router.completed now b envelope Admitted state
    equal admitted (Router.completed now b envelope Admitted admitted)

[<Fact>]
let ``new sends cannot overtake a backlog being admitted`` () =
    let state, _ = Router.send now Limits.defaults a.Scope red (request "first") both
    let state, intents = Router.send now Limits.defaults a.Scope red (request "second") state
    match intents with
    | [ Park(envelope, _) ] -> equal "second" envelope.Body
    | other -> failwithf "Expected queued second send, got %A" other
    let state, attempts = Router.expire now state
    Assert.Empty attempts
    let first = state.Pending.Head.Envelope
    let state = Router.completed now b first Admitted state
    let state, attempts = Router.expire now state
    equal [ PushTo(b, state.Pending.Head.Envelope) ] attempts
    equal "second" state.Pending.Head.Envelope.Body

let private port deliver =
    { new HarnessPort with
        member _.Kind = OpenCode
        member _.Deliver(endpoint, envelope) = deliver endpoint envelope
        member _.Observe() =
            { new IObservable<HarnessEvent> with
                member _.Subscribe(_) = { new IDisposable with member _.Dispose() = () } } }

[<Fact>]
let ``perform stops a failed flush and retains all unattempted mail`` () =
    let clock = { new Clock with member _.Now() = now }
    let engine = Engine(clock, Limits.defaults)
    engine.Claim(red, a) |> run |> ignore
    for body in [ "first"; "second"; "third" ] do engine.Send(a, request body) |> run |> ignore
    let intents = engine.Claim(blue, b) |> run
    let calls = ResizeArray<string>()
    let failing = port (fun endpoint envelope -> async {
        calls.Add envelope.Body
        return Error(SessionGone endpoint) })
    let outcomes = Perform.all (fun _ -> Some failing) clock engine intents |> run
    equal [ "first" ] (List.ofSeq calls)
    equal 3 outcomes.Length
    Assert.All(outcomes, fun outcome -> match outcome with Parked _ -> () | x -> failwithf "%A" x)
    let state = engine.Snapshot() |> run
    equal 3 state.Pending.Length
    Assert.All(state.Pending, fun mail -> equal (Waiting now) mail.Status)
    equal None (RouterState.boundTo b state)

[<Fact>]
let ``perform admits a backlog in order and removes only acknowledged items`` () =
    let clock = { new Clock with member _.Now() = now }
    let engine = Engine(clock, Limits.defaults)
    engine.Claim(red, a) |> run |> ignore
    for body in [ "first"; "second"; "third" ] do engine.Send(a, request body) |> run |> ignore
    let intents = engine.Claim(blue, b) |> run
    let calls = ResizeArray<string>()
    let accepting = port (fun _ envelope -> async {
        calls.Add envelope.Body
        let state = engine.Snapshot() |> run
        Assert.Contains(state.Pending, fun p -> p.Envelope.Id = envelope.Id)
        return Ok() })
    let outcomes = Perform.all (fun _ -> Some accepting) clock engine intents |> run
    equal [ "first"; "second"; "third" ] (List.ofSeq calls)
    equal 3 outcomes.Length
    Assert.Empty((engine.Snapshot() |> run).Pending)

[<Fact>]
let ``throwing adapters produce uncertain admission rather than losing mail`` () =
    let clock = { new Clock with member _.Now() = now }
    let engine = Engine(clock, Limits.defaults)
    engine.Claim(red, a) |> run |> ignore
    engine.Claim(blue, b) |> run |> ignore
    let intents = match engine.Send(a, request "hello") |> run with Decided i -> i | x -> failwithf "%A" x
    let throwing = port (fun _ _ -> async { return failwith "lost response" })
    let outcomes = Perform.all (fun _ -> Some throwing) clock engine intents |> run
    match outcomes with
    | [ Failed(AdmissionUnknown "lost response") ] -> ()
    | other -> failwithf "Expected uncertainty, got %A" other
    Assert.Contains((engine.Snapshot() |> run).Pending, fun p ->
        match p.Status with Uncertain _ -> true | _ -> false)

[<Fact>]
let ``case-sensitive project directories remain separate namespaces`` () =
    Assert.NotEqual<string>(Scope.key (Scope "/project/A"), Scope.key (Scope "/project/a"))
    equal (Scope.key (Scope "/project")) (Scope.key (Scope "/project/./"))
    equal "/" (Scope.key (Scope "/"))

[<Fact>]
let ``message size uses UTF8 bytes and refuses before creating mail`` () =
    let limits = { Limits.defaults with MaxBodyBytes = 3 }
    let state, intents = Router.send now limits a.Scope red (request "éé") senderOnly
    Assert.Empty state.Pending
    equal [ Decline(MessageTooLarge(4, 3)) ] intents

[<Fact>]
let ``a full mailbox refuses new mail and preserves accepted mail`` () =
    let limits = { Limits.defaults with MaxMailboxPeers = 1 }
    let first, _ = Router.send now limits a.Scope red (request "first") senderOnly
    let state, intents = Router.send now limits a.Scope red (request "second") first
    equal first state
    equal [ Decline(QueueFull "the recipient mailbox is full") ] intents

[<Fact>]
let ``global capacity reserves room for failure notices`` () =
    let limits = { Limits.defaults with MaxPending = 2 }
    let first, _ = Router.send now limits a.Scope red (request "first") senderOnly
    let state, intents = Router.send now limits a.Scope red (request "second") first
    equal first state
    equal [ Decline(QueueFull "the router outbox is full") ] intents

[<Fact>]
let ``automatic registration is stable unique and idempotent`` () =
    let first, _ = Router.announce now a RouterState.empty
    let registration = RouterState.boundTo a first |> Option.get
    Assert.StartsWith("oc2-", AgentName.value registration.Name)
    Assert.True registration.Provisional
    let repeated, intents = Router.announce (now.AddMinutes 1.) a first
    equal first repeated
    Assert.Empty intents
    let both, _ = Router.announce now b first
    let other = RouterState.boundTo b both |> Option.get
    Assert.NotEqual(registration.Name, other.Name)

[<Fact>]
let ``explicit hello can replace an unused automatic name`` () =
    let state, _ = Router.announce now a RouterState.empty
    let state, _ = Router.claim now red a state
    let registration = RouterState.boundTo a state |> Option.get
    equal red registration.Name
    Assert.False registration.Provisional
    equal 1 state.Registrations.Count

[<Fact>]
let ``automatic naming never replaces an explicit identity`` () =
    let state, intents = Router.announce now a senderOnly
    equal senderOnly state
    Assert.Empty intents

[<Fact>]
let ``sending fixes the automatic name before communication`` () =
    let state, _ = Router.announce now a RouterState.empty
    let sender = (RouterState.boundTo a state).Value.Name
    let state, _ = Router.send now Limits.defaults a.Scope sender (request "hello") state
    let registration = (RouterState.boundTo a state).Value
    Assert.False registration.Provisional
    let unchanged, intents = Router.claim now red a state
    equal state unchanged
    equal [ Decline(AlreadyNamed(sender, red)) ] intents

[<Fact>]
let ``receiving mail fixes an automatic name without orphaning its mailbox`` () =
    let state, _ = Router.announce now b senderOnly
    let recipient = (RouterState.boundTo b state).Value.Name
    let state, _ = Router.send now Limits.defaults a.Scope red { To = recipient; Body = "hello"; Urgency = AtTurnBoundary } state
    Assert.False (RouterState.boundTo b state).Value.Provisional
    let unchanged, intents = Router.claim now blue b state
    equal state unchanged
    equal [ Decline(AlreadyNamed(recipient, blue)) ] intents
