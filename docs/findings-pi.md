# Pi integration findings

Verified against Pi **0.85.1**, installed from `@earendil-works/pi-coding-agent`.
The implementation uses the [native extension API](https://pi.dev/docs/latest/extensions),
not an assumption that MCP tool loading enables unsolicited input.

## Identity and installation

`session_start` provides a context with `cwd`,
`sessionManager.getSessionId()`, and `getSessionFile()`. The extension uses those
native values to register before any prompt, model request, or `hello` call. Neither
session nor directory is model-supplied. The daemon generates a `pi-tree-animal`
name and collision-checked bare eight-character ID using the shared identity core.
The name and ID appear in Pi's status line. Renaming retains IDs and old-name aliases.

`pi install /absolute/path/to/integrations/pi` installs the local package. Its
extension resolves the built executable in the checkout, preferring Release over
Debug; `COLLAB_MCP_BINARY` overrides that path. It launches the executable's native
stdio bridge, which starts the shared daemon as needed. Pi does not require OC2.
Saved sessions are required; ephemeral `--no-session` contexts are explicitly refused.

## Push, provenance, and ordering

The extension exposes `collab_hello`, `collab_roster`, and `collab_send` as native
Pi tools, preserving the same three verbs and argument contract as MCP while
avoiding generic tool-name collisions. Runtime-generated tokens travel separately
from those arguments. A private per-runtime Unix socket accepts daemon deliveries.
The daemon binds tokens to native endpoints; a live session cannot be replaced
by another process using a different token. Transport descriptors have a 45-second
lease refreshed every 15 seconds. These are same-user local capabilities, not a
sandbox against another process running as the user.

`pi.sendMessage(message, {triggerTurn: true, deliverAs: "followUp"})` supplies a
native `collab.peer` custom message. Its renderer marks it as agent input and shows
the sender's name and stable ID. Metadata in `details` is not sent to the model,
so sender identity and the distinction from user instructions/permission approvals
also appear in message content and the extension's system instructions. Pi's
provider conversion represents custom text as user-role API content; native
custom-message provenance and explicit text must therefore carry the distinction.

Idle input triggers a model turn and streams its response in the existing TUI.
Busy input enters Pi's follow-up queue and runs after the current work completes.
The extension also displays a submission notice while busy. It never calls
`sendUserMessage` to impersonate user input. Pi's `steer` semantics are insufficient
to claim the existing interrupt contract; `interrupt` is refused before socket I/O.

## Admission evidence

The public `pi.sendMessage` API returns void and handles asynchronous errors through
Pi's extension runner. Its busy follow-up queue is volatile. The adapter therefore
returns submission ownership while awaiting native receipt, rather than declaring
delivery from the extension API call or socket acknowledgement.

The adapter checks the exact `custom_message` entry, custom type, displayed content,
and sender/message metadata in the registered native session file. The file's
session header must match both the native session ID and project directory. Plain
user/assistant entries and ID-only matches are insufficient. Receipt paths persist
privately so reconciliation survives daemon restart without retaining live tokens.
An uncertain submission is never automatically resent and blocks later mail to
the recipient until positive evidence clears it. Before the first assistant entry,
Pi may defer writing the session file; receipt confirmation consequently waits for
native persistence. The extension suppresses duplicate pending IDs and checks
existing native custom entries before submitting another copy.

Quitting before consuming a queued follow-up can leave accepted mail without
positive evidence indefinitely. Recovery from absent admission remains shared
roadmap work, not a completed Pi feature.

## Lifecycle

Pi fires `session_shutdown` during quit, extension reload, and conversation
replacement. Shutdown first closes incoming admission, then disconnects the token
and bridge. Session replacement receives a fresh native ID and public identity;
reload and native resume preserve the existing identity. Closing a transport does
not tombstone a resumable conversation. Abrupt transport loss falls back to lease
expiry, and a still-connected duplicate session is refused.

## Reproducible checks

`scripts/check-pi.py` opens two actual Pi TUIs with private configuration/state and
a local scripted OpenAI-compatible provider. It verifies automatic registration
before any model call, distinct IDs, native attribution, roster, idle wake-up,
live streaming, peer provenance, busy follow-up ordering, rename aliases, unsupported
interrupt refusal, and close/resume continuity. Lifecycle reload/new-session checks
also exercise transport replacement and distinct new-conversation identity.

`scripts/check-oc2.py --mode pi` opens actual Pi and OC2 TUIs against private local
providers. Both delivery directions, a shared roster, idle wake-up, distinct peer
rendering, streamed responses, sender IDs, and native admission evidence passed.
Neither check uses user model credentials or paid model requests.
