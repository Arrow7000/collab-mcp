/// `HarnessPort` for opencode2.
///
/// This file is the translation layer, and translation is all it does: our vocabulary
/// on one side, opencode's on the other, and no policy in between. Whether a message
/// may be sent at all is the router's decision (DESIGN.md §8); by the time an envelope
/// arrives here it is going out.
namespace Collab.Adapters.OpenCode

open System
open System.Threading
open Collab.Domain

/// The event types we act on. Everything else on the bus — 84 types at the time of
/// writing, most of them token deltas — is ignored, because the router needs exactly
/// two facts (Ports.fs) and subscribing to more would invite modelling turn state.
module EventTypes =

    /// Names a tool call, but carries no input.
    [<Literal>]
    let ToolInputStarted = "session.tool.input.started"

    /// Carries the input and the session, but not the tool name. The attribution
    /// keystone (DESIGN.md §4, F6).
    [<Literal>]
    let ToolCalled = "session.tool.called"

    /// A session has gone. Its binding must be released.
    [<Literal>]
    let SessionDeleted = "session.deleted"

/// What a single frame tells us, before the two halves of a tool call are joined.
///
/// This exists because opencode reports one call as two events: `input.started` names
/// the tool, `called` carries the input, and neither alone is a `ToolInvocation`.
type Observation =
    | ToolNamed of callId: string * tool: string
    | ToolCalled of endpoint: Endpoint * callId: string * input: string
    | SessionDeleted of endpoint: Endpoint

module Mapping =

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
    /// A reply names the message it answers, because the domain lets a recipient
    /// correlate one (`InReplyTo`) and it cannot do so from a body that never said.
    let render (envelope: Envelope) : SyntheticMessage =
        let sender = AgentName.value envelope.From

        let attribution =
            match envelope.InReplyTo with
            | Some(MessageId original) -> $"[peer {sender}, replying to {original}]"
            | None -> $"[peer {sender}]"

        { Text = $"{attribution}: {envelope.Body}"
          Description = Some $"peer message from {sender}"
          Delivery = delivery envelope.Urgency }

    /// A session id from the bus is an opencode endpoint by construction: this is the
    /// only harness this adapter speaks for.
    let private endpointOf (session: string) : Endpoint =
        { Harness = HarnessKind.OpenCode
          Session = SessionId session }

    /// Decode one frame. Total: anything we do not act on, or cannot read, is `None`.
    let observe (frame: EventFrame) : Observation option =
        let field name = Json.stringField name frame.Data

        match frame.Type with
        | EventTypes.ToolInputStarted ->
            match field "id", field "name" with
            | Some callId, Some tool -> Some(ToolNamed(callId, tool))
            | _ -> None
        | EventTypes.ToolCalled ->
            match field "sessionID", field "id", Json.field "input" frame.Data with
            | Some session, Some callId, Some input ->
                Some(ToolCalled(endpointOf session, callId, input.ToJsonString()))
            | _ -> None
        | EventTypes.SessionDeleted -> field "sessionID" |> Option.map (endpointOf >> SessionDeleted)
        | _ -> None

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

/// Tool names awaiting their call.
///
/// opencode never repeats the name on the event that carries the input, so a name is
/// held from `session.tool.input.started` until the matching `session.tool.called`.
/// Bounded, because a call whose second half never arrives — an interrupted turn, a
/// dropped frame — would otherwise leak a name for the life of the daemon.
type internal ToolNames(capacity: int) =
    let gate = obj ()
    let names = System.Collections.Generic.Dictionary<string, string>()
    let arrival = System.Collections.Generic.Queue<string>()

    member _.Remember(callId: string, tool: string) =
        lock gate (fun () ->
            if names.TryAdd(callId, tool) then
                arrival.Enqueue callId

                while arrival.Count > capacity do
                    names.Remove(arrival.Dequeue()) |> ignore)

    /// Names are consumed: one call has one name, and keeping it afterwards only
    /// grows the table.
    member _.Take(callId: string) : string option =
        lock gate (fun () ->
            match names.TryGetValue callId with
            | true, tool ->
                names.Remove callId |> ignore
                Some tool
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
type OpenCodeAdapter private (client: OpenCodeClient, ownsClient: bool) =

    /// How long to wait before reconnecting a stream that ended. Long enough not to
    /// spin against a daemon that is down, short enough that a restart is invisible.
    let reconnectDelayMs = 1000

    /// Enough for the tool calls in flight across every session at once; overrun
    /// costs an unnamed tool, not a lost event.
    let names = ToolNames 512

    let events = Broadcast<HarnessEvent>()
    let cancellation = new CancellationTokenSource()
    let gate = obj ()
    let mutable pumping = false

    let handle (frame: EventFrame) =
        match Mapping.observe frame with
        | Some(ToolNamed(callId, tool)) -> names.Remember(callId, tool)
        | Some(ToolCalled(endpoint, callId, input)) ->
            // No remembered name means we joined mid-call. The attribution is still
            // worth publishing — the router correlates a claim by its input — so the
            // tool is reported unnamed rather than the event dropped.
            let tool = names.Take callId |> Option.defaultValue ""
            events.Publish(AgentInvoked(endpoint, { Tool = tool; Input = input }))
        | Some(SessionDeleted endpoint) -> events.Publish(SessionEnded endpoint)
        | None -> ()

    let rec pump () =
        async {
            let! _ = client.ReadEvents(handle, cancellation.Token)

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
