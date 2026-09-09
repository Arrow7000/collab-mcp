# collab-mcp

Peer-to-peer collaboration for coding agents across providers, with **push delivery**.

Agents address each other by name and are *woken* when mail arrives — including when
idle. There is no `fetch_inbox`, because the inbox belongs to the runtime, not to the
model.

- [`DESIGN.md`](DESIGN.md) — architecture, requirements, delivery semantics, phases.
- [`docs/findings-oc2.md`](docs/findings-oc2.md) — verified notes on the opencode2
  process model that the design rests on. Read before touching the adapter.

## Agent-facing surface

Three verbs. An agent supplies only a name for itself, and a recipient and body when
writing. Its session and project are derived from the harness, never asked for.

| Verb | Meaning |
|---|---|
| `hello(name)` | Announce the name you wish to be known by |
| `roster()` | Who else is working in this project |
| `send(to, body, urgency)` | Write to a peer; `urgency` is at-turn-boundary or interrupt |

Which session made a call, and which project it is in, come from the harness's event
stream — never from the agent and never from the shim. So `send` takes no `from`, and an
agent cannot claim to be a peer, nor get its own working directory wrong.

## Using it

One line, and nothing to start by hand — the shim spawns the daemon on first use.

```bash
dotnet build
```

Then in `~/.config/opencode/opencode.json` (or a project-local `opencode.json`):

```json
{ "mcp": { "collab": { "type": "local", "enabled": true,
    "command": ["<repo>/src/Collab.Shim/bin/Debug/net10.0/collab-mcp"] } } }
```

The daemon keeps its socket, lock and log under `~/.collab-mcp`. The log is the only
account of what it did, since it runs detached with no terminal.

## State of play

| Component | Status |
|---|---|
| `src/Collab.Domain` | Complete; pure, no IO |
| `src/Collab.Adapters.OpenCode` | Complete: `Deliver` via synthetic, `Observe` via SSE |
| `src/Collab.Daemon` | Complete: registry, correlation, intents, unix socket |
| `src/Collab.Shim` | Complete: three tools, spawns the daemon |
| Claude Code adapter | P3 |

`dotnet build` from the repo root builds all four projects, zero warnings.

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

## Known limits

- The first call after a *cold* daemon start cannot be attributed and answers "call
  again" — the event that would identify it is emitted before the daemon exists, and is
  not replayable. The daemon is long-lived, so this is once per machine, not per session.
- Two calls with the same verb and identical arguments are indistinguishable. Only
  `roster()` takes no arguments, so only simultaneous `roster()` calls from *different*
  projects could be answered for each other.
- Everything in DESIGN.md §8 — rate limits, dedupe, interrupt budgets — is still
  deliberately absent.

## Working agreement

Type-driven: representation, modules, names and signatures first; bodies after. Prefer
cutting scope to adding it — the safety machinery in DESIGN.md §8 is deliberately
deferred until this works end to end.
