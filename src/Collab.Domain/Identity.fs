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
type HarnessKind =
    | OpenCode
    | ClaudeCode
    | Codex

/// A harness-assigned conversation handle, opaque to the domain. We never parse it;
/// only the owning adapter understands its shape.
type SessionId = SessionId of string

/// A live destination: a session, in a harness, that we can deliver to.
type Endpoint = { Harness: HarnessKind; Session: SessionId }

/// Where a name currently points. `Unbound` is a first-class state, not an error:
/// mail for an unbound name parks until a session claims it (DESIGN.md §6).
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
