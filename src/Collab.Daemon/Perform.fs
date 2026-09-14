/// Execute delivery intents and acknowledge every admission to the engine.
namespace Collab.Daemon

open Collab.Domain

module Perform =
    type Ports = HarnessKind -> HarnessPort option

    let private deliver ports endpoint envelope =
        async {
            try
                match ports endpoint.Harness with
                | Some (port: HarnessPort) -> return! port.Deliver(endpoint, envelope)
                | None -> return Error(HarnessRejected(0, $"no adapter is loaded for {endpoint.Harness}"))
            with error ->
                // A throwing adapter cannot establish that admission did not happen.
                return Error(AdmissionUnknown error.Message)
        }

    let private report (engine: Engine) (clock: Clock) endpoint envelope result =
        async {
            do! engine.Completed(endpoint, envelope, result)
            match result with
            | Admitted -> return Delivered(clock.Now())
            | NotAdmitted(AdmissionUnknown _ as fault)
            | NotAdmitted(HarnessRejected _ as fault) -> return Failed fault
            | Deferred | NotAdmitted _ ->
                let! state = engine.Snapshot()
                match state.Pending |> List.tryFind (fun p -> p.Envelope.Id = envelope.Id) with
                | Some mail -> return Parked(mail.ParkedAt, mail.ExpiresAt)
                | None -> return Failed(HarnessRejected(0, "delivery state disappeared before its acknowledgement"))
        }

    let intent ports (clock: Clock) (engine: Engine) intent : Async<DeliveryOutcome list> =
        async {
            match intent with
            | PushTo(endpoint, envelope) ->
                let! result = deliver ports endpoint envelope
                let! outcome = report engine clock endpoint envelope
                                    (match result with Ok() -> Admitted | Error fault -> NotAdmitted fault)
                return [ outcome ]
            | Flush(endpoint, envelopes) ->
                let outcomes = ResizeArray<DeliveryOutcome>()
                let mutable stopped = false
                for envelope in envelopes do
                    let! result =
                        async {
                            if stopped then return Deferred
                            else
                                match! deliver ports endpoint envelope with
                                | Ok() -> return Admitted
                                | Error fault ->
                                    stopped <- true
                                    return NotAdmitted fault
                        }
                    let! outcome = report engine clock endpoint envelope result
                    outcomes.Add outcome
                return List.ofSeq outcomes
            | Park(envelope, until) -> return [ Parked(envelope.SentAt, until) ]
            | Decline refusal -> return [ Refused refusal ]
            | CompleteClaim(name, endpoint) ->
                let (SessionId session) = endpoint.Session
                let (Scope directory) = endpoint.Scope
                Log.write $"bound {AgentName.value name} to {session} in {directory}"
                return []
            | ReleaseBinding name ->
                Log.write $"released {AgentName.value name}"
                return []
        }

    let all ports clock engine intents : Async<DeliveryOutcome list> =
        async {
            let outcomes = ResizeArray<DeliveryOutcome>()
            for one in intents do
                let! next = intent ports clock engine one
                for outcome in next do outcomes.Add outcome
            return List.ofSeq outcomes
        }
