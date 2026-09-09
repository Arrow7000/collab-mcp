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
    /// No such name has ever been registered. Distinct from a name that is merely
    /// unbound, which parks instead of refusing.
    | UnknownRecipient of AgentName
    /// Addressing yourself. Almost always a model error; refusing surfaces it.
    | SelfAddressed of AgentName
    /// Too many messages on this sender→recipient pair in the window.
    | RateLimited of retryAfter: TimeSpan
    /// Identical content to a recent message on the same pair.
    | DuplicateSuppressed of original: MessageId
    /// The sender has spent its allowance of interruptions.
    | InterruptBudgetExhausted of resetsAt: DateTimeOffset
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
    | Delivered of at: DateTimeOffset
    /// The recipient is known but has no live session. Held until one binds.
    | Parked of since: DateTimeOffset
    | Refused of Refusal
    | Failed of DeliveryFault

module DeliveryOutcome =

    /// Whether the sender should consider the message its responsibility still.
    /// `Parked` is *not* a failure — the design promises delivery on bind.
    let isTerminal (outcome: DeliveryOutcome) : bool = failwith "TODO"

    /// Human-facing sentence returned to the calling agent, so a model gets an
    /// actionable reason rather than an error code.
    let explain (outcome: DeliveryOutcome) : string = failwith "TODO"
