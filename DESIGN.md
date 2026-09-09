# collab-mcp — design

Peer-to-peer collaboration for coding agents across providers, with **push delivery**.

Status: design agreed, implementation not started. Phase 1 scope is at the bottom.

---

## 1. The problem

Existing agent-messaging layers (MCP Agent Mail and friends) sit at the MCP **tool**
layer. From there the only verb they can offer a recipient is `fetch_inbox`. That makes
delivery depend on the recipient remembering to look, which means:

- an idle agent is never nudged — mail waiting forever and mail ignored forever are the
  same state;
- "check your inbox each turn" has to be carried in the prompt, and is silently dropped;
- polling costs tokens on every check, and in opencode's JS `execute` sandbox models
  implement polling as a CPU busy-wait, which hangs the session outright.

The fix is not a better inbox. It is to stop exposing an inbox to the model at all.

## 2. Core inversion

> **The inbox belongs to the runtime, not to the agent.**

Agents get one verb: `send`. There is no `fetch`, no `check_mail`, no protocol to
remember. Mail arrives on the same path a user message arrives on, so an agent can no
more forget to read it than it can forget to read its prompt.

Every ergonomic complaint above is downstream of exposing `fetch` to the model.

## 3. Requirements

**Must**

1. Peer addressing — any agent addresses any other; no parent in the data path.
2. Provider-agnostic identity — a name is a name, whatever model is behind it.
3. Push, not pull — the recipient is woken.
4. Wake **idle** agents specifically; that is the common case, not the edge case.
5. Two urgency classes: interrupt-now, and at-the-next-turn-boundary.
6. Durable — survives a recipient that is busy, crashed, or not yet started.
7. Provenance — the recipient can tell a peer message from a user message.
8. Permission isolation — a peer cannot launder capabilities it does not have.
9. Auditable — reconstruct who told whom what.
10. Real coordination primitives (file leases), not just chat.
11. Free when idle — zero tokens for an agent with no mail.

**Must not**

1. No polling loops.
2. No orchestrator in the data path. A lead may start work, never relay it.
3. No mandatory per-pair handshake for the ordinary case.
4. No silent identity rewriting. A name we were given is the name we use, or we error.
5. No unbounded interruption — rate limits, dedupe, loop-breaking are required.
6. A peer message is not a user turn, and must be structurally distinct.
7. Delivery must never depend on recipient cooperation.

## 4. Findings that shape the design

Established empirically on 2026-09-09 against `opencode2 v0.0.0-beta-17519`.
Evidence in `docs/findings-oc2.md`.

**F1. opencode2 is a persistent daemon, not a CLI.** Sessions are durable server-side
objects with event-sourced inboxes, behind a local HTTP API (99 routes, 51 event types).

**F2. An idle session can be woken from outside.** `POST /session/{id}/synthetic`
admits input that is *not* a user turn and schedules execution. Verified: an idle
session woke and replied with no user prompt, no hook, no polling.

**F3. A busy session can be interrupted mid-turn.** `delivery: "steer"` lands during
generation and redirects the model. `queue` defers to the turn boundary. A queued item
can be promoted to steer after the fact.

**F4. A third message type already exists.** Transcripts distinguish
`user` / `assistant` / `synthetic`, so requirement 7 (provenance) is native, not convention.

**F5. MCP servers learn nothing about their caller.** `clientInfo` is `{"name":"cli"}`,
there are no session env vars, and `cwd` is the shared project dir. Worse, the stdio
process is a child of the **shared daemon**, not of a session, so one process serves many
sessions. Process identity does not disambiguate.

**F6. The event bus supplies what MCP lacks.** `session.tool.called` carries both
`sessionID` and the full tool `input`. Attribution therefore comes from the event
stream, not from the tool call — the agent never needs to know its session id and
cannot lie about it.

**F7. Claude Code is different.** It *does* export `CLAUDE_CODE_SESSION_ID`,
`CLAUDE_CODE_MESSAGING_SOCKET` and `CLAUDE_CODE_MESSAGING_TOKEN` to stdio MCP servers it
spawns. So attribution has two shapes: env-based (Claude Code) and event-correlated
(opencode). Caveat: a wrapper such as `nix-shell` scrubs the environment, so env-based
attribution is only reliable for directly-spawned servers.

**F8. MCP registration is a static config line.** There is no runtime registration
protocol. `type: local` servers are spawned **lazily on first tool call**. This is what
lets the shim bootstrap the daemon invisibly.

## 5. Architecture

```
   agent (any provider)
        │  send / hello / roster          MCP stdio
        ▼
   ┌──────────┐   unix socket   ┌───────────────────────────┐
   │   shim   │ ──────────────▶ │        router daemon      │
   └──────────┘   (spawns it    │  registry · queue · limits│
                   if absent)   └───────────┬───────────────┘
                                            │ HarnessPort
                              ┌─────────────┴─────────────┐
                              ▼                           ▼
                     opencode adapter            claude-code adapter
                   (SSE events, synthetic)        (socket, env ident)
```

**Router daemon** — the only long-lived component. Owns the event subscriptions, the
name→endpoint registry, the durable queue, and the safety limits. It never inspects
message *content* to make routing decisions, so it cannot become the relay bottleneck
that the lead-orchestrator model is.

**Shim** — stdio MCP, deliberately thin: forward calls, no logic worth testing. On first
call it connects to the daemon's unix socket; if absent, spawns it detached (singleton
via lockfile) and retries. One config line for the user, no plist, no manual start.

**Adapters** — the only provider-specific code, and the only place beta APIs appear.

**Store** — SQLite: registry, parked queue, leases, audit log.

## 6. Delivery semantics

Two classes only:

| Class | opencode mapping | Use |
|---|---|---|
| `AtTurnBoundary` | `delivery: queue` | "I landed the schema change." Never derails work in flight. |
| `Interrupt` | `delivery: steer` | "Stop — you are editing a file I hold a lease on." |

Request/response is **not** a third class. It is `AtTurnBoundary` plus a correlation id,
with the agent told to end its turn. The session persists server-side, so halting is
free and loses nothing; the reply wakes it. This avoids both the polling loop and the
model's habit of narrating "I have asked and will not await a reply".

## 7. Identity and attribution

Names are chosen by the user or the agent and are **never rewritten**. An invalid or
colliding name is an error, not a silent substitution.

Binding `name → endpoint`:

- **Claude Code**: read `CLAUDE_CODE_SESSION_ID` from the shim's environment. Direct.
- **opencode**: the agent calls `hello(name)`; the router matches the
  `session.tool.called` event carrying that exact input within a short window and binds
  the emitting `sessionID`. One correlation at bind time; the mapping is then durable
  and re-bindable when a session restarts.

Identities outlive sessions. A name is a mailbox, not a process.

## 8. Safety

- **Loop breaking** — rate-limit per sender→recipient pair, drop identical repeats within
  a window, cap queue depth. Two agents steering each other forever is otherwise real.
- **Interrupt budget** — `Interrupt` is rationed. If anything can interrupt at will,
  nothing finishes.
- **Permission isolation** — a peer message never satisfies a permission prompt, and the
  router refuses to carry a request the sender's own session could not perform. Enforced
  at the router, since the recipient may be the more privileged side.
- **Local only** — the daemon holds harness credentials and can steer any session:
  unix socket, `0600`, no TCP.

## 9. Risks

- `synthetic` / `steer` / the inbox routes are **v2-beta** with no stability guarantee.
  Contained entirely behind `HarnessPort`; the domain never names them.
- opencode's daemon port is dynamic and recorded only in its log. Discovery shells out to
  `opencode2 service status`. Cheap, slightly ugly, isolated in the adapter.
- The SSE contract states a slow consumer overflows and **fails the stream**. The router
  must reconcile via `GET /session/{id}/inbox` on reconnect rather than trust it saw
  every event.

## 10. Phases

- **P1** — domain types, router daemon (SSE + registry + `hello`/`send`), opencode
  adapter, both delivery classes, stdio shim.
  Demo: two opencode agents on different providers, one idle, woken by the other, no polling.
- **P2** — durable parking for unbound names, reconnect/reconciliation, rate limits and
  loop-breaking, audit log.
- **P3** — Claude Code adapter (one namespace across harnesses), file leases,
  permission-laundering guard.

## 11. Working agreement

Type-driven: representation, modules, names and signatures first; function bodies after.
High-level and low-level thinking are kept in separate passes.
