/// Routing decisions and the outbox are pure data; the daemon owns delivery IO.
namespace Collab.Domain

open System

type Registration =
    { Id: PeerId
      ShortId: string
      Aliases: AgentName list
      Name: AgentName
      Scope: Scope
      Binding: Binding
      FirstSeen: DateTimeOffset }

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
    let ownsName name (registration: Registration) =
        AgentName.equivalent name registration.Name || registration.Aliases |> List.exists (AgentName.equivalent name)
    let byId (scope: Scope) (id: PeerId) (state: RouterState) =
        state.Registrations |> Map.toSeq |> Seq.map snd
        |> Seq.tryFind (fun r -> r.Id = id && Scope.key r.Scope = Scope.key scope)
    let lookup (scope: Scope) (name: AgentName) (state: RouterState) =
        match PeerId.tryParse (AgentName.value name) with
        | Some id -> byId scope id state
        | None when PeerAddress.tryParse (AgentName.value name) |> Option.isSome ->
            state.Registrations |> Map.toSeq |> Seq.map snd
            |> Seq.tryFind (fun r -> r.ShortId = (AgentName.value name).ToLowerInvariant() && Scope.key r.Scope = Scope.key scope)
        | None ->
            state.Registrations |> Map.toSeq |> Seq.map snd
            |> Seq.filter (fun r -> Scope.key r.Scope = Scope.key scope && ownsName name r)
            |> Seq.sortByDescending (fun r -> Binding.isLive r.Binding, r.FirstSeen)
            |> Seq.tryHead
    let boundTo (endpoint: Endpoint) (state: RouterState) =
        state.Registrations |> Map.toSeq |> Seq.map snd
        |> Seq.tryFind (fun r -> Binding.endpoint r.Binding = Some endpoint)

module Router =
    let roster (scope: Scope) (state: RouterState) =
        state.Registrations |> Map.toList |> List.map snd
        |> List.filter (fun r -> Scope.key r.Scope = Scope.key scope)
        |> List.sortBy (fun r -> AgentName.key r.Name)

    let private forPeer (registration: Registration) (mail: PendingMail) =
        Scope.key mail.Scope = Scope.key registration.Scope &&
        match mail.Envelope.ToPeer with
        | Some id -> id = registration.Id
        | None -> RouterState.ownsName mail.Envelope.To registration
    let private forAddress scope address state mail =
        match RouterState.lookup scope address state with
        | Some registration -> forPeer registration mail
        | None -> Scope.key mail.Scope = Scope.key scope && mail.Envelope.ToPeer.IsNone && AgentName.equivalent mail.Envelope.To address

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
            // A previously unknown name can become the sender's own alias on rename.
            // Return a failure notice instead of admitting the original as peer mail.
            let pending = state.Pending |> List.map (fun mail ->
                if mail.Purpose = PeerMessage && mail.Envelope.ToPeer.IsNone &&
                   mail.Envelope.FromPeer = Some registration.Id && forPeer registration mail then
                    notice now mail (Envelope.bounce now "the recipient name now belongs to the sender" mail.Envelope)
                else mail)
            let state = { state with Pending = pending }
            let backlog = state.Pending |> List.filter (forPeer registration)
            let ready = backlog |> List.takeWhile (fun mail ->
                match mail.Status with
                | Waiting retryAfter -> retryAfter <= now && mail.ExpiresAt > now
                | Delivering _ | Uncertain _ -> false)
            if List.isEmpty ready then state, []
            else
                let ids = ready |> List.map (fun m -> m.Envelope.Id) |> Set.ofList
                let pending = state.Pending |> List.map (fun mail ->
                    if Set.contains mail.Envelope.Id ids then { mail with Status = Delivering endpoint; Envelope = { mail.Envelope with ToPeer = Some registration.Id } }
                    else mail)
                let envelopes = pending |> List.filter (fun mail -> Set.contains mail.Envelope.Id ids) |> List.map _.Envelope
                let intent =
                    match envelopes with
                    | [ envelope ] -> PushTo(endpoint, envelope)
                    | _ -> Flush(endpoint, envelopes)
                { state with Pending = pending }, [ intent ]

    /// Rename a bound peer without changing its ID, aliases, timestamps or mailbox.
    let claim (now: DateTimeOffset) (name: AgentName) (endpoint: Endpoint) (state: RouterState) : RouterState * Intent list =
        let occupied =
            state.Registrations |> Map.toSeq |> Seq.map snd
            |> Seq.tryFind (fun r ->
                Scope.key r.Scope = Scope.key endpoint.Scope && Binding.isLive r.Binding && RouterState.ownsName name r)
        let current = RouterState.boundTo endpoint state
        if (PeerId.tryParse(AgentName.value name) |> Option.isSome) || (PeerAddress.tryParse(AgentName.value name) |> Option.isSome) then state, [ Decline(ReservedName name) ]
        elif occupied |> Option.exists (fun owner -> current |> Option.forall (fun self -> self.Id <> owner.Id)) then
            state, [ Decline(NameInUse name) ]
        else
            let registration =
                match current with
                | Some r when AgentName.equivalent r.Name name -> r
                | Some r ->
                    let aliases = r.Name :: r.Aliases |> List.filter (AgentName.equivalent name >> not) |> List.distinctBy AgentName.key
                    { r with Name = name; Aliases = aliases }
                | None ->
                    let reserved = state.Registrations |> Map.toSeq |> Seq.map snd |> Seq.collect (fun r ->
                        seq { yield r.ShortId; yield AgentName.key r.Name; yield! r.Aliases |> Seq.map AgentName.key }) |> Set.ofSeq
                    let shortId = PeerAddress.allocate PeerAddress.random reserved
                    let rec uniqueId () =
                        let id = PeerId.create()
                        if state.Registrations |> Map.exists (fun _ r -> r.Id = id) then uniqueId () else id
                    { Id = uniqueId (); ShortId = shortId; Name = name; Aliases = []; Scope = endpoint.Scope
                      Binding = Bound(endpoint, now); FirstSeen = now }
            if registration.Aliases.Length > 64 then state, [ Decline(AliasLimit name) ]
            else
                let state = expirePending now state
                let state = { state with Registrations = Map.add (PeerId.value registration.Id) registration state.Registrations }
                let state, delivery = prepareBacklog now endpoint state
                state, CompleteClaim(registration.Name, endpoint) :: delivery

    /// Every accepted send enters the outbox before IO, including bound recipients.
    let send (now: DateTimeOffset) (limits: Limits) (scope: Scope) (from: AgentName) (request: SendRequest) (state: RouterState) : RouterState * Intent list =
        let state = expirePending now state
        let bytes = System.Text.Encoding.UTF8.GetByteCount request.Body
        let peers = state.Pending |> List.filter (fun mail -> mail.Purpose = PeerMessage)
        let sender = RouterState.lookup scope from state
        let recipient = RouterState.lookup scope request.To state
        let mailboxPeers = peers |> List.filter (forAddress scope request.To state) |> List.length
        let self = match sender, recipient with Some a, Some b -> a.Id = b.Id | _ -> AgentName.equivalent from request.To
        if self then state, [ Decline(SelfAddressed request.To) ]
        elif recipient.IsNone && (PeerAddress.tryParse(AgentName.value request.To) |> Option.isSome) then
            state, [ Decline(UnknownPeerAddress request.To) ]
        elif recipient.IsNone && (PeerId.tryParse(AgentName.value request.To) |> Option.isSome) then
            state, [ Decline(UnknownPeerId(PeerId.tryParse(AgentName.value request.To) |> Option.get)) ]
        elif bytes > limits.MaxBodyBytes then state, [ Decline(MessageTooLarge(bytes, limits.MaxBodyBytes)) ]
        // Reserve half the bounded outbox for failure notices; uncertain peers may need one.
        elif peers.Length >= limits.MaxPending / 2 || state.Pending.Length >= limits.MaxPending - 1 then
            state, [ Decline(QueueFull "the router outbox is full") ]
        elif mailboxPeers >= limits.MaxMailboxPeers then
            state, [ Decline(QueueFull "the recipient mailbox is full") ]
        else
            let envelope =
                { Envelope.seal from now request with
                    FromPeer = sender |> Option.map _.Id
                    FromAddress = sender |> Option.map _.ShortId
                    ToPeer = recipient |> Option.map _.Id }
            let until = now + limits.ParkWindow
            let mail =
                { Envelope = envelope; Scope = scope; ParkedAt = now; ExpiresAt = until
                  Status = Waiting now; Purpose = PeerMessage }
            let alreadyWaiting = state.Pending |> List.exists (forAddress scope request.To state)
            let state = { state with Pending = state.Pending @ [ mail ] }
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
            let trees = [| "alder"; "birch"; "cedar"; "elm"; "hazel"; "maple"; "oak"; "pine"; "rowan"; "willow"; "aspen"; "beech"; "fir"; "holly"; "larch"; "spruce" |]
            let animals = [| "badger"; "fox"; "hare"; "heron"; "lynx"; "otter"; "owl"; "panda"; "robin"; "seal"; "stoat"; "swan"; "tiger"; "wolf"; "wren"; "yak" |]
            let prefix = match endpoint.Harness with OpenCode -> "oc2" | ClaudeCode -> "claude"
            let baseName = prefix + "-" + trees[int digest[0] % trees.Length] + "-" + animals[int digest[1] % animals.Length]
            let rec choose attempt =
                let raw = if attempt = 0 then baseName else baseName + "-" + string attempt
                let name = AgentName.create raw |> Result.defaultWith (fun e -> failwithf "%A" e)
                let taken = state.Registrations |> Map.toSeq |> Seq.map snd |> Seq.exists (fun r ->
                    Scope.key r.Scope = Scope.key endpoint.Scope && Binding.isLive r.Binding && RouterState.ownsName name r)
                if taken then choose (attempt + 1) else name
            claim now (choose 0) endpoint state

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
