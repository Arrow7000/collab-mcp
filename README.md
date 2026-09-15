# collab-mcp

**Let coding agents in one project talk to each other and wake each other up.**

Start two supported coding-agent sessions in the same project directory. They join a shared roster automatically, receive readable names and stable short IDs, and can send each other messages. A message wakes an idle recipient; a message for a busy recipient is delivered at an appropriate point in its work. The conversation stays visible in each harness's existing terminal UI.

```text
Red [a1b2c3d4] → Blue [e5f60718]: Please review the parser while I fix the router.
Blue wakes, reviews it, and replies to a1b2c3d4.
Red wakes and continues.
```

## Compatibility and project status

collab-mcp is an early implementation. The versions listed below have real terminal
integration checks.

| Harness | Integration | Tested version | Incoming messages |
| --- | --- | --- | --- |
| **Pi** | Native extension | 0.85.1 | Wakes while idle; messages received while working are queued for a follow-up. |
| **OpenCode 2** | MCP integration | 2.0.3 | Wakes while idle; supports delivery at a turn boundary and interruption. |
| **Claude Code** | MCP plus preview channels | 2.1.270 | Wakes while idle; messages received while working are queued for a follow-up. |

Agents must start in the same directory to share a roster. Separate directories have separate rosters; path aliases and symlinks are not automatically merged.

Claude Code support is experimental because push delivery depends on Anthropic's research-preview channel feature. A standard MCP-only setup does not provide push delivery. Codex and Grok Build are not supported yet.

> **License status:** no project license has been specified. This is an unresolved
> project-owner decision, not an installation or README configuration issue.

Distribution and engineering plans are in [DESIGN.md](DESIGN.md#10-roadmap).

## First collaboration

1. Download the self-contained archive for your machine from [GitHub Releases](https://github.com/Arrow7000/collab-mcp/releases/latest), unpack it, and put `collab-mcp` somewhere on the `PATH` inherited by your coding-agent process. Choose the archive matching your platform: `linux-x64`, `linux-arm64`, `osx-x64`, or `osx-arm64`.
2. Configure one supported harness below. Pi also needs a repository checkout because its local extension is installed from that checkout.
3. Start fresh sessions of that harness in the same project directory after configuring it. No separate daemon launch is needed; the integration starts it automatically.
4. Ask one agent to call its roster tool, then send the other agent a message. Let the sending agent finish its response so the harness can return control to the session.

```text
roster()  →  Red [a1b2c3d4], Blue [e5f60718]
send(to: "Blue", body: "Please review the parser while I fix the router.")
```

Blue receives the request in its terminal. If Blue is idle, it wakes; if it is
working, the message is queued for the applicable delivery point. Blue can reply
using Red's displayed ID.

### Pi

Pi has two installation pieces: a **repository checkout** supplies the local
extension, while the `collab-mcp` executable may be the self-contained binary you
downloaded from Releases. These are separate paths. Clone the checkout, then install
Pi's extension from its `integrations/pi` directory:

```bash
git clone https://github.com/Arrow7000/collab-mcp.git
npm install -g @earendil-works/pi-coding-agent
pi install /absolute/path/to/collab-mcp/integrations/pi
```

Start two new saved Pi sessions from the same project directory. Their status lines
show the generated name and ID, which confirms that the collaboration identity is
present. Pi's tools are named `collab_roster`, `collab_hello`, and `collab_send`.

The extension finds a built executable in this checkout or `collab-mcp` on `PATH`. Set `COLLAB_MCP_BINARY` to an absolute executable path to override discovery. Pi must use saved sessions; `--no-session` is unsupported. An OpenCode installation is not required.

### OpenCode 2

Add this server to your user or project `opencode.json`:

```json
{
  "mcp": {
    "servers": {
      "collab": {
        "type": "local",
        "codemode": true,
        "command": ["collab-mcp"]
      }
    }
  }
}
```

Keep the server name `collab`. Start a fresh OpenCode 2 session after changing the
configuration, then confirm that its MCP tools include the `collab` server and call
`roster()` to confirm the session identity. Use an OC2 V2 build that supplies MCP
session metadata (`ai.opencode/sessionID` or `sessionID`); the older beta-17519 is
unsupported. Both Code Mode and direct-tool mode are tested. See
[OC2 findings](docs/findings-oc2.md).

### Claude Code (preview)

Claude Code support is experimental. Push delivery requires Anthropic's
research-preview channel feature and its development-channel launch flag:

```bash
claude mcp add --transport stdio --scope user collab -- collab-mcp
claude --dangerously-load-development-channels server:collab
```

Claude shows its own confirmation at launch. Start a fresh Claude session with the
server after adding the configuration, then confirm that the `collab` tools are
available and call `roster()`. The flag enables this local, unapproved development
channel; authentication and applicable organization channel policies still apply.
See [Claude's channel requirements](https://code.claude.com/docs/en/channels) and
[verified Claude findings](docs/findings-claude-code.md).

## Tool reference

MCP harnesses expose the tools under the `collab` server. Pi uses the prefixed tool names listed above to avoid extension-name collisions.

| Tool | Inputs | Accepted formats and behavior |
| --- | --- | --- |
| `roster()` | None | Lists the agents currently connected to this project, including the caller. Each entry includes a display name and an eight-character hexadecimal ID. |
| `hello(name)` | `name`: string | Optionally sets or changes the caller's display name. Names must be 1–64 characters and may contain letters, digits, hyphens, and underscores; whitespace-only names are rejected. |
| `send(to, body, urgency?)` | `to`: string; `body`: string; `urgency`: optional string | `to` accepts the current displayed name from `roster()`, any prior name retained as an alias, or an eight-character hexadecimal roster ID. Names and IDs match case-insensitively. `body` accepts any string, including an empty or multiline string, up to 16 KiB of UTF-8-encoded bytes. `urgency` accepts `at_turn_boundary` (the default) or `interrupt`. `interrupt` is available only when the recipient uses OpenCode 2; Pi and Claude Code reject it explicitly. |

IDs survive renaming, reconnecting, and daemon restarts. Previous names remain reserved aliases for that conversation, so an ongoing exchange is not broken by a rename. Incoming messages include the sender's ID for replies.

`at_turn_boundary` lets a busy agent complete its current turn before the message is delivered. `interrupt` asks OpenCode 2 to deliver immediately. An idle recipient is woken when a message arrives.

## What to expect

collab-mcp runs a small local service that keeps the roster, routes messages, and retains accepted messages across service restarts. It stores its Unix socket, log, and SQLite database in the private `~/.collab-mcp` directory. Check it with:

```bash
collab-mcp --status
```

Set `COLLAB_MCP_HOME` to use isolated state. Pi and Claude identities represent native conversations, so closing a terminal does not delete a resumable conversation. Harness-internal workers need their own supported session integration to appear separately.

## Current limits

If a harness has not confirmed accepting a message, it can remain held and later
messages to that recipient can wait behind it. The service checks again every
10 seconds and clears the hold only after the harness provides positive acceptance
evidence. Resuming or restarting the recipient can make that evidence available, but
does not guarantee recovery. Use `collab-mcp --status` to see counts of waiting,
delivering, and uncertain messages; there is currently no manual retry or drop
command. Pi follow-ups are not durable until Pi records them in its native session,
so quitting before they are consumed can leave a message awaiting confirmation.

Messages have bounded per-recipient and global outboxes. Collaboration does not add
file leases, permission isolation, or conflict prevention: agents sharing a directory
can still edit the same files unless they coordinate. See [DESIGN.md](DESIGN.md) for
the delivery model and protocol-level design.

## Contributing

Build from source and run the core checks:

```bash
git clone https://github.com/Arrow7000/collab-mcp.git
cd collab-mcp
dotnet build -c Release
dotnet test -c Release
```

The source-build executable is `src/Collab.Shim/bin/Release/net10.0/collab-mcp`. For harness work, the optional checks use real terminal UIs with private settings and local scripted model providers, with no paid model calls:

```bash
python3 scripts/check-pi.py
python3 scripts/check-oc2.py --oc2 /path/to/opencode2 --mode pi
python3 scripts/check-oc2.py --oc2 /path/to/opencode2 --mode claude
```

[DESIGN.md](DESIGN.md) describes the architecture, delivery semantics, and planned work. [Harness findings](docs/) record verified APIs, behavior, and integration limits.
