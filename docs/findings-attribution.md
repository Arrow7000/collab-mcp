# OC2 attribution investigation — 2026-09-14

## Installed beta

The installed `opencode2 v0.0.0-beta-17519` does not pass session context through its
MCP call path. Its embedded JavaScript invokes the MCP client with name/arguments,
signal, timeout, and a progress callback; no session `_meta` is supplied. Inspection
locations: service call around byte 85311808, tool invocation around 89472967, and
MCP transport wrapper around 94142523.

The transport wrapper contains:

```javascript
i.callTool({name:h.name,arguments:h.args??{}},Le,
  {signal:d,timeout:p,onprogress:()=>{}})
```

This agrees with the earlier empirical probe. It supplies no identifier shared with
the session progress event. A JSON-RPC request ID or progress token cannot be treated
as a session identifier without evidence connecting it to that event.

## Current V2 sources and documentation

[Official V2 MCP documentation](https://opencode.ai/v2/docs/mcp-servers#context)
documents `params._meta.sessionID` for direct and Code Mode calls. Session context is
outside model-visible arguments and is not authentication on its own.

Current V2 branch at `dac296f963cfe9f10d9dc1a86d8640e5e239eb7d` instead uses the key
`ai.opencode/sessionID`:

- [Tool invocation](https://github.com/anomalyco/opencode/blob/dac296f963cfe9f10d9dc1a86d8640e5e239eb7d/packages/core/src/tool/mcp.ts#L65)
  passes the tool context's session ID into `mcp.callTool`.
- [MCP service](https://github.com/anomalyco/opencode/blob/dac296f963cfe9f10d9dc1a86d8640e5e239eb7d/packages/core/src/mcp/index.ts#L628)
  carries that session ID into the connection call.
- [Transport client](https://github.com/anomalyco/opencode/blob/dac296f963cfe9f10d9dc1a86d8640e5e239eb7d/packages/core/src/mcp/client.ts#L274)
  adds the namespaced `_meta` key to the MCP request.

The docs/source mismatch is why both keys are accepted. This was read-only source
inspection, not a demonstration against a newer installed release. No harness was
upgraded and no live session was prompted or reconfigured.

## Implementation decision

The shim forwards MCP `_meta` separately from tool arguments. The daemon reads either
session key and constrains attribution to a progress fact from that session with the
same verb and arguments. Project scope still comes from the harness event, rather
than from a model argument or guessed directory. Conflicting or malformed context
is refused without falling back to the legacy join.

Progress observations are restricted to the configured, supported server key `collab`.
Arbitrary server aliases are currently unsupported; previously accepting every server's
suffix could mix another MCP server's facts into attribution. Dedupe identity includes
the endpoint's session and project as well as assistant step/call/index.

Session metadata is now mandatory following the user's compatibility decision.
Missing context is refused before waiting for a fact or changing state. The attribution
API requires a session, and argument-only matching has been removed. The installed beta
is therefore unsupported for collaboration tools. This prevents the known cross-session
swap but does not recover missing/non-durable progress events or eliminate cold-start gaps.

## Next verification

The isolated 2.0.3 checks below verify both invocation paths. Remaining checks include
concurrent identical calls across real projects, disconnect/reconnect, and operational
installation migration. The shared live service has not been upgraded.

## Isolated OC2 2.0.3 verification

Downloaded the official `@opencode/cli-darwin-arm64@2.0.3` package into temporary
storage and verified its SHA-512 npm integrity. Used separate XDG config/data/state/cache
roots and a private authenticated server. A local scripted OpenAI-compatible provider
returned a tool call; no paid provider or credentials were used.

Both direct (`collab_hello`) and Code Mode (`tools.collab.hello`) calls supplied the
correct `_meta.sessionID` to a capturing MCP server. Code Mode emitted
`session.tool.progress` with `metadata.toolCalls` containing the running `collab.hello`
call and its arguments, plus the matching session and project directory. With the real
MCP shim and a separate durable daemon, `hello` matched its fact and committed a bound
registration. The shared installed binary/service was not upgraded.

V2 config now uses `mcp.servers.collab`; use `codemode:true` (the default). `disabled`
replaces the old `enabled` field. Direct mode emits `session.tool.input.started` carrying
`name:collab_hello`, followed by `session.tool.called` with arguments but without a name;
it emits no MCP progress fact. The adapter now keeps bounded, scoped name context
from the started event and consumes it when the called event arrives. Other servers'
names and unknown collab tools are excluded. The real shim/daemon direct-mode check
also persisted the expected registration.

On macOS use a canonical project path for isolated tests: `/tmp` aliases `/private/tmp`.
The first probe's MCP registry lookup under the alias returned an empty list; using the
canonical path and explicitly registering the server resolved it.

Sources: [official install guidance](https://opencode.ai/v2/docs),
[MCP config](https://opencode.ai/v2/docs/mcp-servers),
[custom local provider setup](https://opencode.ai/v2/docs/providers).

## Shared installation upgrade

With user approval, replaced `~/.opencode/bin/opencode2` with the verified 2.0.3
binary, migrated global MCP entries under `mcp.servers`, and restarted the shared
OC2 service and collaboration daemon. The separate `opencode` V1 binary was preserved.
All 50 previously visible session IDs remained visible. `collab` and `lean-lsp` report
connected; the configured local `agent-mail` endpoint reports unreachable.

Binary, config, and a consistent SQLite database backup are stored privately at
`/Users/aron/.opencode/backups/collab-upgrade-20260914-165630`. Existing terminals still run their loaded old binary and need relaunching
to use metadata-bearing tool calls.

## Automatic registration verification

OC2 2.0.3 session lifecycle observations now trigger an authenticated check of the
project MCP registry. Connected collab sessions receive stable generated default names;
disabled or absent collab servers do not register. The bootstrap integration check
created two sessions and confirmed distinct durable names before any model request
or hello call, then confirmed that a collab-disabled project stayed unregistered.
Scoped roster/send calls repair missed lifecycle registrations without requiring hello.

Default names are provisional until an explicit hello or any accepted message from/to
them. Explicit naming can replace an unused default; after communication, the name is
fixed to preserve peer continuity and queued addressing. Stable peer IDs and safe
mid-conversation rename semantics are a subsequent design question raised by the user.
The live daemon was updated and both existing explicit peer identities were preserved.

### Durable identity and renaming

The provisional-name restriction above is superseded by durable peer IDs. A scoped
endpoint retains one ID across renaming and daemon restart; prior names are reserved
aliases while live. Messages include sender IDs, and recipient IDs pin accepted mail
so name changes cannot redirect it. A new session reusing a released name has a new
identity and cannot inherit old pinned mail. The isolated real OC2 identity check
verifies renaming and delivery through both the old name and ID.
