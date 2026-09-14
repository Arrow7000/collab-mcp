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

    /// A runtime notice retains the sender's mailbox without inventing a peer identity.
    let notice (now: DateTimeOffset) (text: string) (envelope: Envelope) : Envelope =
        { envelope with
            Id = MessageId(Guid.NewGuid())
            To = envelope.From
            Body = text
            Urgency = AtTurnBoundary
            SentAt = now }

    /// The only way to make an Envelope: `From` comes from the binding the router
    /// resolved, never from anything the sender claimed.
    let seal (from: AgentName) (now: DateTimeOffset) (request: SendRequest) : Envelope =
        { Id = MessageId(Guid.NewGuid())
          From = from
          To = request.To
          Body = request.Body
          Urgency = request.Urgency
          SentAt = now }

    /// A message handed back to its sender because nobody claimed the recipient's name.
    ///
    /// Addressed from the sender to itself, because there is no third party it could
    /// honestly come from: inventing a router identity would put a name in the project's
    /// namespace that an agent could then collide with. The reason and the original text
    /// both travel, so the sender sees what came back without having to remember what it
    /// sent.
    ///
    /// Always `AtTurnBoundary`: a message that has already waited out its park window is
    /// not worth interrupting anyone for.
    let bounce (now: DateTimeOffset) (reason: string) (envelope: Envelope) : Envelope =
        { Id = MessageId(Guid.NewGuid())
          From = envelope.From
          To = envelope.From
          Body = $"undeliverable to '{AgentName.value envelope.To}': {reason}\n\nyou wrote: {envelope.Body}"
          Urgency = AtTurnBoundary
          SentAt = now }
