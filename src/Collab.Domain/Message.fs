/// What agents send each other.
///
/// Deliberately small: the router never inspects `Body` to make routing decisions
/// (DESIGN.md §5), so the body is opaque text and nothing branches on it.
namespace Collab.Domain

open System

type MessageId = MessageId of Guid

/// How soon the recipient should see this.
///
/// The sender states intent; the runtime chooses the mechanism. These are *our* names —
/// mapping them onto any harness's vocabulary belongs to its adapter.
type Urgency =
    /// Hold until the recipient finishes what it is doing. The default, and the right
    /// choice for almost everything: it never derails work in flight.
    | AtTurnBoundary
    /// Interrupt the recipient mid-turn.
    | Interrupt

/// A message in transit between two agents.
///
/// There is no thread or reply-to here yet: the sender's name is in the rendered text,
/// which is all a recipient needs to answer. Threading is a nicety and can be added
/// without disturbing delivery.
type Envelope =
    { Id: MessageId
      From: AgentName
      To: AgentName
      Body: string
      Urgency: Urgency
      SentAt: DateTimeOffset }

/// What the sender supplies. The router stamps identity and time, so an agent cannot
/// forge provenance.
type SendRequest =
    { To: AgentName
      Body: string
      Urgency: Urgency }

module Envelope =

    /// The only way to make an Envelope: `From` comes from the binding the router
    /// resolved, never from anything the sender claimed.
    let seal (from: AgentName) (now: DateTimeOffset) (request: SendRequest) : Envelope =
        { Id = MessageId(Guid.NewGuid())
          From = from
          To = request.To
          Body = request.Body
          Urgency = request.Urgency
          SentAt = now }
