/// What happens when we try to deliver, and why we sometimes refuse.
///
/// The vocabulary here is deliberately not the harness's. "Parked" is ours and means
/// *nothing is bound to the recipient's name*; it is unrelated to opencode's `queue`
/// delivery, which is how we implement `AtTurnBoundary`. Keeping the words distinct
/// stops the two ideas being conflated at call sites.
namespace Collab.Domain

open System

/// Refusals the router decides, before any harness is involved.
///
/// Only one for now. The rate limits, duplicate suppression and interrupt budgets in
/// DESIGN.md §8 are real requirements, but there is no traffic to protect until this
/// works end to end, and adding cases here later disturbs nothing.
type Refusal =
    /// Addressing yourself. Almost always a model error; refusing surfaces it.
    | SelfAddressed of AgentName

/// Failures the harness reports. Distinct from `Refusal`: these are things that went
/// wrong, not rules we enforced.
type DeliveryFault =
    | SessionGone of Endpoint
    | HarnessUnreachable of detail: string
    | HarnessRejected of status: int * detail: string

/// The outcome of one `send`.
type DeliveryOutcome =
    /// Handed to the recipient's runtime. For `Interrupt` this means it landed mid-turn;
    /// for `AtTurnBoundary` that it is committed to arrive at the boundary.
    ///
    /// This is the outcome for an idle recipient too: an idle session is live, and
    /// delivering to it wakes it. Waking idle agents is the point, not an edge case.
    | Delivered of at: DateTimeOffset
    /// Nothing is bound to the recipient's name in this scope: it has not announced
    /// itself yet, its session has ended, or the name is wrong. Held until a session
    /// claims the name, then flushed in order.
    ///
    /// Agents name themselves, so the router cannot tell "still booting" from
    /// "misaddressed" at send time, and must assume the former — peers spawned together
    /// race, and refusing there forces the retry loops this design exists to remove. The
    /// deadline is what stops the wrong assumption swallowing mail for ever.
    | Parked of since: DateTimeOffset * expiresAt: DateTimeOffset
    | Refused of Refusal
    | Failed of DeliveryFault

module DeliveryOutcome =

    /// Whether the sender still has to do something about this message.
    ///
    /// `Parked` is false: the router has taken ownership and will either deliver it or
    /// hand it back. Telling a model it must act on parked mail is what produces retry
    /// loops.
    let senderMustAct (outcome: DeliveryOutcome) : bool =
        match outcome with
        | Delivered _ -> false
        | Parked _ -> false
        | Refused _ -> true
        | Failed _ -> true
