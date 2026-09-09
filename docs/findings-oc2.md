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
- OpenAPI spec at `GET /openapi.json` (~289 KB, 99 paths, 51 event types).
- Responses are wrapped: `{"data": ...}`.
- `Model.Ref` uses `id`, **not** `modelID` (sending `modelID` yields HTTP 400).

## Endpoints that matter

| Endpoint | Meaning |
|---|---|
| `POST /session/{id}/synthetic` | Admit input that is not a user turn; schedules execution |
| `POST /session/{id}/prompt` | Admit a user turn |
| `delivery: "steer" \| "queue"` | Interrupt mid-turn, or hold to turn boundary |
| `POST /session/{id}/inbox/{item}/steer` | Promote an already-queued item to interrupt |
| `POST /session/{id}/interrupt` | Hard stop; `continue=true` resumes pending steering |
| `GET /session/{id}/inbox` | Durable enqueued work not yet delivered |
| `GET /event` (SSE) | `session.idle`, `session.step.ended`, `session.tool.called`, … |
| `POST /session/{id}/wait` | Block until the session is idle |
| `GET /session/active` | Currently running sessions |
| `PUT /session/{id}/instructions/entries/{key}` | Durable instruction entry, announced at next step boundary |

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

## Verified: the event bus supplies attribution

`session.tool.called` carries:

```
data.sessionID          ses_…
data.assistantMessageID msg_…
data.input              (full tool input object)
```

So a router subscribed to `GET /event` can attribute any MCP tool call to its originating
session by matching the input. This is the keystone of the design.

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

- `type: local` (stdio): the harness spawns the process **lazily on first tool call**
  (observed as `pending` in `opencode2 mcp list` until first use).
- `type: remote` (http): something must already be listening.

Both `opencode` v1 and `opencode2` read `~/.config/opencode/opencode.json`, and
project-local `opencode.json` is also honoured.

## Gotcha: models write busy-wait loops

Instructing an agent to "poll the inbox up to N times" makes it write
`while (Date.now() - start < ms) {}` inside opencode's JS `execute` sandbox. This blocks
the sandbox: DeepSeek died with `Error: Transport`, Gemini spun for 20+ minutes. This is
a large part of why the pull model is untenable here, independent of ergonomics.
