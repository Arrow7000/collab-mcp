# Empirical findings — opencode2 process model

All established 2026-09-09 against `opencode2 v0.0.0-beta-17519` on macOS.
Recorded so a later session need not re-derive any of it.

## Process model

`opencode2` is a thin client against a persistent daemon:

```
$ opencode2 service status
http://127.0.0.1:49374

$ ps -o command -p 83151
/Users/aron/.opencode/bin/opencode2 serve --service
```

- Basic auth; password in `~/.config/opencode/service.json`.
- Port is **dynamic** and appears only in `~/.local/share/opencode/log/opencode.log`.
  Discover via `opencode2 service status`.
- OpenAPI spec at `GET /openapi.json` (~289 KB, 99 paths, 51 event types). It sits at the
  root rather than under `/api`, but still requires the same basic auth.
- Responses are wrapped: `{"data": ...}`.
- `Model.Ref` uses `id`, **not** `modelID` (sending `modelID` yields HTTP 400).

## Endpoints that matter

Every route below is under `/api`. `GET /openapi.json` is the exception and sits at the
root. The spec declares `"security": []` on every path, which is **misleading**: basic
auth is genuinely required, and an unauthenticated `GET /api/session` returns 401.

| Endpoint | Meaning |
|---|---|
| `POST /api/session/{id}/synthetic` | Admit input that is not a user turn; schedules execution |
| `POST /api/session/{id}/prompt` | Admit a user turn |
| `delivery: "steer" \| "queue"` | Interrupt mid-turn, or hold to turn boundary |
| `POST /api/session/{id}/inbox/{item}/steer` | Promote an already-queued item to interrupt |
| `POST /api/session/{id}/interrupt` | Hard stop; `continue=true` resumes pending steering |
| `GET /api/session/{id}/inbox` | Durable enqueued work not yet delivered |
| `GET /api/event` (SSE) | `session.idle`, `session.step.ended`, `session.tool.called`, … |
| `POST /api/session/{id}/wait` | Block until the session is idle |
| `GET /api/session/active` | Currently running sessions |
| `PUT /api/session/{id}/instructions/entries/{key}` | Durable instruction entry, announced at next step boundary |

## Verified: synthetic wakes an idle session

Created a session, prompted it, waited for idle, then injected a synthetic message with
no user prompt. Transcript afterwards:

```
[user]      "Reply with exactly: READY."
[assistant] "READY."
[synthetic] "[peer RedStone]: the number is 47. Reply with exactly: GOT 47"
[assistant] "GOT 47"
```

The session woke and executed on its own. No hook, no poll, no cron.

## Verified: steer interrupts a busy session

Started a long generation, then injected with `delivery: "steer"`:

```
[user]      "Write the numbers 1 to 200, one per line..."
[assistant] "Here are the numbers 1 through 200... 1. **1** — The first positive integ"   <- cut off
[synthetic] "STOP counting immediately. Reply with exactly: STEERED."
[assistant] "STEERED."
```

## Verified: MCP servers cannot identify their caller

Registered a probe stdio MCP server and logged everything available to it.

```json
"clientInfo": { "name": "cli", "version": "0.0.0-beta-17519" }
```

- No session id anywhere in `initialize`.
- No session/agent environment variables.
- `cwd` is the project directory, shared by every session in it.
- The process is a child of the **shared daemon** (`opencode2 serve --service`), not of a
  session — so one MCP process serves many sessions.

Consequence: process identity, cwd and clientInfo are all useless for attribution.

## Verified: MCP tools are reachable only from the JS sandbox

opencode2 does **not** expose an MCP tool to the model as a tool. A model that calls one
directly gets `Unknown tool: probe`. They are reachable only from inside the `execute`
sandbox, namespaced by server:

```js
return await tools.probe.probe({ note: "hello-from-probe" });
```

Two consequences follow, and the second is the load-bearing one.

- This is *why* models write busy-wait loops around messaging tools (see the gotcha at
  the end). Anything asked of an MCP tool is asked inside a JavaScript sandbox, where a
  wait is a thing the model can write.
- `session.tool.called` describes the **sandbox** call, so its `input` is JavaScript
  source. The arguments an MCP server actually receives are not in it.

## Verified: the event bus supplies attribution

`session.tool.progress` is the join between an MCP call and the session that made it:

```json
{"sessionID": "ses_…", "assistantMessageID": "msg_…", "id": "call_00_…",
 "metadata": {"toolCalls": [
   {"tool": "probe.probe", "status": "running", "input": {"note": "hello-from-probe"}}]}}
```

It carries the session, the tool namespaced as `<server>.<tool>`, and the exact MCP
arguments — everything needed, on one event, with no join across frames.

Measured ordering: the `running` frame is emitted **~1 ms before** the MCP server
receives the call, and the call itself blocked for four seconds without delaying it. So a
router may wait for this event while answering the call it describes, and will not
deadlock.

Four things will bite anyone reimplementing this:

- **`toolCalls` is cumulative and append-only.** Every frame restates every call before
  it, and a `completed` entry is restated too. Only newly-`running` entries are new.
- **The call id is not unique.** `data.id` is minted by the model provider, not by
  opencode: deepseek issues `call_00_…`, but google/gemini issues the literal `tool_0`
  for *every* call in a session. Identity is `(assistantMessageID, id, index)`.
- **A call with no arguments has no `input` field at all** — not `{}`, absent. Over MCP
  the same call arrives as `{}`. Anything matching on arguments must treat the two as
  equal, or an argument-free verb can never be attributed.
- **These frames are not durable.** `session.tool.called` carries a `durable` block;
  `session.tool.progress` does not, so a missed frame cannot be replayed. A router that
  starts *after* a call has begun can never attribute that call — which is exactly what
  happens to the first call of a cold start, since the harness spawns the stdio server
  as part of that call and the event precedes it. The only remedy is to say so and let
  the agent call again.

The synthetic response is `{"data": {id: "msg_…", sessionID, timeCreated, type:
"synthetic", payload, delivery}}`. Keep `data.id`: it is the handle that
`POST /api/session/{id}/inbox/{item}/steer` takes to promote a queued item later.

SSE frames carry the event type twice — on the `event:` line and as `type` inside the
JSON payload. Prefer the payload's.

## Verified: a peer message is structurally distinct

`GET /api/session/{id}/message` returns peer deliveries as their own message type, with
the text at the top level rather than under `content`:

```json
{"id": "msg_…", "text": "[peer BlueJay]: What is 6 times 7?",
 "description": "peer message from BlueJay", "type": "synthetic"}
```

So provenance (requirement 7) is native, not convention — a recipient cannot mistake a
peer message for a user turn.

## Verified: Claude Code is the opposite

A stdio MCP server spawned directly by Claude Code receives:

```
CLAUDE_CODE_SESSION_ID=<value>
CLAUDE_CODE_MESSAGING_SOCKET=<value>
CLAUDE_CODE_MESSAGING_TOKEN=<value>
CLAUDE_PROJECT_DIR=<value>
```

Caveat: a server launched through a wrapper that scrubs the environment (observed with
`nix-shell`) receives **none** of these. Env-based attribution is only reliable for
directly-spawned servers.

## MCP registration

Static config line only; there is no runtime registration protocol.

- `type: local` (stdio): the harness spawns the process **lazily** (observed as `pending`
  in `opencode2 mcp list` until first use), then keeps it. Measured: it is spawned when
  the session starts and answers `initialize` and `tools/list` immediately, with the
  first `tools/call` arriving seconds later — so a server has time to get ready before
  anything is asked of it. But on a genuinely cold start the model can reach its first
  call while the server is still starting, and the call blocks until the server answers.
  That is the window in which the attribution event above is lost.
- `type: remote` (http): something must already be listening.

Both `opencode` v1 and `opencode2` read `~/.config/opencode/opencode.json`, and
project-local `opencode.json` is also honoured.

## Gotcha: models write busy-wait loops

Instructing an agent to "poll the inbox up to N times" makes it write
`while (Date.now() - start < ms) {}` inside opencode's JS `execute` sandbox. This blocks
the sandbox: DeepSeek died with `Error: Transport`, Gemini spun for 20+ minutes. This is
a large part of why the pull model is untenable here, independent of ergonomics.

The sandbox finding above explains why this is not merely a bad habit. Every MCP call is
made from inside JavaScript, so a wait is always within reach of the model — there is no
version of a messaging tool here that a model *cannot* wrap in a loop. Only removing the
reason to wait removes the loop, which is what push delivery does.

## Gotcha: a tool description is the whole contract

Related, and cheap to get wrong: an agent meets this system only through the text of the
tool descriptions. Saying "end your turn, you will be woken" in them, and again in every
result, is what actually stops the loop. Observed: told only what the tools did, a model
that got an error went on to read the entire source tree and run `dotnet build` looking
for the fault.
