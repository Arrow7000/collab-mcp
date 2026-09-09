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

## State of play

| Component | Status |
|---|---|
| `src/Collab.Domain` | Types and bodies complete; builds clean |
| `src/Collab.Adapters.OpenCode` | Complete: `Deliver` via synthetic, `Observe` via SSE |
| Router daemon | **Not started** — hosts `RouterState`, folds events, performs `Intent`s |
| Stdio shim | **Not started** — three tools over a unix socket to the daemon |
| Claude Code adapter | P3 |

`dotnet build` from the repo root builds both projects, zero warnings.

## Working agreement

Type-driven: representation, modules, names and signatures first; bodies after. Prefer
cutting scope to adding it — the safety machinery in DESIGN.md §8 is deliberately
deferred until this works end to end.
