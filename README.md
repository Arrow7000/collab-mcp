# collab-mcp

Peer-to-peer collaboration for coding agents across providers, with **push delivery**.

Agents address each other by name and are *woken* when mail arrives — including when
idle. There is no `fetch_inbox`, because the inbox belongs to the runtime, not to the
model.

- [`DESIGN.md`](DESIGN.md) — architecture, requirements, delivery semantics, and roadmap.
- [`docs/findings-oc2.md`](docs/findings-oc2.md) — verified notes on the opencode2
  process model that the design rests on. Read before touching the adapter.

## Agent-facing surface

Three verbs. An agent supplies only a name for itself, and a recipient and body when
writing. Its session and project are derived from the harness, never asked for.

| Verb | Meaning |
|---|---|
| `hello(name)` | Choose or change your display name |
| `roster()` | Who else is working in this project |
| `send(to, body, urgency)` | Write to a peer; `urgency` is at-turn-boundary or interrupt |

OC2 sessions with `collab` loaded register automatically with a stable generated
`oc2-maple-otter` style name. No prompt to call `hello` is needed. `roster` shows peers and identifies
your own entry, its short durable ID (for example `a1b2c3d4`), and previous names. `hello` can change the
display name at any time; the ID stays the same. Previous names remain reserved aliases
for that live peer. `send` accepts a current name, a previous name, or a peer ID.
Messages include the sender ID as a reliable reply address. Short IDs use eight random
hexadecimal digits and are checked for collisions before registration. The original
full IDs and earlier `peer-…` short addresses remain valid for compatibility.
Eight-character hex strings are reserved for IDs rather than display names. `oc2-`
identifies a generated OpenCode name, independent of model/provider. Generated names use two
session-derived words and add a numeric suffix when a live name is already taken.

Registration follows session lifecycle events; a scoped tool call also repairs a missed
registration. Cold-start gaps or reopening a conversation without a lifecycle event
may delay presence until its next execution/tool call. Historical sessions are not
registered wholesale.

Runtime session metadata is required on every tool call; project context comes from
matching harness events. `send` takes no `from`, and agents supply no session or
directory arguments. Missing, malformed or conflicting metadata is refused immediately.

## Using it

One line, and nothing to start by hand — the shim spawns the daemon on first use.

```bash
dotnet build
```

Add this to `~/.config/opencode/opencode.json` (or a project-local `opencode.json`).
Use an OC2 V2 build that supplies MCP `_meta.ai.opencode/sessionID` or `_meta.sessionID`.
The installed beta-17519 lacks this context and is unsupported for tool calls. Keep the
server key `collab`, which the adapter uses to isolate tool-progress events. OC2 2.0.3 was verified with the real shim and daemon in both tool modes.

```json
{ "mcp": { "servers": { "collab": { "type": "local", "codemode": true,
    "command": ["<repo>/src/Collab.Shim/bin/Debug/net10.0/collab-mcp"] } } } }
```

The daemon keeps its socket, lock, log and SQLite database under `~/.collab-mcp`.
Inspect an existing daemon without starting one:

```bash
<repo>/src/Collab.Shim/bin/Debug/net10.0/collab-mcp --status
```

Status reports the event-stream connection, bindings, and waiting/delivering/uncertain
outbox counts. The log records connection failures and recovery activity. Set
`COLLAB_MCP_HOME` to a separate directory for isolated runs.

## State of play

| Component | Status |
|---|---|
| `src/Collab.Domain` | P1 implemented; pure routing, parking and identity |
| `src/Collab.Adapters.OpenCode` | P1 implemented: synthetic delivery and SSE observation |
| `src/Collab.Daemon` | SQLite registry/outbox/audit, correlation, delivery acknowledgements, Unix socket |
| `src/Collab.Shim` | P1 implemented: three tools, spawns the daemon |
| Claude Code adapter | P3 |

`dotnet build` from the repo root builds all four runtime projects and the test project.
`dotnet test` runs identity ownership and attribution regression suites, including
concurrent claims through the daemon engine and session-scoped invocation matching.

Names belong to one live session in each project. Repeating `hello` with your current
name is harmless and preserves its spelling. Names and aliases owned by a different
live session are refused. IDs survive renaming, terminal reconnection, and daemon
restart. After session deletion, a new session may reuse a name but receives a new
ID and does not inherit messages addressed to the previous peer. Names containing whitespace are rejected rather than trimmed.

**P1 is demonstrated.** Two opencode2 agents on different providers — RedStone on
`deepseek-v4-pro`, BlueJay on `google/gemini-3.8-flash` — in one project:

```
[user]      "…announce yourself as RedStone, then end your turn."
[assistant] READY.                                          <- idle, nothing polling
[synthetic] [peer BlueJay]: What is 6 times 7?              <- pushed in; woke it
[assistant] (send 42 to BlueJay)
```

BlueJay, itself idle by then, was woken in turn by the reply. Neither agent polled,
waited or looped; both were woken from idle, in both directions.

Optional real-harness checks use a private server, temporary state, and a local
scripted provider, so they need no model credentials:

```bash
dotnet build -c Release
python3 scripts/check-oc2.py --oc2 /path/to/opencode --mode codemode
python3 scripts/check-oc2.py --oc2 /path/to/opencode --mode direct
python3 scripts/check-oc2.py --oc2 /path/to/opencode --mode bootstrap
python3 scripts/check-oc2.py --oc2 /path/to/opencode --mode identity
python3 scripts/check-oc2.py --oc2 /path/to/opencode --mode tui
```

OC2 2.0.3 passed these checks. Its open TUI showed externally injected idle/busy peer
notices and streamed replies. See [the terminal findings](docs/findings-tui.md).

## Known limits

- Cold-start or disconnected event streams can lose a required project attribution
  fact. Calls then time out without performing an action; socket readiness alone does
  not guarantee event-stream readiness.
- The installed OC2 beta lacks runtime session metadata and is refused. Newer V2
  source supplies metadata, and 2.0.3 was verified end to end in both Code Mode and
  direct-tool mode. Argument-only attribution has been removed. See
  [the attribution findings](docs/findings-attribution.md).
- Registry and accepted mail survive daemon restarts in a private SQLite database.
  Lost admission responses and crashes during delivery retain uncertain mail without
  automatic retries; an uncertain item blocks later mail to that recipient. Positive
  matching inbox/transcript evidence clears it; absent evidence leaves it held.
  Session reconciliation and safe retry of absent uncertain items remain unfinished. See [the roadmap in DESIGN.md](DESIGN.md#10-roadmap).
- Messages are limited to 16 KiB of UTF-8 text; a mailbox holds at most 64 peer
  messages. The global outbox allows 256 peer messages and reserves space for notices
  within 512 items. The audit retains the last 64 state snapshots. Rate limits,
  duplicate suppression and interrupt budgets remain unfinished.

## Working agreement

Type-driven: representation, modules, names and signatures first; bodies after. Prefer
cutting scope to adding it — the safety machinery in DESIGN.md §8 is deliberately
deferred until this works end to end.
