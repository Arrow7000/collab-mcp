/// The boundary between our domain and any particular agent runtime.
///
/// Everything version-unstable lives on the far side of this file. The opencode
/// endpoints we depend on (`synthetic`, `steer`, the inbox routes) are v2-beta with
/// no stability guarantee (DESIGN.md §9), so the domain never names them: an adapter
/// maps them onto the vocabulary below and nothing above this line changes if they
/// move.
namespace Collab.Domain

open System

/// A tool call we observed an agent make, used to attribute an MCP call to the
/// session that made it.
///
/// This exists because MCP gives a server no way to identify its caller: `clientInfo`
/// carries no session, there are no session env vars, and the stdio process is shared
/// across sessions (docs/findings-oc2.md). For opencode the adapter recovers
/// attribution from the event bus, which reports both the session and the tool input.
/// For Claude Code it reads the session id from its own environment instead. Both
/// arrive here as the same fact.
type ToolInvocation = { Tool: string; Input: string }

/// What a harness tells us about its sessions.
///
/// Deliberately only two facts. There is no "went idle" event, because the router does
/// not track turn boundaries: every harness we support already implements that itself
/// — opencode chooses between `queue` and `steer` delivery internally, and Claude Code
/// delivers between tool calls or starts a new turn. Modelling turn state here would
/// duplicate the runtime and get it subtly wrong. The router only needs to know who is
/// speaking, and when a binding dies.
type HarnessEvent =
    /// An agent invoked a tool. Carries the endpoint, so a claim can be matched to
    /// the session that made it.
    | AgentInvoked of endpoint: Endpoint * invocation: ToolInvocation
    /// The session no longer exists. Its binding must be dropped and anything
    /// undelivered re-parked.
    | SessionEnded of endpoint: Endpoint
    /// Authenticated harness-wide deletion can arrive without project context.
    | SessionRemoved of harness: HarnessKind * session: SessionId

/// One agent runtime we can observe and deliver into.
///
/// Deliberately two verbs. Anything richer (waiting, interrupting, promoting a queued
/// item) is a detail of how an adapter implements `Deliver`, not a concept the router
/// should reason about.
type HarnessPort =

    abstract Kind: HarnessKind

    /// Push an envelope into a bound session, waking it if it is idle.
    ///
    /// Implementations must present it as distinct from a user turn, so provenance
    /// survives (DESIGN.md §3), and must honour `Urgency` using whatever the runtime
    /// provides rather than by holding the message here.
    abstract Deliver: endpoint: Endpoint * envelope: Envelope -> Async<Result<unit, DeliveryFault>>

    /// Long-lived stream of session lifecycle facts.
    ///
    /// Implementations must be resilient to gaps: opencode's SSE contract states that
    /// a slow consumer overflows and fails the stream, so an adapter is expected to
    /// reconnect and reconcile rather than assume it saw every event (DESIGN.md §9).
    abstract Observe: unit -> IObservable<HarnessEvent>

/// Time, injected so the router's windows and budgets are testable without waiting.
type Clock =
    abstract Now: unit -> DateTimeOffset
