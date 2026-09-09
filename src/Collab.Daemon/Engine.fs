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
    | MClaim of AgentName * Endpoint * AsyncReplyChannel<Intent list>
    | MSend of Endpoint * SendRequest * AsyncReplyChannel<SendResult>
    | MRoster of Endpoint * AsyncReplyChannel<RosterView>
    | MObserved of HarnessEvent * AsyncReplyChannel<Intent list>
    | MExpire of AsyncReplyChannel<Intent list>

type Engine(clock: Clock, limits: Limits) =

    let agent =
        MailboxProcessor<Message>.Start(fun inbox ->
            let rec loop (state: RouterState) =
                async {
                    match! inbox.Receive() with
                    | MClaim(name, endpoint, reply) ->
                        let state', intents = Router.claim (clock.Now()) name endpoint state
                        reply.Reply intents
                        return! loop state'

                    | MSend(endpoint, request, reply) ->
                        // The sender is looked up, never taken from the request: the
                        // harness said which session called, and the registry says which
                        // name that session answers to (DESIGN.md §7).
                        match RouterState.boundTo endpoint state with
                        | None ->
                            reply.Reply Anonymous
                            return! loop state
                        | Some caller ->
                            let state', intents =
                                Router.send (clock.Now()) limits caller.Scope caller.Name request state

                            reply.Reply(Decided intents)
                            return! loop state'

                    | MRoster(endpoint, reply) ->
                        reply.Reply
                            { Everyone = Router.roster endpoint.Scope state
                              Caller = RouterState.boundTo endpoint state }

                        return! loop state

                    | MObserved(event, reply) ->
                        let state', intents = Router.observe event state
                        reply.Reply intents
                        return! loop state'

                    | MExpire reply ->
                        let state', intents = Router.expire (clock.Now()) state
                        reply.Reply intents
                        return! loop state'
                }

            loop RouterState.empty)

    member _.Claim(name: AgentName, endpoint: Endpoint) : Async<Intent list> =
        agent.PostAndAsyncReply(fun reply -> MClaim(name, endpoint, reply))

    member _.Send(endpoint: Endpoint, request: SendRequest) : Async<SendResult> =
        agent.PostAndAsyncReply(fun reply -> MSend(endpoint, request, reply))

    member _.Roster(endpoint: Endpoint) : Async<RosterView> =
        agent.PostAndAsyncReply(fun reply -> MRoster(endpoint, reply))

    member _.Observed(event: HarnessEvent) : Async<Intent list> =
        agent.PostAndAsyncReply(fun reply -> MObserved(event, reply))

    member _.Expire() : Async<Intent list> =
        agent.PostAndAsyncReply MExpire
