# TUI visibility of external activity — 2026-09-14

Question: with an agent's terminal UI already open, will externally delivered peer
input appear and will the resulting execution stream live?

The answer depends on targeting the session actually owned by, or connected to, that
UI. Running a separate process against saved history does not establish that the
existing terminal will update. Backend admission, model execution, and UI rendering
are three separate behaviours to verify.

## OpenCode 2

Installed version: `opencode2 v0.0.0-beta-17519`, matching the earlier runtime demo.
Its help says the default TUI uses the background service; `--standalone` uses a
private server, `--server` selects another server, and `--session` selects a session.
Our adapter discovers the background service, so private/other servers are not
automatically covered.

Inspection of the installed Bun executable's embedded JavaScript found:

- The session data reducer handles `session.inbox.enqueued`, appending both `user`
  and `synthetic` items to the session message store.
- `session.text.delta` appends text to assistant content; tool input/progress/success
  events update corresponding tool content.
- The timeline listens for inbox events for its selected session, inserting a synthetic
  item when its description is nonempty. It also listens for streamed text/reasoning
  and step events. There is no requirement that this TUI submitted the input.
- Synthetic entries without descriptions are filtered from the timeline. The normal
  synthetic renderer displays the **description**, rather than the message body.
  Our adapter supplies `peer message from <name>`, so the expected trigger is a notice
  identifying the sender, not the full peer message text.
- The inspected background-tab unread handler counts user inputs only, not synthetic
  inputs. Visibility in the selected session does not imply an unread badge elsewhere.

Useful inspection locations in that installed binary: inbox reducer around byte
111439734, text reducer around 111442621, timeline listeners around 112001402,
description filtering around 112003471, renderer `function dLt(e)` around 112044000.
These are version-specific evidence locations, not stable interfaces.

This is strong implementation evidence for live updates in an attached TUI, but no
PTY screen capture was performed. The prior demo proves runtime wake/steer and stored
transcripts, not terminal rendering.

Current [official V2 CLI plugin docs](https://opencode.ai/v2/docs/build/plugins/cli/)
also expose server-event listeners and session/message/pending synchronization. They
support the general event-driven architecture, but are not a substitute for checking
the installed beta. Current upstream files inspected during research use some different
event names, so their behaviour was not treated as proof for this installation.

## Claude Code

Installed version identified by executable symlink: 2.1.270.

The [official channels reference](https://code.claude.com/docs/en/channels-reference)
explicitly documents external channel input appearing in the open terminal as a
one-line incoming-event summary, followed by visible agent activity. Busy-session
events queue in order and are grouped on the next turn. This establishes supported
external wake and turn-boundary delivery; it does not establish mid-generation steer.

The supported integration is an MCP channel loaded into the existing Claude process.
Channels require session opt-in, and custom channels currently use a development
allowlist bypass during research preview. This is a concrete public integration path
to investigate alongside the messaging-socket environment variables recorded earlier.
No Claude adapter exists in this repository yet.

The reference also says notifications have no acknowledgement: finishing a transport
write does not prove admission or processing, and unloaded/blocked channels can drop
events silently. An adapter must not label that write as confirmed delivery.

The [channels guide](https://code.claude.com/docs/en/channels) distinguishes replies
sent through tools from ordinary terminal responses: outbound channel replies may be
represented by the tool call and its confirmation rather than the full reply text.

These are documented behaviours, not a local end-to-end screen test.

## Codex CLI

The delegated read-only investigation reports installed `codex-cli 0.154.0` with
`queue --thread ... --message ...`, `agents`, and TUI `--remote` commands/options.
No Codex adapter exists in this repository yet.

[Official Codex App Server documentation](https://learn.chatgpt.com/docs/app-server)
supports terminal UI attachment to an app-server, externally starting an idle thread
with `turn/start`, steering an active turn with `turn/steer`, and streamed item/text/tool
notifications. Steering requires the active turn ID and does not emit a fresh
`turn/started` event.

This provides an appropriate integration architecture. It does **not** yet prove the
installed TUI's exact handling of another client's turn, including whether it displays
the trigger text and switches from idle to streaming correctly. The existing TUI must
be connected to the same app-server/thread being targeted. A separate headless run or
history injection is insufficient evidence. The documented start/steer paths accept
user input; peer provenance also needs investigation rather than assuming an OC2-like
synthetic message type.

## Adapter acceptance criteria

For each supported harness, use isolated sessions and a real terminal capture:

1. Keep the TUI idle and deliver an external peer message. Verify the visible sender,
   trigger representation, running indicator, streamed response, and return to idle.
2. Deliver while busy at turn-boundary urgency. Verify pending visibility and eventual
   execution without prematurely interrupting the original work.
3. Test interrupt urgency where the harness supports it; document or reject unsupported
   semantics instead of silently mapping them to queue.
4. Verify behaviour while typing and while scrolled away from the live tail.
5. Where supported, attach two viewers to one session, then detach/reconnect. Verify
   fresh history, activity state, and permission prompts.
6. Deliver to a background session and record badges/notifications separately from
   selected-session rendering.

Desired user experience: peer input visibly identifies its source, the terminal keeps
showing work as it happens, and local input/permission controls remain usable.
UI visibility should be a required adapter check alongside backend delivery.

No messages were sent into live sessions, no terminals were interrupted, and no harness
configuration was changed during this investigation.

## Isolated OC2 2.0.3 terminal verification

Ran an actual 120×35 terminal TUI attached to a private 2.0.3 server with temporary
XDG configuration/storage. A local scripted provider emitted word-by-word response
deltas, with a pause during the busy response. No real model credentials or paid calls
were used. The real collaboration shim/daemon committed `hello` in the same setup.

Injected synthetic queue input through the API while the terminal remained open:

- Idle: `peer message from Blue` appeared as a synthetic timeline notice; the harness
  made a new model request and the streamed response appeared in the existing view.
- Busy: `peer message from Green` appeared while the active response was paused.
  Its reply had not appeared in that capture; after the active stream completed, the
  queued response streamed into the same view. It did not overtake the active turn.

This confirms that an external trigger updates the open OC2 terminal without a user
prompt. The visible notice uses the description; the model receives the peer body.
These are terminal output assertions, not a screenshot review. Typing/scroll preservation,
background tabs, detach/reconnect, permission prompts, and steer remain separate checks.

Reproduce after `dotnet build -c Release`:

```bash
python3 scripts/check-oc2.py --oc2 /path/to/compatible/opencode --mode tui
```

The script uses a private server, a scripted local provider, separate collaboration
storage, and process-group cleanup. `--mode codemode` and `--mode direct` verify the
metadata/event join and durable registration without opening a TUI.
