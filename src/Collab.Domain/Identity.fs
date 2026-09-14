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

/// The project two agents must share in order to reach each other.
///
/// Derived, never configured: opencode puts `location.directory` on every event, and
/// Claude Code exports `CLAUDE_PROJECT_DIR` to the servers it spawns. So there is no
/// workspace to create, name or enumerate — agents working the same directory are
/// peers, and agents in unrelated projects cannot collide on a name.
type Scope = Scope of directory: string

/// A live destination: a session, in a harness, working in a project.
type Endpoint =
    { Harness: HarnessKind
      Session: SessionId
      Scope: Scope }

/// Where a name currently points.
///
/// Agents choose their own names and announce them with `hello`, so `Unbound` covers
/// both "known agent, not ready yet" and "nobody has ever used that name". The router
/// cannot tell those apart at send time, which is exactly why an unbound recipient
/// parks under a deadline rather than being refused: the common case is a peer that is
/// still booting, and the deadline is what stops a misaddressed message vanishing.
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
        let original = if isNull raw then "" else raw

        if String.IsNullOrWhiteSpace original then
            Error Empty
        elif original.Length > MaxLength then
            Error(TooLong(original.Length, MaxLength))
        else
            let offending =
                original |> Seq.filter (isLegal >> not) |> Seq.distinct |> Seq.toArray |> String

            if offending.Length > 0 then
                Error(IllegalCharacters offending)
            else
                Ok(AgentName original)

    let value (AgentName n) : string = n

    /// Canonical form used for map keys and comparisons, so `redstone` and `RedStone`
    /// cannot both be claimed. The original casing is what we display.
    let key (name: AgentName) : string = (value name).ToLowerInvariant()

    let equivalent (a: AgentName) (b: AgentName) : bool = key a = key b

/// Public identity lasts for one peer lifetime, independent of its display names.
type PeerId = private PeerId of Guid

module PeerId =
    let create () = PeerId(Guid.NewGuid())
    let value (PeerId id) = "peer-" + id.ToString "N"
    let tryParse (raw: string) =
        if isNull raw || not (raw.StartsWith("peer-", StringComparison.OrdinalIgnoreCase)) then None
        else
            match Guid.TryParseExact(raw.Substring 5, "N") with
            | true, id -> Some(PeerId id)
            | _ -> None
    /// Deterministic migration makes repeated reads of a legacy snapshot agree.
    let legacy (raw: string) =
        let bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes raw)
        PeerId(Guid(bytes[0..15]))

/// Compact public address; the full PeerId remains the durable identity underneath.
module PeerAddress =
    let tryParse (raw: string) =
        if not (isNull raw) && raw.Length = 13 && raw.StartsWith("peer-", StringComparison.OrdinalIgnoreCase) &&
           raw.Substring(5) |> Seq.forall Uri.IsHexDigit then Some(raw.ToLowerInvariant())
        else None

    /// The caller supplies all reserved addresses/names, including inactive peers.
    let allocate (candidate: unit -> string) (reserved: Set<string>) =
        let rec next () =
            let address = candidate ()
            match tryParse address with
            | None -> invalidArg "candidate" "expected peer- followed by eight hexadecimal digits"
            | Some address when Set.contains address reserved -> next ()
            | Some address -> address
        next ()

    let random () = "peer-" + Guid.NewGuid().ToString("N").Substring(0, 8)

module Scope =

    /// Harness directories are absolute. Normalize syntax but preserve case so distinct
    /// projects on case-sensitive filesystems cannot be merged. Path aliases are separate.
    let key (Scope directory) : string =
        directory |> System.IO.Path.GetFullPath |> System.IO.Path.TrimEndingDirectorySeparator

module Binding =

    let endpoint (binding: Binding) : Endpoint option =
        match binding with
        | Unbound -> None
        | Bound(endpoint = e) -> Some e

    let isLive (binding: Binding) : bool =
        match binding with
        | Unbound -> false
        | Bound _ -> true
