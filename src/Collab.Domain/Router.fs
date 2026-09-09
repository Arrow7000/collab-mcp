/// The router's own state and decisions, as data.
///
/// Everything here is pure: `decide` maps (state, request) to an intent, and the
/// daemon is left to perform it. That keeps the rules from DESIGN.md §8 — rate limits,
/// duplicate suppression, interrupt budgets, permission isolation — testable without a
/// harness, a socket or a clock.
namespace Collab.Domain

open System

/// What the router knows about one identity, from the moment it is minted.
///
/// A Registration exists before any agent does: enlisting creates it, claiming binds
/// it. That ordering is what lets an unminted name be rejected outright while a minted
/// one parks.
type Registration =
    { Name: AgentName
      /// Proves entitlement to this name. Never leaves the router except to whoever
      /// enlisted the agent, who passes it to the agent itself.
      Token: ClaimToken
      Binding: Binding
      /// Set when an agent has presented a valid token but we have not yet seen which
      /// session it spoke from. Resolved by the next `AgentInvoked` event.
      PendingClaim: ClaimToken option
      EnlistedAt: DateTimeOffset }

/// Mail held for a name with no live session. Ordered, so a session that binds
/// receives a backlog in the order it was sent.
///
/// `ExpiresAt` bounds the hold: peers spawned together race, so parking is how a
/// sender that is ready first avoids retrying, but a name nobody claims must not
/// swallow mail indefinitely.
type ParkedMail =
    { Envelope: Envelope
      ParkedAt: DateTimeOffset
      ExpiresAt: DateTimeOffset }

/// One past send on a sender→recipient pair. Carries the content key so duplicate
/// suppression does not need the original envelope.
type RecentSend =
    { At: DateTimeOffset
      Id: MessageId
      Content: string }

/// The router's whole state. A value, so it can be rebuilt by replaying the log.
type RouterState =
    { Registrations: Map<string, Registration>
      Parked: ParkedMail list
      /// Recent traffic per sender→recipient pair, for rate limiting and duplicate
      /// suppression. Trimmed against the clock rather than growing without bound.
      Recent: Map<string, RecentSend list>
      /// When each sender spent an interruption, so the budget window can slide
      /// rather than reset on a fixed schedule.
      Interrupts: Map<string, DateTimeOffset list> }

/// The limits from DESIGN.md §8, in one place so they can be tuned as data.
type Limits =
    { /// How long undeliverable mail is held before being returned to its sender.
      /// Sized for a peer that is still booting, not for one that never arrives.
      ParkWindow: TimeSpan
      MaxPerPairPerWindow: int
      PairWindow: TimeSpan
      DuplicateWindow: TimeSpan
      InterruptsPerWindow: int
      InterruptWindow: TimeSpan
      MaxParkedPerRecipient: int }

/// What the router has decided to do. The daemon performs these; the domain never
/// touches IO.
type Intent =
    /// Push now to a live endpoint.
    | PushTo of endpoint: Endpoint * envelope: Envelope
    /// Hold: nothing is bound to the recipient's name yet.
    | Park of envelope: Envelope * until: DateTimeOffset
    /// The hold expired without anyone claiming the name. Wake the *sender* and tell
    /// it, so an unroutable message surfaces through the same push path as any other
    /// message rather than vanishing.
    | ReturnToSender of envelope: Envelope * reason: string
    /// Tell the sender no, with a reason it can act on.
    | Decline of Refusal
    /// Hand a freshly minted identity back to whoever enlisted it.
    | Enlisted of name: AgentName * token: ClaimToken
    /// Bind a name to the session that just proved it owns it.
    | CompleteClaim of name: AgentName * endpoint: Endpoint
    /// Release a binding whose session has gone, re-parking anything undelivered.
    | ReleaseBinding of name: AgentName
    /// Deliver a parked backlog, in send order, to a session that has just bound.
    /// Binding is the only trigger: an already-bound session is delivered to directly,
    /// idle or not.
    | Flush of endpoint: Endpoint * envelopes: Envelope list

module Limits =
    /// Conservative starting point; tuned once we have real traffic.
    let defaults: Limits = failwith "TODO"

module RouterState =

    let empty: RouterState = failwith "TODO"

    let lookup (name: AgentName) (state: RouterState) : Registration option = failwith "TODO"

    /// Drop expired entries from `Recent` and `Interrupts`, and expire overdue parked
    /// mail. Called on each decision so the maps stay bounded without a background
    /// sweeper; the returned intents carry anything that has to be handed back.
    let evict
        (now: DateTimeOffset)
        (limits: Limits)
        (state: RouterState)
        : RouterState * Intent list =
        failwith "TODO"

module Router =

    /// Mint an identity. Called before the agent exists — by a lead about to spawn
    /// subagents, or by a human at the CLI setting up a run of top-level peers. The
    /// name becomes addressable immediately, so a peer that starts first can send to
    /// it and have the message park rather than bounce.
    let enlist
        (now: DateTimeOffset)
        (name: AgentName)
        (token: ClaimToken)
        (state: RouterState)
        : Result<RouterState * Intent list, Refusal> =
        failwith "TODO"

    /// An agent presents its token to take up its identity. The claim is not bound
    /// until an `AgentInvoked` event says which session it came from: the token
    /// establishes *who*, the event establishes *where* (DESIGN.md §7).
    let claim
        (now: DateTimeOffset)
        (name: AgentName)
        (token: ClaimToken)
        (state: RouterState)
        : Result<RouterState * Intent list, Refusal> =
        failwith "TODO"

    /// The main decision. Total in the outcome: every rejection path is a `Refusal`
    /// the sender can be told about.
    let send
        (now: DateTimeOffset)
        (limits: Limits)
        (from: AgentName)
        (request: SendRequest)
        (state: RouterState)
        : RouterState * Intent list =
        failwith "TODO"

    /// Fold a harness fact into the state. This is where a pending claim is matched
    /// to its session (flushing anything parked for that name), and where a dead
    /// session's binding is released.
    let observe
        (now: DateTimeOffset)
        (event: HarnessEvent)
        (state: RouterState)
        : RouterState * Intent list =
        failwith "TODO"
