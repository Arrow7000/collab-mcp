/// Routing decisions and the outbox are pure data; the daemon owns delivery IO.
namespace Collab.Domain

open System

type Registration =
    { Name: AgentName
      Scope: Scope
      Binding: Binding
      FirstSeen: DateTimeOffset
      Provisional: bool }

type PendingStatus =
    | Waiting of retryAfter: DateTimeOffset
    | Delivering of Endpoint
    | Uncertain of endpoint: Endpoint * detail: string

type MailPurpose = PeerMessage | FailureNotice

/// Accepted mail stays here until admission is confirmed or a failure notice replaces it.
type PendingMail =
    { Envelope: Envelope
      Scope: Scope
      ParkedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset
      Status: PendingStatus
      Purpose: MailPurpose }

type RouterState =
    { Registrations: Map<string, Registration>
      Pending: PendingMail list }

type Limits =
    { ParkWindow: TimeSpan
      MaxBodyBytes: int
      MaxPending: int
      MaxMailboxPeers: int }

type Intent =
    | PushTo of endpoint: Endpoint * envelope: Envelope
    | Park of envelope: Envelope * until: DateTimeOffset
    | Decline of Refusal
    | CompleteClaim of name: AgentName * endpoint: Endpoint
    | ReleaseBinding of name: AgentName
    /// Each item is acknowledged separately. Stop after the first failed admission.
    | Flush of endpoint: Endpoint * envelopes: Envelope list

module Limits =
    let defaults: Limits =
        { ParkWindow = TimeSpan.FromSeconds 90.0
          MaxBodyBytes = 16 * 1024; MaxPending = 512; MaxMailboxPeers = 64 }

module RouterState =
    let private sep = string (char 0x1F)
    let empty: RouterState = { Registrations = Map.empty; Pending = [] }
    let key (scope: Scope) (name: AgentName) = Scope.key scope + sep + AgentName.key name
    let lookup (scope: Scope) (name: AgentName) (state: RouterState) = Map.tryFind (key scope name) state.Registrations
    let boundTo (endpoint: Endpoint) (state: RouterState) =
        state.Registrations |> Map.toSeq |> Seq.map snd
        |> Seq.tryFind (fun r -> Binding.endpoint r.Binding = Some endpoint)

module Router =
    let roster (scope: Scope) (state: RouterState) =
        state.Registrations |> Map.toList |> List.map snd
        |> List.filter (fun r -> Scope.key r.Scope = Scope.key scope)
        |> List.sortBy (fun r -> AgentName.key r.Name)

    let private forMailbox scope name (mail: PendingMail) =
        Scope.key mail.Scope = Scope.key scope && AgentName.equivalent mail.Envelope.To name

    let private release (endpoint: Endpoint) (state: RouterState) =
        let registrations, intents =
            state.Registrations
            |> Map.fold (fun (registrations, intents) key registration ->
                if Binding.endpoint registration.Binding = Some endpoint then
                    Map.add key { registration with Binding = Unbound } registrations,
                    ReleaseBinding registration.Name :: intents
                else registrations, intents) (state.Registrations, [])
        { state with Registrations = registrations }, List.rev intents

    let private notice now (mail: PendingMail) envelope =
        { Envelope = envelope
          Scope = mail.Scope
          ParkedAt = now
          ExpiresAt = now + Limits.defaults.ParkWindow
          Status = Waiting now
          Purpose = FailureNotice }

    /// Expiry never races a request in flight or treats uncertain admission as failure.
    /// Failure notices are retained for a returning sender but cannot bounce in a loop.
    let private expirePending now state =
        let dead, live = state.Pending |> List.partition (fun mail ->
            match mail.Status with
            | Waiting _ -> mail.ExpiresAt <= now
            | Delivering _ | Uncertain _ -> false)
        let notices = dead |> List.choose (fun mail ->
            match mail.Purpose with
            | FailureNotice -> None
            | PeerMessage ->
                Some(notice now mail (Envelope.bounce now "the delivery deadline expired" mail.Envelope)))
        { state with Pending = live @ notices }

    /// Serialize admissions per mailbox. An uncertain head blocks later messages;
    /// otherwise they could pass a message whose admission we cannot establish.
    let private prepareBacklog (now: DateTimeOffset) (endpoint: Endpoint) (state: RouterState) =
        let target = RouterState.boundTo endpoint state
        match target with
        | None -> state, []
        | Some registration ->
            let backlog = state.Pending |> List.filter (forMailbox registration.Scope registration.Name)
            let ready = backlog |> List.takeWhile (fun mail ->
                match mail.Status with
                | Waiting retryAfter -> retryAfter <= now && mail.ExpiresAt > now
                | Delivering _ | Uncertain _ -> false)
            if List.isEmpty ready then state, []
            else
                let ids = ready |> List.map (fun m -> m.Envelope.Id) |> Set.ofList
                let pending = state.Pending |> List.map (fun mail ->
                    if Set.contains mail.Envelope.Id ids then { mail with Status = Delivering endpoint }
                    else mail)
                let envelopes = ready |> List.map _.Envelope
                let intent =
                    match envelopes with
                    | [ envelope ] -> PushTo(endpoint, envelope)
                    | _ -> Flush(endpoint, envelopes)
                let k = RouterState.key registration.Scope registration.Name
                let registrations = Map.add k { registration with Provisional = false } state.Registrations
                { state with Pending = pending; Registrations = registrations }, [ intent ]

    /// Repeat hello preserves identity. Live collisions and renaming are refused.
    /// Expire overdue mail before binding so timer scheduling cannot extend deadlines.
    let claim (now: DateTimeOffset) (name: AgentName) (endpoint: Endpoint) (state: RouterState) : RouterState * Intent list =
        let bind state =
            let k = RouterState.key endpoint.Scope name
            let registration =
                { Name = name; Scope = endpoint.Scope; Binding = Bound(endpoint, now); Provisional = false
                  FirstSeen = RouterState.lookup endpoint.Scope name state
                              |> Option.map _.FirstSeen |> Option.defaultValue now }
            let state = { state with Registrations = Map.add k registration state.Registrations }
            let state, delivery = prepareBacklog now endpoint state
            state, CompleteClaim(name, endpoint) :: delivery
        match RouterState.boundTo endpoint state with
        | Some current when AgentName.equivalent current.Name name ->
            let k = RouterState.key current.Scope current.Name
            let state = { state with Registrations = Map.add k { current with Provisional = false } state.Registrations }
            let state, delivery = prepareBacklog now endpoint (expirePending now state)
            state, CompleteClaim(current.Name, endpoint) :: delivery
        | Some current when current.Provisional ->
            match RouterState.lookup endpoint.Scope name state with
            | Some { Binding = Bound _ } -> state, [ Decline(NameInUse name) ]
            | _ -> bind { state with Registrations = Map.remove (RouterState.key current.Scope current.Name) state.Registrations }
        | Some current -> state, [ Decline(AlreadyNamed(current.Name, name)) ]
        | None ->
            match RouterState.lookup endpoint.Scope name state with
            | Some { Binding = Bound _ } -> state, [ Decline(NameInUse name) ]
            | _ -> bind (expirePending now state)

    /// Every accepted send enters the outbox before IO, including bound recipients.
    let send (now: DateTimeOffset) (limits: Limits) (scope: Scope) (from: AgentName) (request: SendRequest) (state: RouterState) : RouterState * Intent list =
        let state = expirePending now state
        let bytes = System.Text.Encoding.UTF8.GetByteCount request.Body
        let peers = state.Pending |> List.filter (fun mail -> mail.Purpose = PeerMessage)
        let mailboxPeers = peers |> List.filter (forMailbox scope request.To) |> List.length
        if AgentName.equivalent from request.To then state, [ Decline(SelfAddressed request.To) ]
        elif bytes > limits.MaxBodyBytes then state, [ Decline(MessageTooLarge(bytes, limits.MaxBodyBytes)) ]
        // Reserve half the bounded outbox for failure notices; uncertain peers may need one.
        elif peers.Length >= limits.MaxPending / 2 || state.Pending.Length >= limits.MaxPending - 1 then
            state, [ Decline(QueueFull "the router outbox is full") ]
        elif mailboxPeers >= limits.MaxMailboxPeers then
            state, [ Decline(QueueFull "the recipient mailbox is full") ]
        else
            let envelope = Envelope.seal from now request
            let until = now + limits.ParkWindow
            let mail =
                { Envelope = envelope; Scope = scope; ParkedAt = now; ExpiresAt = until
                  Status = Waiting now; Purpose = PeerMessage }
            let alreadyWaiting = state.Pending |> List.exists (forMailbox scope request.To)
            let registrations =
                state.Registrations |> Map.map (fun _ r ->
                    if Scope.key r.Scope = Scope.key scope && (AgentName.equivalent r.Name from || AgentName.equivalent r.Name request.To)
                    then { r with Provisional = false } else r)
            let state = { state with Pending = state.Pending @ [ mail ]; Registrations = registrations }
            match RouterState.lookup scope request.To state with
            | Some { Binding = Bound(endpoint = endpoint) } when not alreadyWaiting ->
                prepareBacklog now endpoint state
            | _ -> state, [ Park(envelope, until) ]

    /// Stable session-derived names are defaults, never substitutes for requested names.
    let announce now (endpoint: Endpoint) (state: RouterState) =
        match RouterState.boundTo endpoint state with
        | Some _ -> state, []
        | None ->
            let (SessionId session) = endpoint.Session
            let digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes session)
            let raw = "oc2-" + Convert.ToHexString(digest).ToLowerInvariant().Substring(0, 16)
            let name = AgentName.create raw |> Result.defaultWith (fun e -> failwithf "%A" e)
            let next, intents = claim now name endpoint state
            let k = RouterState.key endpoint.Scope name
            match RouterState.lookup endpoint.Scope name next with
            | Some r when Binding.endpoint r.Binding = Some endpoint ->
                let provisional = not (next.Pending |> List.exists (forMailbox endpoint.Scope name))
                { next with Registrations = Map.add k { r with Provisional = provisional } next.Registrations }, intents
            | _ -> next, intents

    let observe (event: HarnessEvent) (state: RouterState) =
        match event with
        | AgentInvoked _ -> state, []
        | SessionEnded endpoint -> release endpoint state

    /// Only the matching active attempt may update a pending item.
    let completed (now: DateTimeOffset) (endpoint: Endpoint) (envelope: Envelope) (result: DeliveryResult) (state: RouterState) : RouterState =
        match state.Pending |> List.tryFind (fun mail ->
            mail.Envelope.Id = envelope.Id && mail.Status = Delivering endpoint) with
        | None -> state
        | Some mail ->
            let remove state =
                { state with Pending = state.Pending |> List.filter (fun p -> p.Envelope.Id <> envelope.Id) }
            let update status state =
                let pending =
                    state.Pending |> List.map (fun p ->
                        if p.Envelope.Id = envelope.Id then { p with Status = status } else p)
                { state with Pending = pending }
            match result with
            | Admitted -> remove state
            | Deferred -> update (Waiting now) state
            | NotAdmitted(SessionGone _) ->
                release endpoint state |> fst |> update (Waiting now)
            | NotAdmitted(HarnessUnreachable _) -> update (Waiting(now.AddSeconds 5.)) state
            | NotAdmitted(AdmissionUnknown detail) ->
                let state = update (Uncertain(endpoint, detail)) state
                match mail.Purpose with
                | FailureNotice -> state
                | PeerMessage ->
                    let warning =
                        Envelope.notice now
                            $"Delivery to '{AgentName.value envelope.To}' is uncertain: {detail}. It may already have arrived. Do not resend; report this for reconciliation."
                            envelope
                    { state with Pending = state.Pending @ [ notice now mail warning ] }
            | NotAdmitted(HarnessRejected(status, detail)) ->
                let state = remove state
                match mail.Purpose with
                | FailureNotice -> state
                | PeerMessage ->
                    let bounce = Envelope.bounce now $"runtime rejected admission ({status}): {detail}" envelope
                    { state with Pending = state.Pending @ [ notice now mail bounce ] }

    /// Timer and recovery drive retries; agents do not need to poll or resend.
    let expire now state : RouterState * Intent list =
        let state = expirePending now state
        state.Registrations |> Map.toList |> List.choose (fun (_, r) -> Binding.endpoint r.Binding)
        |> List.fold (fun (state, intents) endpoint ->
            let state, next = prepareBacklog now endpoint state
            state, intents @ next) (state, [])

    /// A read proves the same synthetic input is already owned by the harness.
    let reconciled (endpoint: Endpoint) (envelope: Envelope) (state: RouterState) =
        let pending =
            state.Pending |> List.filter (fun mail ->
                match mail.Status with
                | Uncertain(e, _) when e = endpoint && mail.Envelope.Id = envelope.Id -> false
                | _ -> true)
        { state with Pending = pending }
