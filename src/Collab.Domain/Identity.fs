/// Who agents are, and where they currently live.
///
/// An identity outlives any session: a name is a mailbox, not a process. Sessions
/// bind to a name and later unbind; the name persists across restarts.
namespace Collab.Domain

open System

/// A name chosen by the user or the agent. Never rewritten by us: an invalid or
/// colliding name is an error, not a silent substitution (DESIGN.md §7).
type AgentName = private AgentName of string

/// Why a proposed name was rejected. Distinct cases so the caller can say something
/// useful rather than "invalid name".
type NameError =
    | Empty
    | TooLong of length: int * max: int
    | IllegalCharacters of offending: string

/// The agent runtimes we can deliver into. Each has its own attribution mechanism
/// and its own delivery API; both live behind `HarnessPort`.
///
/// Only runtimes we actually support belong here, so that exhaustive matches stay
/// meaningful. Others are added when their adapter is.
type HarnessKind =
    | OpenCode
    | ClaudeCode

/// A harness-assigned conversation handle, opaque to the domain. We never parse it;
/// only the owning adapter understands its shape.
type SessionId = SessionId of string

/// A live destination: a session, in a harness, that we can deliver to.
type Endpoint = { Harness: HarnessKind; Session: SessionId }

/// Where a name currently points. `Unbound` is a first-class state, not an error:
/// mail for an unbound name parks until a session claims it (DESIGN.md §6).
///
/// A name binds to at most one endpoint. Where an agent "lives" is the harness's
/// concern, and session ids do not collide across directories, so a second concurrent
/// worker is a second name rather than a second binding.
///
/// `Bound` says nothing about whether the session is currently executing. A session
/// that has finished its turn is still bound, and is still deliverable — waking it is
/// the point (see Delivery.fs on `Parked`).
type Binding =
    | Unbound
    | Bound of endpoint: Endpoint * since: DateTimeOffset

module AgentName =

    [<Literal>]
    let MaxLength = 64

    /// Total constructor. Rejects rather than repairs.
    let create (raw: string) : Result<AgentName, NameError> = failwith "TODO"

    let value (AgentName n) : string = n

    /// Case-insensitive, so `redstone` and `RedStone` cannot both be claimed.
    let equivalent (a: AgentName) (b: AgentName) : bool = failwith "TODO"

module Binding =

    let endpoint (binding: Binding) : Endpoint option = failwith "TODO"

    let isLive (binding: Binding) : bool = failwith "TODO"
