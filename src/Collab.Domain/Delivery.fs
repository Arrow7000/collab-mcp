/// What happens when we try to deliver, and why we sometimes refuse.
///
/// The vocabulary here is deliberately not the harness's. "Parked" is ours and means
/// *the recipient has no live session*; it is unrelated to opencode's `queue`
/// delivery, which is how we implement `AtTurnBoundary`. Keeping the words distinct
/// stops the two ideas being conflated at call sites.
namespace Collab.Domain

open System

/// Refusals the router decides, before any harness is involved. Each is a rule from
/// DESIGN.md §8, made explicit so the sender is told *why* rather than just "failed".
type Refusal =
    /// Addressing yourself. Almost always a model error; refusing surfaces it.
    | SelfAddressed of AgentName
    /// Too many messages on this sender→recipient pair in the window.
    | RateLimited of retryAfter: TimeSpan
    /// Identical content to a recent message on the same pair.
    | DuplicateSuppressed of original: MessageId
    /// The sender has spent its allowance of interruptions.
    | InterruptBudgetExhausted of resetsAt: DateTimeOffset
    /// The recipient already has more parked mail than we will hold for it.
    | RecipientBacklogFull of recipient: AgentName * max: int
    /// The sender's own session could not perform what it is asking for, so carrying
    /// the request would launder a capability it does not hold.
    | WouldLaunderPermission of detail: string

/// Failures the harness reports. Distinct from `Refusal`: these are things that went
/// wrong, not rules we enforced.
type DeliveryFault =
    | SessionGone of Endpoint
    | HarnessUnreachable of detail: string
    | HarnessRejected of status: int * detail: string

/// The outcome of one `send`.
type DeliveryOutcome =
    /// Handed to the recipient's runtime. For `Interrupt` this means it landed
    /// mid-turn; for `AtTurnBoundary` that it is committed to arrive at the boundary.
    ///
    /// This is the outcome for an idle recipient too: an idle session is live, and
    /// delivering to it wakes it. Waking idle agents is the point of the system, not
    /// an edge case.
    | Delivered of at: DateTimeOffset
    /// Nothing is bound to the recipient's name: it has not started yet, its session
    /// has ended, or the name is simply wrong. Held until a session claims the name,
    /// then flushed in order.
    ///
    /// Parking is bounded. It exists because peers spawned together race — a sender is
    /// routinely ready before its partner has registered — and refusing there would
    /// force exactly the retry loops this design abolishes. But a name nobody ever
    /// claims must not swallow mail, so the hold has a deadline, after which the
    /// envelope is returned to its sender (`Intent.ReturnToSender`).
    ///
    /// Strictly about the *absence of a binding*. An idle session is not parked; it is
    /// `Delivered` to.
    | Parked of since: DateTimeOffset * expiresAt: DateTimeOffset
    | Refused of Refusal
    | Failed of DeliveryFault

module DeliveryOutcome =

    /// Whether the sender still has to do something about this message.
    ///
    /// `Parked` is false: the router has taken ownership and will either deliver it or
    /// hand it back. Telling a model it must act on a parked message is what produces
    /// retry loops.
    let senderMustAct (outcome: DeliveryOutcome) : bool =
        match outcome with
        | Delivered _ -> false
        | Parked _ -> false
        | Refused _ -> true
        | Failed _ -> true

    /// Human-facing sentence returned to the calling agent, so a model gets an
    /// actionable reason rather than an error code.
    let explain (outcome: DeliveryOutcome) : string = failwith "TODO"
