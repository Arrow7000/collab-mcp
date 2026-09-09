/// The router's own state and decisions, as data.
///
/// Everything here is pure: `decide` maps (state, request) to an intent, and the
/// daemon is left to perform it. That keeps the rules from DESIGN.md §8 — rate limits,
/// duplicate suppression, interrupt budgets, permission isolation — testable without a
/// harness, a socket or a clock.
namespace Collab.Domain

open System

/// What the router knows about one identity.
type Registration =
    { Name: AgentName
      Binding: Binding
      /// Set when the agent has announced itself but we have not yet matched the
      /// announcement to a session. Resolved by the next matching `AgentInvoked`.
      PendingClaim: ToolInvocation option
      FirstSeen: DateTimeOffset }

/// Mail held for a name with no live session. Ordered, so a session that binds
/// receives a backlog in the order it was sent.
type Parked = { Envelope: Envelope; ParkedAt: DateTimeOffset }

/// The router's whole state. A value, so it can be rebuilt by replaying the log.
type RouterState =
    { Registrations: Map<string, Registration>
      Parked: Parked list
      /// Recent traffic per sender→recipient pair, for rate limiting and duplicate
      /// suppression. Trimmed against the clock rather than growing without bound.
      Recent: Map<string, (DateTimeOffset * MessageId) list>
      /// Interruptions spent per sender in the current window.
      InterruptsSpent: Map<string, int> }

/// The limits from DESIGN.md §8, in one place so they can be tuned as data.
type Limits =
    { MaxPerPairPerWindow: int
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
    /// Hold: the recipient exists but has no live session.
    | Park of envelope: Envelope
    /// Tell the sender no, with a reason it can act on.
    | Decline of Refusal
    /// Bind a name to the session that just proved it owns it.
    | CompleteClaim of name: AgentName * endpoint: Endpoint
    /// Release a binding whose session has gone, re-parking anything undelivered.
    | ReleaseBinding of name: AgentName
    /// Deliver a backlog to a session that has just bound or gone idle.
    | Flush of endpoint: Endpoint * envelopes: Envelope list

module Limits =
    /// Conservative starting point; tuned once we have real traffic.
    let defaults: Limits = failwith "TODO"

module RouterState =

    let empty: RouterState = failwith "TODO"

    let lookup (name: AgentName) (state: RouterState) : Registration option = failwith "TODO"

    /// Drop expired entries from `Recent` and `InterruptsSpent`. Called on each
    /// decision so the maps stay bounded without a background sweeper.
    let evict (now: DateTimeOffset) (limits: Limits) (state: RouterState) : RouterState =
        failwith "TODO"

module Router =

    /// An agent announces which identity it is speaking as. The claim is not trusted
    /// until an `AgentInvoked` event attributes it to a session, which is what stops
    /// an agent claiming a name it does not own (DESIGN.md §7).
    let claim
        (now: DateTimeOffset)
        (name: AgentName)
        (invocation: ToolInvocation)
        (state: RouterState)
        : RouterState * Intent list =
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
    /// to its session, and where going idle releases parked mail.
    let observe
        (now: DateTimeOffset)
        (event: HarnessEvent)
        (state: RouterState)
        : RouterState * Intent list =
        failwith "TODO"
