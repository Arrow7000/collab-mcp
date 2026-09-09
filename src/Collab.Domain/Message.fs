/// What agents send each other.
///
/// Deliberately small: the router never inspects `Body` to make routing decisions
/// (DESIGN.md §5), so the body is opaque text and nothing branches on it.
namespace Collab.Domain

open System

type MessageId = MessageId of Guid

/// Ties a reply to the message that prompted it. Request/response is not a delivery
/// class — it is an ordinary message plus this correlation (DESIGN.md §6).
type ConversationId = ConversationId of Guid

/// How soon the recipient should see this.
///
/// The sender states intent; the runtime chooses the mechanism. These are *our*
/// names — the mapping onto any harness's vocabulary belongs to its adapter.
type Urgency =
    /// Hold until the recipient finishes what it is doing. The default, and the
    /// right choice for almost everything: it never derails work in flight.
    | AtTurnBoundary
    /// Interrupt the recipient mid-turn. Rationed by an interrupt budget, because
    /// if anything can interrupt at will then nothing finishes (DESIGN.md §8).
    | Interrupt

/// A message in transit between two agents.
type Envelope =
    { Id: MessageId
      Conversation: ConversationId
      From: AgentName
      To: AgentName
      Body: string
      Urgency: Urgency
      /// Set when this message answers an earlier one, so the recipient's runtime
      /// can present it as a reply rather than as an unprompted interruption.
      InReplyTo: MessageId option
      SentAt: DateTimeOffset }

/// What the sender supplies. The router mints identity and timestamps, so an agent
/// cannot forge provenance.
type SendRequest =
    { To: AgentName
      Body: string
      Urgency: Urgency
      InReplyTo: MessageId option }

module Envelope =

    /// The only way to make an Envelope: the router stamps `From` from the binding it
    /// resolved, never from anything the sender claimed.
    let seal (from: AgentName) (now: DateTimeOffset) (request: SendRequest) : Envelope =
        failwith "TODO"

    /// Content identity used for duplicate suppression: same sender, recipient and
    /// body within a window is a repeat, regardless of MessageId (DESIGN.md §8).
    let contentKey (envelope: Envelope) : string = failwith "TODO"
