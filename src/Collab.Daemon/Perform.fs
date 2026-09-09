/// Turning the router's decisions into deliveries.
///
/// The router does not do IO and the adapters do not make decisions, so this is the
/// join. It contains no policy of its own: every branch here is a direct consequence of
/// which `Intent` it was handed.
namespace Collab.Daemon

open Collab.Domain

module Perform =

    /// Which adapter serves a harness. A function rather than a fixed adapter because a
    /// second one (Claude Code, P3) puts two harnesses in one namespace, and delivery
    /// must then follow the endpoint rather than the daemon's configuration.
    type Ports = HarnessKind -> HarnessPort option

    let private deliver (ports: Ports) (endpoint: Endpoint) (envelope: Envelope) =
        async {
            match ports endpoint.Harness with
            | Some port -> return! port.Deliver(endpoint, envelope)
            | None ->
                // Status 0 says no request was made: this is a routing mistake, not a
                // harness that refused us.
                return Error(HarnessRejected(0, $"no adapter is loaded for {endpoint.Harness}"))
        }

    /// Carry out one decision.
    ///
    /// Returns an outcome only where there is a caller waiting to hear it. A flush, a
    /// bounce and a binding change all happen on their own account: their audience has
    /// either already been told or is not the one that triggered them.
    let intent (ports: Ports) (clock: Clock) (intent: Intent) : Async<DeliveryOutcome option> =
        async {
            match intent with
            | PushTo(endpoint, envelope) ->
                match! deliver ports endpoint envelope with
                | Ok() -> return Some(Delivered(clock.Now()))
                | Error fault -> return Some(Failed fault)

            | Park(_, until) -> return Some(Parked(clock.Now(), until))

            | Decline refusal -> return Some(Refused refusal)

            | ReturnToSender(endpoint, envelope, reason) ->
                let! result = deliver ports endpoint (Envelope.bounce (clock.Now()) reason envelope)

                match result with
                | Ok() -> Log.write $"bounced to {AgentName.value envelope.From}: {reason}"
                | Error fault -> Log.write $"bounce to {AgentName.value envelope.From} failed: {fault}"

                return None

            | Flush(endpoint, envelopes) ->
                // In send order, and one at a time: a backlog that arrives shuffled
                // reads as a different conversation than the one that was held.
                for envelope in envelopes do
                    let! result = deliver ports endpoint envelope

                    match result with
                    | Ok() -> Log.write $"flushed to {AgentName.value envelope.To} from {AgentName.value envelope.From}"
                    | Error fault -> Log.write $"flush to {AgentName.value envelope.To} failed: {fault}"

                return None

            | CompleteClaim(name, endpoint) ->
                let (SessionId session) = endpoint.Session
                let (Scope directory) = endpoint.Scope
                Log.write $"bound {AgentName.value name} to {session} in {directory}"
                return None

            | ReleaseBinding name ->
                Log.write $"released {AgentName.value name}"
                return None
        }

    let all (ports: Ports) (clock: Clock) (intents: Intent list) : Async<DeliveryOutcome list> =
        async {
            let outcomes = ResizeArray<DeliveryOutcome>()

            for one in intents do
                let! outcome = intent ports clock one
                outcome |> Option.iter outcomes.Add

            return List.ofSeq outcomes
        }
