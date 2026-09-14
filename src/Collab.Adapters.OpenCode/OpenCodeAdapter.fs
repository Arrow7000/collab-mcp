/// `HarnessPort` for opencode2.
///
/// This file is the translation layer, and translation is all it does: our vocabulary
/// on one side, opencode's on the other, and no policy in between. Whether a message
/// may be sent at all is the router's decision (DESIGN.md §8); by the time an envelope
/// arrives here it is going out.
namespace Collab.Adapters.OpenCode

open System
open System.Text.Json.Nodes
open System.Threading
open Collab.Domain

/// The event types we act on. Everything else on the bus — 84 types at the time of
/// writing, most of them token deltas — is ignored, because the router needs exactly
/// two facts (Ports.fs) and subscribing to more would invite modelling turn state.
module EventTypes =

    /// The attribution keystone (DESIGN.md §4, F6).
    ///
    /// opencode2 does not expose MCP tools to the model directly: they are reachable
    /// only from inside the `execute` sandbox, as `tools.<server>.<tool>(args)`. So the
    /// obvious-looking `session.tool.called` reports the *sandbox* call, and its `input`
    /// is JavaScript source — the arguments an MCP server actually receives are not in
    /// it. They surface here instead, alongside the session that sent them.
    [<Literal>]
    let ToolProgress = "session.tool.progress"

    [<Literal>]
    let ToolInputStarted = "session.tool.input.started"

    [<Literal>]
    let ToolCalled = "session.tool.called"

    /// A session has gone. Its binding must be released.
    [<Literal>]
    let SessionDeleted = "session.deleted"

/// What a single frame tells us. A list, because one progress frame reports every MCP
/// call the enclosing sandbox call has made so far, and so can name more than one.
type Observation =
    /// One MCP tool call, with an identity for the call itself.
    ///
    /// The identity is needed because each frame repeats every call reported before it,
    /// so without one a single call is published again for every later call around it.
    | McpToolCalled of endpoint: Endpoint * call: string * invocation: ToolInvocation
    | SessionDeleted of endpoint: Endpoint

module Mapping =

    /// This is the server key in the supported OpenCode configuration.
    [<Literal>]
    let ServerName = "collab"

    /// Newer harnesses carry session context outside model-visible tool arguments.
    /// The namespaced key is in current source; the plain key is in public V2 docs.
    /// Malformed or conflicting context must never fall back to argument-only matching.
    let sessionMetadata (metadata: JsonNode option) : Result<SessionId, string> =
        let read key (object': JsonObject) =
            if not (object'.ContainsKey key) then
                Ok None
            else
                match Json.stringField key object' with
                | Some value when not (String.IsNullOrWhiteSpace value) -> Ok(Some(SessionId value))
                | _ -> Error $"'{key}' must be a nonempty session ID"

        match metadata with
        | None -> Error "session metadata is required; use an OC2 runtime that supplies MCP session context"
        | Some(:? JsonObject as object') ->
            match read "ai.opencode/sessionID" object', read "sessionID" object' with
            | Error error, _ | _, Error error -> Error error
            | Ok(Some a), Ok(Some b) when a <> b -> Error "session metadata keys disagree"
            | Ok(Some session), _ | _, Ok(Some session) -> Ok session
            | _ -> Error "session metadata is required; expected ai.opencode/sessionID or sessionID"
        | Some _ -> Error "MCP metadata must be an object"

    /// The mapping the whole design turns on.
    ///
    /// The sender states intent and the runtime chooses the mechanism (Message.fs), so
    /// this is where intent becomes mechanism — and the only place either word for it
    /// is written down.
    let delivery (urgency: Urgency) : Delivery =
        match urgency with
        | Urgency.Interrupt -> Steer
        | Urgency.AtTurnBoundary -> Queue

    /// Render an envelope so the recipient can see *who* it is from.
    ///
    /// opencode already marks the message `synthetic`, so provenance of *kind* is
    /// structural (DESIGN.md §4, F4) — but that says nothing about whose message it
    /// is, and the model sees only the text. The sender's name therefore goes in the
    /// body, and the description carries it again for the UI.
    ///
    /// The sender's name is all a recipient needs to answer, so there is no thread or
    /// reply-to to render yet.
    let render (envelope: Envelope) : SyntheticMessage =
        let sender = AgentName.value envelope.From
        let (MessageId id) = envelope.Id

        let fromPeer = if envelope.LegacyFormat then None else envelope.FromPeer
        { Id = Some("msg_" + id.ToString "N")
          Text =
              match fromPeer with
              | None -> $"[peer {sender}]: {envelope.Body}"
              | Some peer -> $"[peer {sender} · {PeerId.value peer}]: {envelope.Body}\n\nReply address: {PeerId.value peer}. This ID stays the same across name changes."
          Description = Some(match fromPeer with
                             | None -> $"peer message from {sender}"
                             | Some peer -> $"peer message from {sender} [{PeerId.value peer}]")
          Delivery = delivery envelope.Urgency }

    /// A session id from the bus is an opencode endpoint by construction: this is the
    /// only harness this adapter speaks for.
    ///
    /// An event without a `location` cannot be scoped, and an unscoped endpoint would
    /// silently join every project into one namespace. So the directory is required,
    /// and a frame lacking it is dropped rather than guessed at.
    let private endpointOf (frame: EventFrame) (session: string) : Endpoint option =
        frame.Directory
        |> Option.bind (fun directory ->
            try
                if String.IsNullOrWhiteSpace session || not (System.IO.Path.IsPathFullyQualified directory) then None
                else
                    Some
                        { Harness = HarnessKind.OpenCode
                          Session = SessionId session
                          Scope = Scope(Scope.key (Scope directory)) }
            with _ -> None)

    /// Only this server's progress can attribute this server's calls.
    let private bareTool (qualified: string) : string option =
        let prefix = ServerName + "."
        if qualified.StartsWith(prefix, StringComparison.Ordinal) then
            Some(qualified.Substring prefix.Length)
        else
            None

    /// What identifies one MCP call.
    ///
    /// All three parts are load-bearing. `toolCalls` is cumulative and append-only, so
    /// position within the sandbox call distinguishes the calls inside it. The sandbox
    /// call's own id would distinguish those lists — except that it is minted by the
    /// model provider rather than by opencode, and at least one provider issues the
    /// literal `tool_0` for every call in a session. The assistant step is what actually
    /// separates them.
    let private callIdentity (endpoint: Endpoint) (step: string) (callId: string) (index: int) : string =
        let (SessionId session) = endpoint.Session
        let (Scope directory) = endpoint.Scope
        System.Text.Json.JsonSerializer.Serialize [ directory; session; step; callId; string index ]

    /// The MCP calls a progress frame reports as having just started.
    let private startedIn (data: JsonNode) : (int * ToolInvocation) list =
        match
            Json.field "metadata" data
            |> Option.bind (Json.field "toolCalls")
        with
        | Some(:? JsonArray as calls) ->
            calls
            |> Seq.indexed
            |> Seq.choose (fun (index, call) ->
                match call with
                | null -> None
                | entry ->
                    match Json.stringField "status" entry, Json.stringField "tool" entry with
                    | Some "running", Some tool ->
                        // A call with no arguments is reported with no `input` field at
                        // all, which states the same fact as an empty object. Treating
                        // the two differently would make an argument-free verb — which
                        // `roster` is — impossible to attribute.
                        let input =
                            Json.field "input" entry
                            |> Option.map (fun node -> node.ToJsonString())
                            |> Option.defaultValue "{}"

                        bareTool tool |> Option.map (fun bare -> index, { Tool = bare; Input = input })
                    | _ -> None)
            |> Seq.toList
        | _ -> []

    /// Decode one frame. Total: anything we do not act on, or cannot read, is empty.
    let observe (frame: EventFrame) : Observation list =
        let field name = Json.stringField name frame.Data

        match frame.Type with
        | EventTypes.ToolProgress ->
            match field "sessionID" |> Option.bind (endpointOf frame), field "id", field "assistantMessageID" with
            | Some endpoint, Some callId, Some step ->
                startedIn frame.Data
                |> List.map (fun (index, invocation) ->
                    McpToolCalled(endpoint, callIdentity endpoint step callId index, invocation))
            | _ -> []
        | EventTypes.SessionDeleted ->
            field "sessionID"
            |> Option.bind (endpointOf frame)
            |> Option.map SessionDeleted
            |> Option.toList
        | _ -> []

    /// New or reopened sessions, plus first execution as a fallback after missed events.
    let sessionAvailable (frame: EventFrame) =
        if List.contains frame.Type [ "session.created"; "session.viewed"; "session.execution.started" ] then
            Json.stringField "sessionID" frame.Data |> Option.bind (endpointOf frame)
        else None

    /// Direct calls split tool name and input across two events.
    let directIdentity (frame: EventFrame) =
        match Json.stringField "sessionID" frame.Data |> Option.bind (endpointOf frame),
              Json.stringField "assistantMessageID" frame.Data, Json.stringField "id" frame.Data with
        | Some endpoint, Some step, Some id -> Some(endpoint, callIdentity endpoint step id 0)
        | _ -> None

    let directStarted (frame: EventFrame) =
        if frame.Type <> EventTypes.ToolInputStarted then None
        else
            match directIdentity frame, Json.stringField "name" frame.Data with
            | Some(_, identity), Some qualified ->
                let prefix = ServerName + "_"
                if qualified.StartsWith(prefix, StringComparison.Ordinal) then
                    let tool = qualified.Substring prefix.Length
                    if List.contains tool [ "hello"; "roster"; "send" ] then Some(identity, tool) else None
                else None
            | _ -> None

    let directCalled (tool: string) (frame: EventFrame) =
        if frame.Type <> EventTypes.ToolCalled || not (List.contains tool [ "hello"; "roster"; "send" ]) then []
        else
            directIdentity frame |> Option.map (fun (endpoint, identity) ->
                let input = Json.field "input" frame.Data |> Option.map (fun n -> n.ToJsonString()) |> Option.defaultValue "{}"
                McpToolCalled(endpoint, identity, { Tool = tool; Input = input })) |> Option.toList

module Faults =

    /// opencode's failures in the domain's words.
    ///
    /// `SessionGone` needs the endpoint the call was made for, which the HTTP layer
    /// never knew, so the translation belongs here and not in the client.
    let ofApi (endpoint: Endpoint) (error: ApiError) : DeliveryFault =
        match error with
        | ApiError.Unreachable detail -> HarnessUnreachable detail
        | ApiError.SessionNotFound _ -> SessionGone endpoint
        | ApiError.Rejected(status, detail) -> HarnessRejected(status, detail)
        | ApiError.AdmissionUnknown detail -> Collab.Domain.AdmissionUnknown detail

/// Sandbox calls we have already reported.
///
/// Every progress frame restates the MCP calls before it, so without this one call
/// would be published as many times as the sandbox around it makes further calls.
/// Bounded, because the alternative is a table that grows for the life of the daemon.
type internal Reported(capacity: int) =
    let gate = obj ()
    let seen = System.Collections.Generic.HashSet<string>()
    let arrival = System.Collections.Generic.Queue<string>()

    /// True the first time a given call is offered, false afterwards.
    member _.IsNew(key: string) : bool =
        lock gate (fun () ->
            if seen.Add key then
                arrival.Enqueue key

                while arrival.Count > capacity do
                    seen.Remove(arrival.Dequeue()) |> ignore

                true
            else
                false)

/// Bounded name context awaiting the direct call's arguments.
type internal DirectCalls(capacity: int) =
    let gate = obj ()
    let names = System.Collections.Generic.Dictionary<string, string>()
    let order = System.Collections.Generic.Queue<string>()
    member _.Remember(identity, tool) = lock gate (fun () ->
        if names.TryAdd(identity, tool) then order.Enqueue identity
        while order.Count > capacity do names.Remove(order.Dequeue()) |> ignore)
    member _.Take(identity) = lock gate (fun () ->
        match names.TryGetValue identity with
        | true, tool -> names.Remove identity |> ignore; Some tool
        | _ -> None)

/// Multicast for the event stream.
///
/// Hand-rolled rather than taking a dependency on System.Reactive for one subject: an
/// observer that throws is isolated, because a bad subscriber must not be able to kill
/// the pump that every other subscriber depends on.
type internal Broadcast<'T>() =
    let gate = obj ()
    let observers = ResizeArray<IObserver<'T>>()

    member _.Publish(value: 'T) =
        let snapshot = lock gate (fun () -> observers.ToArray())

        for observer in snapshot do
            try
                observer.OnNext value
            with _ ->
                ()

    interface IObservable<'T> with
        member _.Subscribe(observer: IObserver<'T>) =
            lock gate (fun () -> observers.Add observer)

            { new IDisposable with
                member _.Dispose() =
                    lock gate (fun () -> observers.Remove observer |> ignore) }

/// `HarnessPort` over opencode2.
///
/// Deliver is one POST. Observe is one long-lived subscription to the daemon-wide
/// event stream, shared by every subscriber: the daemon has one bus, and opening a
/// connection per subscriber would multiply the overflow risk for no gain.
type EventConnection =
    | Connecting
    | Connected
    | Disconnected of detail: string

type OpenCodeAdapter private (client: OpenCodeClient, ownsClient: bool) =

    /// How long to wait before reconnecting a stream that ended. Long enough not to
    /// spin against a daemon that is down, short enough that a restart is invisible.
    let reconnectDelayMs = 1000

    /// Enough for the sandbox calls in flight across every session at once. Overrun
    /// costs a duplicate report, which attribution then consumes against a call that
    /// has already been answered — so it is sized generously.
    let reported = Reported 4096
    let directCalls = DirectCalls 4096

    let events = Broadcast<HarnessEvent>()
    let sessions = Broadcast<Endpoint>()
    let connections = Broadcast<EventConnection>()
    let mutable connection = Connecting
    let cancellation = new CancellationTokenSource()
    let gate = obj ()
    let mutable pumping = false

    let handle (frame: EventFrame) =
        Mapping.sessionAvailable frame |> Option.iter sessions.Publish
        Mapping.directStarted frame |> Option.iter (fun (identity, tool) -> directCalls.Remember(identity, tool))
        let direct =
            if frame.Type = EventTypes.ToolCalled then
                Mapping.directIdentity frame |> Option.bind (fun (_, identity) -> directCalls.Take identity)
                |> Option.map (fun tool -> Mapping.directCalled tool frame) |> Option.defaultValue []
            else []
        for observation in Mapping.observe frame @ direct do
            match observation with
            | McpToolCalled(endpoint, call, invocation) ->
                if reported.IsNew call then
                    events.Publish(AgentInvoked(endpoint, invocation))
            | SessionDeleted endpoint -> events.Publish(SessionEnded endpoint)

    let connectionChanged status =
        lock gate (fun () -> connection <- status)
        connections.Publish status

    let rec pump () =
        async {
            connectionChanged Connecting
            let! result = client.ReadEvents(handle, cancellation.Token, onConnected = (fun () -> connectionChanged Connected))
            if not cancellation.IsCancellationRequested then
                connectionChanged (Disconnected(match result with Ok () -> "stream closed" | Error error -> sprintf "%A" error))

            // A failed stream and a restarted daemon are answered the same way, so we
            // do not distinguish them: reconnect, and let the client rediscover the
            // port. Reconciling the events missed in the gap is the router's job
            // (DESIGN.md §9); this adapter never claims to have seen everything.
            if not cancellation.IsCancellationRequested then
                do! Async.Sleep reconnectDelayMs

                if not cancellation.IsCancellationRequested then
                    return! pump ()
        }

    let ensurePumping () =
        lock gate (fun () ->
            if not pumping then
                pumping <- true
                Async.Start(pump (), cancellation.Token))

    /// Wrap a client the caller owns and will dispose.
    new(client: OpenCodeClient) = new OpenCodeAdapter(client, false)

    /// Discover the daemon and hold the connection for this adapter's lifetime.
    new() = new OpenCodeAdapter(new OpenCodeClient(), true)

    member _.FindAdmission(endpoint: Endpoint, envelope: Envelope) =
        let (SessionId session) = endpoint.Session
        client.FindSynthetic(session, Mapping.render envelope)

    member _.Sessions = sessions :> IObservable<Endpoint>
    member _.HasCollab(endpoint: Endpoint) =
        let (Scope directory) = endpoint.Scope
        client.HasServer(directory, Mapping.ServerName)

    member _.Connection = lock gate (fun () -> connection)
    member _.Connections = connections :> IObservable<EventConnection>

    interface HarnessPort with

        member _.Kind = HarnessKind.OpenCode

        member _.Deliver(endpoint, envelope) =
            async {
                match endpoint.Harness with
                | HarnessKind.ClaudeCode ->
                    // A routing bug, not a harness failure. Status 0 says no request
                    // was made: there is no HTTP exchange to report.
                    return Error(HarnessRejected(0, "endpoint is not an opencode session"))
                | HarnessKind.OpenCode ->
                    let (SessionId session) = endpoint.Session
                    let! result = client.PostSynthetic(session, Mapping.render envelope)
                    return result |> Result.map ignore |> Result.mapError (Faults.ofApi endpoint)
            }

        member _.Observe() =
            ensurePumping ()
            events :> IObservable<HarnessEvent>

    interface IDisposable with
        member _.Dispose() =
            cancellation.Cancel()
            cancellation.Dispose()

            if ownsClient then
                (client :> IDisposable).Dispose()
