/// The router's state, and the one thread that is allowed to change it.
///
/// `Router` is pure: it maps a state and an input to a new state and a list of intents.
/// This puts a single owner around it, so decisions are serialised and no two calls can
/// read the same registry and both act on it. Performing the intents is deliberately not
/// done here — that is IO, it is slow, and holding the state while it happens would make
/// one delivery block every other decision.
namespace Collab.Daemon

open Collab.Domain

/// The answer to a `send`, before anything has been performed.
type SendResult =
    | Decided of Intent list
    /// Nothing is bound to the calling session, so there is no sender to stamp on the
    /// envelope. The agent has not announced a name yet.
    | Anonymous

/// Who is asking, and who else is there.
type RosterView =
    { Everyone: Registration list
      /// The asker's own registration, so the answer can say which entry is theirs.
      /// `None` before it has announced a name.
      Caller: Registration option }

type private Message =
    | MAnnounce of Endpoint * AsyncReplyChannel<Result<Intent list, exn>>
    | MClaim of AgentName * Endpoint * AsyncReplyChannel<Result<Intent list, exn>>
    | MSend of Endpoint * SendRequest * AsyncReplyChannel<Result<SendResult, exn>>
    | MRoster of Endpoint * AsyncReplyChannel<RosterView>
    | MObserved of HarnessEvent * AsyncReplyChannel<Result<Intent list, exn>>
    | MExpire of AsyncReplyChannel<Result<Intent list, exn>>
    | MCompleted of Endpoint * Envelope * DeliveryResult * AsyncReplyChannel<Result<unit, exn>>
    | MReconciled of Endpoint * Envelope * AsyncReplyChannel<Result<unit, exn>>
    | MSnapshot of AsyncReplyChannel<RouterState>

type Engine(clock: Clock, limits: Limits, ?store: StateStore) =

    let commit action oldState nextState =
        try
            if oldState <> nextState then
                store |> Option.iter (fun s -> s.Save(nextState, clock.Now(), action))
            Ok nextState
        with error -> Error error

    let initial =
        let loaded = store |> Option.map (fun s -> s.Load()) |> Option.defaultValue RouterState.empty
        let recovered =
            let pending =
                loaded.Pending |> List.map (fun mail ->
                    match mail.Status with
                    | Delivering endpoint ->
                        { mail with Status = Uncertain(endpoint, "Daemon restarted during admission; delivery outcome is unknown.") }
                    | _ -> mail)
            { loaded with Pending = pending }
        match commit "restart recovery" loaded recovered with
        | Ok state -> state
        | Error error -> raise error

    let unwrap operation = async {
        let! result = operation
        return result |> Result.defaultWith raise
    }

    let agent =
        MailboxProcessor<Message>.Start(fun inbox ->
            let rec loop (state: RouterState) =
                async {
                    match! inbox.Receive() with
                    | MAnnounce(endpoint, reply) ->
                        let next, intents = Router.announce (clock.Now()) endpoint state
                        match commit "automatic registration" state next with
                        | Ok saved -> reply.Reply(Ok intents); return! loop saved
                        | Error error -> reply.Reply(Error error); return! loop state

                    | MClaim(name, endpoint, reply) ->
                        let state', intents = Router.claim (clock.Now()) name endpoint state
                        match commit "router transition" state state' with
                        | Ok saved -> reply.Reply(Ok intents); return! loop saved
                        | Error error -> reply.Reply(Error error); return! loop state

                    | MSend(endpoint, request, reply) ->
                        // The sender is looked up, never taken from the request: the
                        // harness said which session called, and the registry says which
                        // name that session answers to (DESIGN.md §7).
                        match RouterState.boundTo endpoint state with
                        | None ->
                            reply.Reply(Ok Anonymous)
                            return! loop state
                        | Some caller ->
                            let state', intents =
                                Router.send (clock.Now()) limits caller.Scope caller.Name request state

                            match commit "send accepted" state state' with
                            | Ok saved -> reply.Reply(Ok(Decided intents)); return! loop saved
                            | Error error -> reply.Reply(Error error); return! loop state

                    | MRoster(endpoint, reply) ->
                        reply.Reply
                            { Everyone = Router.roster endpoint.Scope state
                              Caller = RouterState.boundTo endpoint state }

                        return! loop state

                    | MObserved(event, reply) ->
                        let state', intents = Router.observe event state
                        match commit "router transition" state state' with
                        | Ok saved -> reply.Reply(Ok intents); return! loop saved
                        | Error error -> reply.Reply(Error error); return! loop state

                    | MExpire reply ->
                        let state', intents = Router.expire (clock.Now()) state
                        match commit "router transition" state state' with
                        | Ok saved -> reply.Reply(Ok intents); return! loop saved
                        | Error error -> reply.Reply(Error error); return! loop state

                    | MCompleted(endpoint, envelope, result, reply) ->
                        let state' = Router.completed (clock.Now()) endpoint envelope result state
                        match commit "delivery result" state state' with
                        | Ok saved -> reply.Reply(Ok()); return! loop saved
                        | Error error -> reply.Reply(Error error); return! loop state

                    | MReconciled(endpoint, envelope, reply) ->
                        let next = Router.reconciled endpoint envelope state
                        match commit "admission reconciled" state next with
                        | Ok saved -> reply.Reply(Ok()); return! loop saved
                        | Error error -> reply.Reply(Error error); return! loop state

                    | MSnapshot reply ->
                        reply.Reply state
                        return! loop state
                }

            loop initial)

    member _.Claim(name: AgentName, endpoint: Endpoint) : Async<Intent list> =
        agent.PostAndAsyncReply(fun reply -> MClaim(name, endpoint, reply)) |> unwrap

    member _.Send(endpoint: Endpoint, request: SendRequest) : Async<SendResult> =
        agent.PostAndAsyncReply(fun reply -> MSend(endpoint, request, reply)) |> unwrap

    member _.Roster(endpoint: Endpoint) : Async<RosterView> =
        agent.PostAndAsyncReply(fun reply -> MRoster(endpoint, reply))

    member _.Observed(event: HarnessEvent) : Async<Intent list> =
        agent.PostAndAsyncReply(fun reply -> MObserved(event, reply)) |> unwrap

    member _.Expire() : Async<Intent list> =
        agent.PostAndAsyncReply MExpire |> unwrap

    member _.Completed(endpoint: Endpoint, envelope: Envelope, result: DeliveryResult) : Async<unit> =
        agent.PostAndAsyncReply(fun reply -> MCompleted(endpoint, envelope, result, reply)) |> unwrap

    member _.Snapshot() : Async<RouterState> = agent.PostAndAsyncReply MSnapshot

    member _.Reconciled(endpoint: Endpoint, envelope: Envelope) : Async<unit> =
        agent.PostAndAsyncReply(fun reply -> MReconciled(endpoint, envelope, reply)) |> unwrap

    member _.Announce(endpoint: Endpoint) : Async<Intent list> =
        agent.PostAndAsyncReply(fun reply -> MAnnounce(endpoint, reply)) |> unwrap
