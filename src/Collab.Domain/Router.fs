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
    ///
    /// Carries the sender's endpoint rather than leaving it to be looked up: only the
    /// router knows where that name is bound, and a sender that has itself gone away in
    /// the meantime has nowhere to be told, which is a decision worth making here where
    /// it is testable rather than at the point of performing IO.
    | ReturnToSender of to': Endpoint * envelope: Envelope * reason: string
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

    let empty: RouterState =
        { Registrations = Map.empty; Parked = [] }

    /// Registrations are keyed by scope and name together; a lookup without a scope is
    /// meaningless.
    let key (scope: Scope) (name: AgentName) : string =
        Scope.key scope + sep + AgentName.key name

    let lookup (scope: Scope) (name: AgentName) (state: RouterState) : Registration option =
        Map.tryFind (key scope name) state.Registrations

    /// Which identity a session currently owns.
    ///
    /// This is the reverse of the lookup above, and it is how a call gets a sender: the
    /// harness says which session spoke, and this says which name that session answers
    /// to. An agent is therefore never asked who it is.
    let boundTo (endpoint: Endpoint) (state: RouterState) : Registration option =
        state.Registrations
        |> Map.toSeq
        |> Seq.map snd
        |> Seq.tryFind (fun r -> Binding.endpoint r.Binding = Some endpoint)

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

    /// An agent announces the name it wishes to be known by.
    ///
    /// The endpoint is not a parameter the agent could ever influence. MCP tells a
    /// server nothing about its caller, so the daemon recovers the calling session from
    /// the harness's own event stream before it gets here (Ports.fs), and by this point
    /// the session and its project are facts the harness reported. A name is the whole
    /// of what the agent contributes.
    ///
    /// Rebinding is deliberate rather than an error: an identity outlives a session, so
    /// a restarted agent announcing the same name takes its mailbox back, along with
    /// anything that parked while it was away.
    let claim
        (now: DateTimeOffset)
        (name: AgentName)
        (endpoint: Endpoint)
        (state: RouterState)
        : RouterState * Intent list =
        let scope = endpoint.Scope
        let k = RouterState.key scope name

        let registration =
            { Name = name
              Scope = scope
              Binding = Bound(endpoint, now)
              FirstSeen =
                state.Registrations
                |> Map.tryFind k
                |> Option.map (fun r -> r.FirstSeen)
                |> Option.defaultValue now }

        let mine, others =
            state.Parked
            |> List.partition (fun p ->
                Scope.key p.Scope = Scope.key scope && AgentName.equivalent p.Envelope.To name)

        let state' =
            { state with
                Registrations = Map.add k registration state.Registrations
                Parked = others }

        let flush =
            if List.isEmpty mine then
                []
            else
                [ Flush(endpoint, mine |> List.map (fun p -> p.Envelope)) ]

        state', CompleteClaim(name, endpoint) :: flush

    /// The main decision.
    ///
    /// `scope` and `from` are resolved by the daemon from the session that made the
    /// call, never read out of the request: an agent says who it is writing *to*, and
    /// nothing about who it is.
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

    /// Fold a harness fact into the state.
    ///
    /// Only one of the two facts changes routing. `AgentInvoked` exists so that a
    /// caller-blind MCP call can be attributed to a session, and that correlation is
    /// the daemon's: it is an artefact of how MCP is specified, not a rule about who
    /// may talk to whom, and by the time `claim` or `send` is called the answer is
    /// already an argument.
    let observe (event: HarnessEvent) (state: RouterState) : RouterState * Intent list =
        match event with
        | AgentInvoked _ -> state, []

        | SessionEnded endpoint ->
            match RouterState.boundTo endpoint state with
            | None -> state, []
            | Some registration ->
                let k = RouterState.key registration.Scope registration.Name
                let released = { registration with Binding = Unbound }

                { state with Registrations = Map.add k released state.Registrations },
                [ ReleaseBinding registration.Name ]

    /// Hand back mail whose hold has run out. Driven by the clock rather than by a
    /// decision, because a parked message expires whether or not anyone is sending.
    let expire (now: DateTimeOffset) (state: RouterState) : RouterState * Intent list =
        let dead, live = state.Parked |> List.partition (fun p -> p.ExpiresAt <= now)

        // A sender that has itself gone away cannot be told anything, so its mail is
        // simply dropped. Holding it further would only wait on a session that no
        // longer exists.
        let returns =
            dead
            |> List.choose (fun p ->
                RouterState.lookup p.Scope p.Envelope.From state
                |> Option.bind (fun r -> Binding.endpoint r.Binding)
                |> Option.map (fun sender ->
                    ReturnToSender(
                        sender,
                        p.Envelope,
                        $"no agent claimed the name '{AgentName.value p.Envelope.To}' in time"
                    )))

        { state with Parked = live }, returns
