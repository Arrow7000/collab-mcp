/// The router's own state and decisions, as data.
///
/// Everything here is pure: a decision maps (state, input) to a new state plus intents,
/// and the daemon performs them. That keeps routing testable without a harness, a socket
/// or a real clock.
namespace Collab.Domain

open System

/// What the router knows about one identity.
///
/// Scoped: a name is unique within a project, not across the machine, so unrelated
/// repositories cannot collide on `RedStone`.
///
/// TODO(P2): carry `LastActive`, and let `roster` filter on it. Without that a
/// long-lived daemon accumulates every name ever used in a project, and an agent asking
/// who its peers are gets months of ghosts.
type Registration =
    { Name: AgentName
      Scope: Scope
      Binding: Binding
      /// Set when an agent has announced a name but we have not yet seen which session
      /// it spoke from. Resolved by the next matching `AgentInvoked` event.
      PendingClaim: ToolInvocation option
      FirstSeen: DateTimeOffset }

/// Mail held because nothing is bound to the recipient's name. Ordered, so a session
/// that binds receives its backlog in the order it was sent.
type ParkedMail =
    { Envelope: Envelope
      Scope: Scope
      ParkedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset }

/// The router's whole state. A value, so it can be rebuilt by replaying the log.
type RouterState =
    { Registrations: Map<string, Registration>
      Parked: ParkedMail list }

/// The one tunable that survived the cut to a minimal first version.
type Limits =
    { /// How long undeliverable mail is held before being returned to its sender.
      /// Sized for a peer that is still booting, not for one that never arrives.
      ParkWindow: TimeSpan }

/// What the router has decided to do. The daemon performs these; the domain never
/// touches IO.
type Intent =
    /// Push now to a bound endpoint.
    | PushTo of endpoint: Endpoint * envelope: Envelope
    /// Hold: nothing is bound to the recipient's name yet.
    | Park of envelope: Envelope * until: DateTimeOffset
    /// The hold expired without anyone claiming the name. Wake the *sender* and tell it,
    /// so an unroutable message surfaces through the same push path as any other rather
    /// than vanishing.
    | ReturnToSender of envelope: Envelope * reason: string
    /// Tell the sender no, with a reason it can act on.
    | Decline of Refusal
    /// Bind a name to the session that just proved it owns it.
    | CompleteClaim of name: AgentName * endpoint: Endpoint
    /// Release a binding whose session has gone.
    | ReleaseBinding of name: AgentName
    /// Deliver a parked backlog, in send order, to a session that has just bound.
    /// Binding is the only trigger: an already-bound session is delivered to directly,
    /// idle or not.
    | Flush of endpoint: Endpoint * envelopes: Envelope list

module Limits =

    /// Long enough for a subagent to boot and announce itself, short enough that a
    /// misaddressed message comes back while its sender still has the context to care.
    let defaults: Limits = { ParkWindow = TimeSpan.FromSeconds 90.0 }

module RouterState =

    /// Unit separator. Cannot occur in a name (see `AgentName`), so no two distinct
    /// scope/name pairs can collide on one key.
    let private sep = string (char 0x1F)

    let empty: RouterState = { Registrations = Map.empty; Parked = [] }

    /// Registrations are keyed by scope and name together; a lookup without a scope is
    /// meaningless.
    let key (scope: Scope) (name: AgentName) : string =
        Scope.key scope + sep + AgentName.key name

    let lookup (scope: Scope) (name: AgentName) (state: RouterState) : Registration option =
        Map.tryFind (key scope name) state.Registrations

module Router =

    /// Everyone the asking agent could talk to in its project.
    ///
    /// This is what lets peers find each other without their names being written into
    /// their prompts. Only the agent that starts *second* needs it: the first does not
    /// have to find anyone, because the second one finds it and sends, and push delivery
    /// wakes it. So discovery never has to wait or poll.
    let roster (scope: Scope) (state: RouterState) : Registration list =
        state.Registrations
        |> Map.toList
        |> List.map snd
        |> List.filter (fun r -> Scope.key r.Scope = Scope.key scope)
        |> List.sortBy (fun r -> AgentName.key r.Name)

    /// An agent announces the name it is speaking as.
    ///
    /// The claim is recorded but not bound: MCP cannot tell us which session called, so
    /// the binding waits for the `AgentInvoked` event reporting this same invocation
    /// (DESIGN.md §7). The name is reserved meanwhile, so mail can already park for it.
    let claim
        (now: DateTimeOffset)
        (scope: Scope)
        (name: AgentName)
        (invocation: ToolInvocation)
        (state: RouterState)
        : RouterState * Intent list =
        let k = RouterState.key scope name

        let registration =
            match Map.tryFind k state.Registrations with
            | Some existing -> { existing with PendingClaim = Some invocation }
            | None ->
                { Name = name
                  Scope = scope
                  Binding = Unbound
                  PendingClaim = Some invocation
                  FirstSeen = now }

        { state with Registrations = Map.add k registration state.Registrations }, []

    /// The main decision.
    let send
        (now: DateTimeOffset)
        (limits: Limits)
        (scope: Scope)
        (from: AgentName)
        (request: SendRequest)
        (state: RouterState)
        : RouterState * Intent list =
        if AgentName.equivalent from request.To then
            state, [ Decline(SelfAddressed request.To) ]
        else
            let envelope = Envelope.seal from now request

            match RouterState.lookup scope request.To state with
            | Some { Binding = Bound(endpoint = endpoint) } -> state, [ PushTo(endpoint, envelope) ]
            | _ ->
                let until = now + limits.ParkWindow

                let parked =
                    { Envelope = envelope
                      Scope = scope
                      ParkedAt = now
                      ExpiresAt = until }

                { state with Parked = state.Parked @ [ parked ] }, [ Park(envelope, until) ]

    /// Fold a harness fact into the state: match a pending claim to its session and
    /// flush anything waiting for it, or release a dead session's binding.
    let observe
        (now: DateTimeOffset)
        (event: HarnessEvent)
        (state: RouterState)
        : RouterState * Intent list =
        match event with
        | AgentInvoked(endpoint, invocation) ->
            let pending =
                state.Registrations
                |> Map.toList
                |> List.tryFind (fun (_, r) ->
                    Scope.key r.Scope = Scope.key endpoint.Scope
                    && r.PendingClaim = Some invocation)

            match pending with
            | None -> state, []
            | Some(k, registration) ->
                let bound =
                    { registration with
                        Binding = Bound(endpoint, now)
                        PendingClaim = None }

                let mine, others =
                    state.Parked
                    |> List.partition (fun p ->
                        Scope.key p.Scope = Scope.key endpoint.Scope
                        && AgentName.equivalent p.Envelope.To registration.Name)

                let state2 =
                    { state with
                        Registrations = Map.add k bound state.Registrations
                        Parked = others }

                let flush =
                    if List.isEmpty mine then
                        []
                    else
                        [ Flush(endpoint, mine |> List.map (fun p -> p.Envelope)) ]

                state2, CompleteClaim(registration.Name, endpoint) :: flush

        | SessionEnded endpoint ->
            let bound =
                state.Registrations
                |> Map.toList
                |> List.tryFind (fun (_, r) ->
                    match r.Binding with
                    | Bound(endpoint = e) -> e = endpoint
                    | Unbound -> false)

            match bound with
            | None -> state, []
            | Some(k, registration) ->
                let released = { registration with Binding = Unbound }

                { state with Registrations = Map.add k released state.Registrations },
                [ ReleaseBinding registration.Name ]

    /// Hand back mail whose hold has run out. Driven by the clock rather than by a
    /// decision, because a parked message expires whether or not anyone is sending.
    let expire (now: DateTimeOffset) (state: RouterState) : RouterState * Intent list =
        let dead, live = state.Parked |> List.partition (fun p -> p.ExpiresAt <= now)

        { state with Parked = live },
        dead
        |> List.map (fun p ->
            ReturnToSender(
                p.Envelope,
                $"no agent claimed the name '{AgentName.value p.Envelope.To}' in time"
            ))
