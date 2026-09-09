/// The long-lived process: one instance, one socket, one copy of the registry.
///
/// It is the only component that holds harness credentials and the only one that can
/// steer a session, so it listens on a unix socket with mode 0600 and never on TCP
/// (DESIGN.md §8). Nobody starts it by hand — the shim spawns it on first use.
///
/// It is also where the agent-facing wording lives. That is deliberate: the shim
/// forwards and formats nothing, so there is one place where what an agent is told can
/// be read and changed, and no chance of the two halves disagreeing about what a verb
/// means.
namespace Collab.Daemon

open System
open System.IO
open System.Net.Sockets
open System.Text.Json.Nodes
open Collab.Domain
open Collab.Adapters.OpenCode

module Daemon =

    /// How long a call waits for the harness to say who made it.
    ///
    /// The two race by about a millisecond in practice, so this is not a latency budget
    /// but a giving-up point: if the fact has not arrived by now it is not coming, and
    /// the agent is better told so than left hanging.
    let private attributionTimeout = TimeSpan.FromSeconds 10.0

    /// Facts are held only long enough to cover the race. Every tool call in every
    /// session passes through here, not just ours, so the table is bounded on both time
    /// and size.
    let private factWindow = TimeSpan.FromMinutes 2.0
    let private factCapacity = 4096

    /// Park windows are measured in tens of seconds, so this is fine grained enough
    /// while costing nothing when there is no mail.
    let private expiryIntervalMs = 5000

    let private ok text = { Ok = true; Text = text }
    let private no text = { Ok = false; Text = text }

    /// Said after anything that will be answered later, because the failure mode this
    /// whole design exists to remove is an agent that waits in a loop instead of
    /// stopping (DESIGN.md §1).
    let private endYourTurn =
        "If you are waiting on a reply, end your turn now — you will be woken automatically when it arrives. Do not poll or loop."

    let private describeName (error: NameError) =
        match error with
        | Empty -> "a name cannot be empty"
        | TooLong(length, max) -> $"a name may be at most {max} characters, and that one is {length}"
        | IllegalCharacters offending ->
            $"a name may contain only letters, digits, hyphen and underscore, but that one contains {offending}"

    let private describeFault (fault: DeliveryFault) =
        match fault with
        | SessionGone _ -> "its session has gone"
        | HarnessUnreachable detail -> $"its runtime is unreachable ({detail})"
        | HarnessRejected(status, detail) -> $"its runtime refused the message with status {status}: {detail}"

    let private describeRefusal (refusal: Refusal) =
        match refusal with
        | SelfAddressed name -> $"you addressed yourself ({AgentName.value name})"

    let private urgencyOf (input: JsonNode) =
        match Field.text "urgency" input with
        | Some "interrupt" -> Urgency.Interrupt
        | _ -> Urgency.AtTurnBoundary

    let private directoryOf (endpoint: Endpoint) =
        let (Scope directory) = endpoint.Scope
        directory

    /// The registry as an agent should read it: names, and whether anyone is behind
    /// them right now.
    let private describeRoster (endpoint: Endpoint) (view: RosterView) =
        let directory = directoryOf endpoint

        if List.isEmpty view.Everyone then
            $"No agents have announced themselves in {directory} yet."
        else
            let line (registration: Registration) =
                let name = AgentName.value registration.Name

                let mine =
                    match view.Caller with
                    | Some caller when AgentName.equivalent caller.Name registration.Name -> " (you)"
                    | _ -> ""

                let live =
                    if Binding.isLive registration.Binding then
                        ""
                    else
                        " — announced earlier, not currently running"

                $"  {name}{mine}{live}"

            let listing = view.Everyone |> List.map line |> String.concat "\n"

            let hint =
                match view.Caller with
                | Some _ -> ""
                | None -> "\n\nYou have not announced a name yet; call hello before sending."

            $"Agents in {directory}:\n{listing}{hint}"

    /// Everything the daemon does with one call, once its session is known.
    let private dispatch
        (engine: Engine)
        (attribution: Attribution)
        (ports: Perform.Ports)
        (clock: Clock)
        (call: Call)
        : Async<Answer> =
        async {
            // Arguments are checked before the session is looked up, so a malformed
            // call is answered at once rather than after the attribution wait.
            let invocation = { Tool = call.Verb; Input = call.Input.ToJsonString() }

            let resolve () = attribution.Resolve(invocation, attributionTimeout)

            // Nothing has been performed at this point, so a repeat cannot duplicate
            // anything — and the usual cause is a daemon that had not finished
            // subscribing to the harness, which a second attempt a moment later gets
            // past. Saying so is better than a refusal the agent cannot act on.
            let unattributed =
                no
                    "Could not work out which session this call came from, so nothing was done — nothing was sent and nothing changed. Call this once more; if it fails again, report it rather than continuing to retry."

            match call.Verb with
            | Verbs.Hello ->
                match Field.text "name" call.Input with
                | None -> return no "hello needs a name."
                | Some raw ->
                    match AgentName.create raw with
                    | Error error -> return no $"'{raw}' cannot be used as a name: {describeName error}."
                    | Ok name ->
                        match! resolve () with
                        | None -> return unattributed
                        | Some endpoint ->
                            let! intents = engine.Claim(name, endpoint)
                            let! _ = Perform.all ports clock intents

                            let waiting =
                                intents
                                |> List.sumBy (function
                                    | Flush(_, envelopes) -> List.length envelopes
                                    | _ -> 0)

                            let backlog =
                                if waiting = 0 then
                                    ""
                                else
                                    $" {waiting} message(s) were already waiting for you and have just been delivered."

                            return
                                ok
                                    $"You are known as {AgentName.value name} in {directoryOf endpoint}. Peers can now reach you, and their messages arrive on their own — there is nothing to check.{backlog}"

            | Verbs.Roster ->
                match! resolve () with
                | None -> return unattributed
                | Some endpoint ->
                    let! view = engine.Roster endpoint
                    return ok (describeRoster endpoint view)

            | Verbs.Send ->
                match Field.text "to" call.Input, Field.text "body" call.Input with
                | None, _ -> return no "send needs a recipient in 'to'."
                | _, None -> return no "send needs a message in 'body'."
                | Some rawTo, Some body ->
                    match AgentName.create rawTo with
                    | Error error -> return no $"'{rawTo}' cannot be used as a name: {describeName error}."
                    | Ok recipient ->
                        match! resolve () with
                        | None -> return unattributed
                        | Some endpoint ->
                            let request =
                                { To = recipient
                                  Body = body
                                  Urgency = urgencyOf call.Input }

                            match! engine.Send(endpoint, request) with
                            | Anonymous ->
                                return
                                    no
                                        "You have not announced a name yet, so there is nobody to send this from. Call hello first."
                            | Decided intents ->
                                let! outcomes = Perform.all ports clock intents
                                let name = AgentName.value recipient

                                match outcomes with
                                | [ Delivered _ ] -> return ok $"Delivered to {name}. {endYourTurn}"
                                | [ Parked _ ] ->
                                    return
                                        ok
                                            $"{name} has not announced itself yet, so your message is held and will be delivered the moment it does. Do not resend. {endYourTurn}"
                                | [ Refused refusal ] -> return no $"Not sent: {describeRefusal refusal}."
                                | [ Failed fault ] -> return no $"Could not reach {name}: {describeFault fault}."
                                | _ -> return no "The router reached no decision, which is a bug."

            | other -> return no $"unknown verb '{other}'"
        }

    let private handle (dispatch: Call -> Async<Answer>) (client: Socket) =
        async {
            try
                use stream = new NetworkStream(client, true)
                use reader = new StreamReader(stream)
                use writer = new StreamWriter(stream, AutoFlush = true)
                let! line = reader.ReadLineAsync() |> Async.AwaitTask

                let! answer =
                    async {
                        match (if isNull line then None else Protocol.decodeCall line) with
                        | None -> return no "unreadable call"
                        | Some call ->
                            try
                                return! dispatch call
                            with error ->
                                Log.write $"dispatch failed: {error}"
                                return no $"the collaboration daemon failed: {error.Message}"
                    }

                do! writer.WriteLineAsync(Protocol.encodeAnswer answer) |> Async.AwaitTask
            with error ->
                Log.write $"connection failed: {error.Message}"
        }

    /// Bind the socket, subscribe to the harness, and serve until killed.
    ///
    /// Returns rather than throwing when another daemon already holds the lock: two
    /// shims can race to spawn one, and losing that race is the expected outcome for
    /// all but one of them, not an error.
    let run () : int =
        Paths.directory () |> ignore

        let held =
            try
                Some(
                    new FileStream(
                        Paths.daemonLock (),
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None
                    )
                )
            with _ ->
                None

        match held with
        | None ->
            Log.write "another daemon already holds the lock; exiting"
            0
        | Some lockFile ->
            use _lock = lockFile
            let path = Paths.socket ()

            // A socket file outlives the process that bound it, so a daemon that was
            // killed leaves one behind that nothing is listening on.
            if File.Exists path then
                File.Delete path

            use listener =
                new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)

            listener.Bind(UnixDomainSocketEndPoint path)
            File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            listener.Listen 64
            Log.write $"listening on {path}"

            let clock = SystemClock() :> Clock
            let engine = Engine(clock, Limits.defaults)
            let attribution = Attribution(factCapacity, factWindow, clock)

            use adapter = new OpenCodeAdapter()
            let port = adapter :> HarnessPort

            let ports kind =
                if kind = HarnessKind.OpenCode then Some port else None

            let observed (event: HarnessEvent) =
                (match event with
                 | AgentInvoked(endpoint, invocation) -> attribution.Record(endpoint, invocation)
                 | SessionEnded _ -> ())

                async {
                    let! intents = engine.Observed event
                    let! _ = Perform.all ports clock intents
                    return ()
                }
                |> Async.Start

            use _subscription = port.Observe().Subscribe(Observer.onNext observed)
            Log.write "subscribed to the opencode event stream"

            let rec expiring () =
                async {
                    do! Async.Sleep expiryIntervalMs
                    let! intents = engine.Expire()
                    let! _ = Perform.all ports clock intents
                    return! expiring ()
                }

            Async.Start(expiring ())

            let serve = dispatch engine attribution ports clock

            let rec accepting () =
                async {
                    let! client = listener.AcceptAsync() |> Async.AwaitTask
                    Async.Start(handle serve client)
                    return! accepting ()
                }

            Async.RunSynchronously(accepting ())
            0
