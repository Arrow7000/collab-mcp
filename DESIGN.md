# collab-mcp — design

Peer-to-peer collaboration for coding agents across providers, with **push delivery**.

Status: P1 built and demonstrated end to end. Phases are at the bottom.
Current milestones and acceptance checks are tracked in §10 below.

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

Agents get three verbs: `hello` to announce a name, `roster` to see who else is in the
project, and `send`. There is no `fetch`, no `check_mail`, no protocol to remember. Mail arrives on the same path a user message arrives on, so an agent can no
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

**F6. The event bus supplies what MCP lacks.** `session.tool.progress` carries the
`sessionID`, the tool name and the exact MCP arguments on one event, ~1 ms before the
server receives the call. Attribution therefore comes from the event stream, not from
the tool call — the agent never needs to know its session id and cannot lie about it.

Two corrections to an earlier reading of this, both found by building it. The obvious
candidate, `session.tool.called`, is the wrong event: opencode does not expose MCP tools
to the model at all, only inside its `execute` sandbox, so that event describes the
sandbox call and its `input` is JavaScript source. And the progress frames are not
durable, so a call that begins before the router is listening can never be attributed —
which is precisely the first call after a cold start, since the harness spawns the stdio
server as part of it. That call is answered with "call again", and the second succeeds.

**F7. Claude Code is different.** It *does* export `CLAUDE_CODE_SESSION_ID`,
`CLAUDE_CODE_MESSAGING_SOCKET` and `CLAUDE_CODE_MESSAGING_TOKEN` to stdio MCP servers it
spawns. So attribution has two shapes: env-based (Claude Code) and event-correlated
(opencode). Caveat: a wrapper such as `nix-shell` scrubs the environment, so env-based
attribution is only reliable for directly-spawned servers.

**F8. MCP registration is a static config line.** There is no runtime registration
protocol. `type: local` servers are spawned **lazily**, but at session start rather than
at first call, and a call made before the server is ready simply blocks. This is what
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

It also owns correlation, and owns the wording of everything an agent is told. The first
because caller-blindness is an artefact of how MCP is specified rather than a rule about
who may talk to whom: `Router` is handed a session and decides routing, and never learns
that the question was hard. The second so that there is one place to read what an agent
sees, and no way for the two halves to disagree about what a verb means.

**Discovery.** `roster` lists the agents in the asking agent's project, so peers find
each other without their names being written into their prompts. Only the agent that
starts *second* needs it: the first does not have to find anyone, because the second
finds it and sends, and push delivery wakes it. Discovery therefore never has to wait or
poll. (P2: a `LastActive` stamp and a filter, so a long-lived daemon does not offer up
every name ever used in a project.)

**Shim** — stdio MCP, deliberately thin: forward calls, no logic worth testing. It
connects to the daemon's unix socket at startup rather than on first call; if the daemon
is absent it spawns it detached (singleton via lockfile) and waits. Startup rather than
first call because the harness starts a stdio server before it will run anything, so the
wait costs time the model has not yet had — and a daemon started by the first call
subscribes to the harness after the event that would have attributed that call.

The shim and daemon are one binary, invoked with `--daemon` for the latter. Not
tidiness: it is what lets the shim start the daemon without a second path to configure or
keep in step, so the user writes one line and nothing else.

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

**Idle is not a delivery state.** An idle session is live, and delivering to it wakes it
(F2) — that is the entire point of the system. Mail *parks* only when nothing is bound
to the recipient's name: the agent has not started, or its session ended. Parked mail
flushes when a session claims the name.

**The router does not track turn boundaries.** Every harness we support implements that
itself: opencode chooses between `queue` and `steer` internally, and Claude Code
delivers between tool calls or starts a new turn. Modelling turn state in the router
would duplicate the runtime and get it subtly wrong, so `HarnessEvent` carries no
"went idle" case.

## 7. Identity and attribution

**Automatic presence, optional explicit naming.** OC2 sessions are registered when the
harness reports session creation, viewing, or execution and the project's `collab` MCP
server is connected. The default peer name uses a harness prefix plus two session-derived words, such as
`oc2-maple-otter`; collisions with live names/aliases get a numeric suffix. The
prefix is `oc2-` for OpenCode and `claude-` for the domain's ClaudeCode harness.
ClaudeCode presence is not yet implemented by an adapter.
This does not enumerate historical conversations, execute model work, or wake an idle
session. A scoped `roster`/`send` call also repairs a missed lifecycle registration.

`hello` is optional and changes the display name without changing the durable peer ID.
Roster entries expose current name, ID, and previous names; new messages expose sender
name and ID. Previous names remain reserved aliases while the peer is live (at most
64). Send accepts names or IDs; known recipients are pinned by ID before delivery.
Unknown names park until first claimed; unknown IDs are refused. IDs cannot be claimed
as display names. Existing explicit names are preserved during automatic registration.

The cost is that the router cannot distinguish "still booting" from "misaddressed" at
send time, so it must assume the former and park under a deadline. Peers spawned
together genuinely do race, and refusing there is what forces retry loops.

Names are **never rewritten**. An invalid or colliding name is an error, not a silent
substitution.

**Ownership.** One live endpoint owns one name, and one name has one live owner in its
project. Repeating `hello` with an equivalent name preserves the original spelling and
binding timestamps. Renaming preserves the ID, timestamps, and mailbox; changing back
to an alias is allowed. A competing live session cannot claim its names or aliases.
Terminal reconnection to the same harness session and daemon restart preserve identity.
Session deletion releases names, but a genuinely new session gets a new ID and cannot
inherit mail pinned to the old peer. Missing/deleted-session reconciliation remains
required recovery work.

Version-1 persisted registrations receive deterministic full IDs. Versions 1–3
migrate atomically to version 4, exposing a persisted eight-hex-digit public address
without changing full IDs or names. Earlier prefixed short addresses and full IDs
remain accepted. Already submitted message text stays unchanged; new messages show
bare hex IDs. Eight-character hex strings are reserved for ID addressing.
Short addresses are reserved across all stored peers, including inactive peers; new
allocation retries collisions and also checks names/aliases. The full ID is checked
for collision before insertion as well. Legacy pending messages retain their exact original rendered text so reconciliation
can still identify an already admitted message; newly created messages carry IDs.

**Nothing about an agent's identity or location is taken from the agent.** An agent
supplies only the name it wishes to be known by, and who it is writing to. Which session
it is, and which project that session works in, both come from the harness: opencode
reports them on every event, Claude Code in the environment of the server it spawns.
This is not only about lying — it removes a whole class of mistake, since an agent
cannot get its own workdir wrong if it is never asked for it. It also means `send` takes
no `from`: the router resolves the sender from the session that made the call.

**Scope is derived, never configured.** A name is unique within a project, not across
the machine, so two unrelated repositories cannot collide on `RedStone`. The project
comes from the harness: opencode puts `location.directory` on every event, and Claude
Code exports `CLAUDE_PROJECT_DIR` to the servers it spawns. There is no workspace to
create, name or enumerate — agents working the same directory are peers, and an event
that arrives without a location is dropped rather than guessed at, since an unscoped
endpoint would silently merge every project into one namespace.

Binding `name → endpoint`:

- **Claude Code**: read `CLAUDE_CODE_SESSION_ID` and `CLAUDE_PROJECT_DIR` from the
  shim's environment. Direct; no correlation needed.
- **opencode**: lifecycle observations announce connected sessions automatically. For
  tool calls, the router matches the
  `session.tool.progress` event carrying that call and binds the emitting `sessionID`.
  Correlation happens on *every* call, not only at bind time, because `send` and `roster`
  need the caller's identity just as much as `hello` does — that is what lets `send` take
  no `from`. The mapping is persisted with the outbox in SQLite. An unbound
  mailbox can be reclaimed when a session restarts.

  Runtime session metadata is mandatory. The shim forwards `_meta` separately from
  arguments; the daemon requires `ai.opencode/sessionID` (source) or `sessionID` (docs),
  refuses conflicting/malformed/missing context, and joins only facts from that session.
  Scope still comes from the event. Only `collab` server progress is eligible. The
  installed beta lacks metadata and is unsupported; argument-only matching was removed
  following the user's compatibility decision. OC2 2.0.3 was verified through the real
  shim and durable daemon in isolated Code Mode with a local scripted provider.
  Direct mode now joins its scoped name/input events and was also verified end to end. Evidence is in [docs/findings-attribution.md](docs/findings-attribution.md).

Identities outlive sessions. A name is a mailbox, not a process.

## 8. Safety

P1 initially shipped with self-address refusal and a park deadline. Name ownership
now refuses live collisions and renaming a bound session. Text is bounded to 16 KiB
UTF-8, mailboxes to 64 peer items, and the outbox to 256 peers with reserved capacity
for notices within 512 items. Admission capacity refusals never discard accepted mail.
Rate limits, duplicate suppression and interrupt budgets remain requirements below.


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

## 10. Roadmap

P1 demonstrated bidirectional idle wake-up between two OpenCode agents on different
providers. The original P2 phase covered reliability and safety; P3 covered other
harnesses and shared-edit coordination. The milestones below track that remaining
work and its acceptance checks. Historical reviews live in `docs/` and are not current
status reports.

### Current target

Two peers can send, stop, restart, and resume without accepted messages silently
disappearing or identities changing. The open terminal shows the trigger and streams
the resulting activity. Backend transcript checks alone are insufficient.

### M1 — trustworthy identity and existing delivery

- [x] Enforce one active name per endpoint and one active endpoint per name.
  Repeating hello with the same name is idempotent and preserves displayed spelling;
  renaming preserves the peer ID and reserves previous names as aliases. Another
  live owner blocks a claim. A new session may reuse released names with a new ID.
- [x] Add durable peer IDs, safe renaming, and addressing by ID or reserved alias;
  migrate persisted state and verify actual OC2 rename/delivery.
- [x] Add deterministic regression tests and include them in `dotnet test`.
- [x] Inspect installed and current V2 MCP call paths for harness-supplied context.
- [x] Forward session metadata independently of arguments and constrain event matching
  by session when present; refuse malformed/conflicting metadata.
- [x] Filter observations to the collab server and retain endpoint identity in dedupe keys.
- [x] Require runtime session metadata and remove argument-only attribution.
- [x] Automatically register newly created/viewed/executing OC2 sessions with connected
  collab servers. Preserve explicit names; test two peers before any model/hello call.
- [x] Demonstrate metadata-scoped attribution through the real shim/daemon in isolated
  OC2 2.0.3 Code Mode, using a local scripted model provider.
- [x] Support and verify the direct-tool event sequence (`codemode:false`).
- [x] Keep ownership of backlog until delivery admission is acknowledged; report
  failed flushes truthfully and release missing-session bindings.
- [x] Apply park deadlines during claims, not just timer ticks.
- [x] Fix discovery subprocess deadlines, socket disposal, and daemon response deadlines.
- [x] Expose event-stream readiness/failures in logs and `--status`; bound SSE handshakes.
  Early shim startup only establishes socket readiness. It cannot promise receipt of
  the first attribution fact on metadata-less harnesses.
- [x] Reject silent name rewriting, including surrounding whitespace.
- [x] Normalize absolute filesystem scopes and trailing separators while preserving case.
  Symlink aliases remain separate scopes.
- [x] Correct README claims about attribution and component completeness.

Done when regressions are covered, failure paths have explicit outcomes, and remaining
attribution limitations are documented precisely. Do not claim identity safety while
the required metadata/event path lacks runtime verification.

### M2 — durable delivery and recovery

- [x] Specify delivery state and transitions before storage bodies: pending, admitted,
  retryable, expired, permanently failed, and ambiguous admission after response loss.
- [x] Persist registry, outbox, and audit changes together in SQLite.
- [x] Feed delivery acknowledgements into the engine; retain stable message IDs.
- [ ] Complete session reconciliation and absent-input recovery after reconnect/restart.
  Positive matching synthetic inbox/transcript evidence now clears uncertain mail.
- [ ] Define ordering and idempotency using verified harness capabilities.
- [ ] Complete version/restart workflow and reproducible installation. Read-only
  `--status` now reports connection readiness, bindings, and outbox states.

Done when daemon and harness restart tests preserve accepted mail, failed deliveries
remain recoverable, and ambiguous admissions have a documented policy. No unsupported
exactly-once promise.

Current recovery policy: accepted mail is committed before intents or acknowledgements.
Waiting mail remains retryable; acknowledged admissions leave the outbox. A lost
response or a restart during delivery leaves an uncertain item that blocks its mailbox.
It is never retried blindly. Synthetic input uses a stable `msg_` ID derived from the
envelope UUID. Identical admissions were verified against installed OC2 with
`resume:false`: the same item was returned twice and one inbox item remained. This
checks queued idempotency; projected-message and cross-version retry guarantees remain
open. Reconciliation accepts only positive, matching inbox/transcript evidence.
SQLite commits the registry, outbox, and a full state audit snapshot in one transaction;
unchanged state produces no audit row. Audit snapshots retain the last 64 transitions
in the same transaction, bounding historical storage. The private database is `~/.collab-mcp/state.sqlite`
(or the directory supplied by `COLLAB_MCP_HOME` for isolated runs).

### M3 — bounded operation and visible behaviour

- [x] Bound outbox/mailboxes, message sizes, and audit snapshot retention.
- [ ] Limit sender/recipient traffic and interrupts.
- [ ] Suppress duplicate messages and prevent reply loops.
- [ ] Define activity-aware roster cleanup without confusing idle with dead.
- [x] Add fake HTTP/SSE integration tests, an optional real OC2 check script, and Linux/macOS CI checks.
  CI configuration is added to the working tree; hosted execution is unverified.
- [x] Verify a real OC2 2.0.3 TUI displays idle/busy queue triggers and streams replies
  without user prompts, using a scripted local provider. Busy mail does not overtake.
- [ ] Complete visual/tool/permission/steer and return-to-idle checks beyond terminal
  text assertions. Reproduce the current check with `scripts/check-oc2.py`.
- [ ] Check typing, scroll position, background tabs, and detach/reconnect separately.

Done when unattended collaboration has bounded resource use and the user can observe
externally triggered work in the same open session.

### M4 — cross-harness collaboration

- [ ] Verify Claude Code's channel transport against its installed version, including
  opt-in, receipt semantics, terminal visibility, and unsupported interrupt behaviour.
- [ ] Verify Codex's shared app-server/thread transport, external-input rendering,
  attribution, and peer provenance. Do not assume a synthetic message type exists.
- [ ] Implement adapters behind the harness boundary.
- [ ] Demonstrate bidirectional idle wake-up across harnesses with real TUIs open.

Done when cross-harness peers share a namespace and exhibit the same supported
delivery contract; unsupported urgency must be explicit.

### M5 — coordination of shared edits

- [ ] Specify file lease ownership, expiry, renewal, and conflict behaviour.
- [ ] Decide whether leases are advisory or enforced by harness/tool integration.
- [ ] Define an enforceable permission-isolation policy for opaque peer messages.
- [ ] Demonstrate two peers coordinating shared edits and recovering from a dead owner.

### Evidence

- 2026-09-14: removed the public ID prefix; bare hex and prior prefixed addresses
  resolve to the same identity. Version-3 migration preserves submitted text. All
  105 tests and the real OC2 identity check pass; the live migration preserved
  all five registrations and session bindings.

- 2026-09-14: compact eight-hex-digit public peer IDs with collision retry, backward
  compatible full-ID addressing, and readable generated names implemented. Migration
  preserves identity and the exact text of already submitted messages. All 104 tests
  pass in Debug and Release; real OC2 identity and bootstrap checks pass. The live
  migration preserved every existing name and full ID and assigned unique short IDs.

- 2026-09-14: durable peer IDs, safe renaming, reserved aliases, and name/ID addressing
  implemented. SQLite migration and restart preserve identity. Real OC2 verifies that
  old-name and ID sends reach the renamed peer and expose the sender ID. All 99
  tests pass; Debug builds without warnings. The live migration preserved both
  existing names and session bindings, and bootstrap registration still passes.

- 2026-09-14: session metadata forwarding, session-constrained event matching, server
  filtering, and endpoint-scoped dedupe implemented. All 40 regression cases pass;
  Release builds with zero warnings/errors. Newer harness runtime verification remains.
- 2026-09-14: name ownership implemented; routing and engine regression tests added.
  Live owners block competing claims, repeat hello preserves identity, deletion permits
  reclaim, and legacy aliases are released together. Attribution remains unresolved.
- [Initial review](docs/review-2026-09-14.md)
- [Original OC2 runtime findings](docs/findings-oc2.md)
- [TUI visibility investigation](docs/findings-tui.md)
- [Attribution investigation](docs/findings-attribution.md)

Update checkboxes only after implementation and relevant checks pass. Keep unresolved
harness questions here instead of treating API acceptance as end-to-end proof.

## 11. Working agreement

Type-driven: representation, modules, names and signatures first; function bodies after.
High-level and low-level thinking are kept in separate passes.
