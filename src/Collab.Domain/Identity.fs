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

/// Secret minted with a name and handed to whoever will run that agent. Presenting it
/// is what proves entitlement to the name.
///
/// This is why an agent cannot squat a peer's identity, and why binding does not have
/// to resort to comparing serialised tool arguments: the token is unguessable, so a
/// claim either matches exactly or is rejected.
type ClaimToken = ClaimToken of string

/// A live destination: a session, in a harness, that we can deliver to.
type Endpoint = { Harness: HarnessKind; Session: SessionId }

/// Where a name currently points.
///
/// Names are *minted* by the router before any session exists, so `Unbound` means
/// something precise: this agent has been enlisted but has not claimed its name yet,
/// or has claimed it and since gone. It never means "no idea who that is" — an
/// unminted name is rejected outright, which is only sound because the router is the
/// allocator (DESIGN.md §7).
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

    /// Letters, digits, hyphen and underscore. Deliberately narrow: names travel
    /// through JSON, map keys and log lines, and a name that needs quoting anywhere
    /// is a name that will eventually be mangled somewhere.
    let private isLegal (c: char) = Char.IsLetterOrDigit c || c = '-' || c = '_'

    /// Total constructor. Rejects rather than repairs.
    let create (raw: string) : Result<AgentName, NameError> =
        let trimmed = if isNull raw then "" else raw.Trim()

        if String.IsNullOrEmpty trimmed then
            Error Empty
        elif trimmed.Length > MaxLength then
            Error(TooLong(trimmed.Length, MaxLength))
        else
            let offending =
                trimmed |> Seq.filter (isLegal >> not) |> Seq.distinct |> Seq.toArray |> String

            if offending.Length > 0 then
                Error(IllegalCharacters offending)
            else
                Ok(AgentName trimmed)

    let value (AgentName n) : string = n

    /// Canonical form used for map keys and comparisons, so `redstone` and `RedStone`
    /// cannot both be claimed. The original casing is what we display.
    let key (name: AgentName) : string = (value name).ToLowerInvariant()

    let equivalent (a: AgentName) (b: AgentName) : bool = key a = key b

module Binding =

    let endpoint (binding: Binding) : Endpoint option =
        match binding with
        | Unbound -> None
        | Bound(endpoint = e) -> Some e

    let isLive (binding: Binding) : bool =
        match binding with
        | Unbound -> false
        | Bound _ -> true
