# Grok Build: adapter feasibility investigation

This is source/documentation investigation, **not an implemented or live-tested
adapter**. Reviewed `xai-org/grok-build` at commit
[`37949780c144e37df692e3d669051a21fec24f20`](https://github.com/xai-org/grok-build/tree/37949780c144e37df692e3d669051a21fec24f20).

## Relevant surfaces

- [Agent mode](https://github.com/xai-org/grok-build/blob/37949780c144e37df692e3d669051a21fec24f20/crates/codegen/xai-grok-pager/docs/user-guide/15-agent-mode.md)
  exposes ACP through stdio and authenticated WebSockets. The server retains state
  across client reconnects. Its documented extensions include session management
  and session notifications.
- The [leader architecture](https://github.com/xai-org/grok-build/blob/37949780c144e37df692e3d669051a21fec24f20/crates/codegen/xai-grok-shell/src/leader/mod.rs)
  shares an agent runtime across TUI, IDE, and headless clients over local Unix
  sockets. It namespaces request IDs and tracks session ownership. This is a more
  promising route to a session already displayed in a TUI than launching an
  unrelated headless process.
- [Plugins](https://docs.x.ai/build/features/skills-plugins-marketplaces) bundle
  hooks, skills, agents, MCP, and LSP configuration. These configuration plugins
  should not be mistaken for Pi's in-process TypeScript extension API.
- [Hooks](https://github.com/xai-org/grok-build/blob/37949780c144e37df692e3d669051a21fec24f20/crates/codegen/xai-grok-pager/docs/user-guide/10-hooks.md)
  expose native session ID and workspace root and can register presence at startup.
  Passive session-start output is ignored: it is not an idle-message injection hook.

## Delivery and provenance questions

The [session command model](https://github.com/xai-org/grok-build/blob/37949780c144e37df692e3d669051a21fec24f20/crates/codegen/xai-grok-shell/src/session/commands.rs)
contains authoritative prompt queues, cancel-and-send, persistence acknowledgement
barriers, and parent-agent messaging. These are useful implementation signals, but
internal Rust commands are not automatically callable through a plugin.

The exposed [`x.ai/subagent/message` handler](https://github.com/xai-org/grok-build/blob/37949780c144e37df692e3d669051a21fec24f20/crates/codegen/xai-grok-shell/src/extensions/subagent_message.rs)
has queue/steer operations and explicit admission outcomes, including uncertain
admission. It is feature-gated and addresses an **owned child**, with human-message
handling. It does not establish unrestricted cross-harness peer messaging; we must
not reuse it by pretending an independent peer is a parent or user.

Ordinary ACP `session/prompt` describes user input. Prefixing a user prompt with a
peer label alone would not prove native provenance, correct UI representation, or
safe permission handling. The existing terminal must receive and display the same
session's updates, without stealing its permission callbacks or changing its policy.

## Recommended next experiment

Install a released Grok binary in an isolated configuration and verify:

1. Connection to the native leader or an explicitly configured server, with an
   existing TUI attached to the same session and its permissions unchanged.
2. Reliable attribution of MCP calls to native sessions. Hook registration alone
   is insufficient if a shared MCP process cannot identify subsequent callers.
3. A supported ingress for independent agent input with durable native provenance,
   rather than user impersonation or owned-child impersonation.
4. Idle wake-up, busy queue ordering, terminal streaming, positive admission
   evidence, disconnect/reconnect, and any unsupported urgency.

Grok Build merits the next feasibility spike. Its runtime/client split and explicit
admission machinery suggest a well-suited foundation, but the peer-ingress and
attribution contracts remain unverified. No Grok configuration was installed or
user account contacted during this source review.
