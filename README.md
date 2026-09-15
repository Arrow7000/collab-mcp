# collab-mcp

**Let coding agents talk to each other—and wake each other up.**

Open two agents in the same project. They appear in each other's roster automatically,
get readable names and short stable IDs, and can send messages across harnesses and
model providers. An incoming message wakes an idle agent; a normal message to a busy
agent waits for its turn boundary. You can watch the resulting work stream in the
agents' existing terminal UIs.

The model never has to check an inbox. It sends a message, ends its turn, and resumes
when a reply arrives.

```text
Red [a1b2c3d4] → Blue [e5f60718]: Can you review the parser while I fix the router?
Blue wakes, reviews it, and replies to a1b2c3d4.
Red wakes and continues—with both conversations visible in their own terminals.
```

## Three tools

| Tool | What it does |
| --- | --- |
| `roster()` | Lists agents in the current project, including yourself. |
| `hello(name)` | Optionally chooses or changes your display name. |
| `send(to, body, urgency?)` | Pushes a message to another agent. |

`to` accepts a current name, previous name, or the eight-character hexadecimal ID
shown by `roster`. IDs survive renaming, reconnecting, and daemon restarts. Previous
names remain reserved aliases for that conversation, so renaming doesn't break
ongoing exchanges. Incoming messages include the sender's ID for replies.

Pi exposes these tools as `collab_roster`, `collab_hello`, and `collab_send` to avoid
collisions with other extensions. MCP harnesses namespace them under the `collab` server.

Names are scoped to the project directory. Start agents in the same directory to
connect them; separate directories have separate rosters. Path aliases and symlinks
are not automatically merged.

## Supported harnesses

This is an early working implementation, with real terminal integration checks.

| Harness | Integration | Tested version | Incoming messages |
| --- | --- | --- | --- |
| **Pi** | Native extension | 0.85.1 | Custom agent messages; idle wake-up and busy follow-ups. |
| **OpenCode 2** | MCP plus the native event/delivery API | 2.0.3 | Synthetic peer input; turn-boundary and interrupt delivery. |
| **Claude Code** | MCP plus opt-in preview channels | 2.1.270 | Channel input; idle wake-up and busy follow-ups. |

`at_turn_boundary` is the default urgency. `interrupt` is supported only for OC2
recipients; Pi and Claude refuse it explicitly. Loading MCP tools alone does not
provide push support in an arbitrary harness. Pi uses an extension to connect its
running agent and UI to the same collaboration core.

Codex and Grok Build are not supported yet. Investigation notes and remaining work
live in [DESIGN.md](DESIGN.md#10-roadmap).

## Install

Download the self-contained archive for your machine from
[GitHub Releases](https://github.com/Arrow7000/collab-mcp/releases/latest). It includes
the .NET runtime; installing the .NET SDK is unnecessary.

```bash
# Choose one: linux-x64, linux-arm64, osx-x64, osx-arm64
VERSION=v0.1.0
PLATFORM=osx-arm64
ARCHIVE="collab-mcp-${VERSION}-${PLATFORM}.tar.gz"

curl -fLO "https://github.com/Arrow7000/collab-mcp/releases/download/${VERSION}/${ARCHIVE}"
curl -fLO "https://github.com/Arrow7000/collab-mcp/releases/download/${VERSION}/SHA256SUMS"
grep " ${ARCHIVE}$" SHA256SUMS | shasum -a 256 --check
tar -xzf "$ARCHIVE"
mkdir -p "$HOME/.local/bin"
install -m 755 collab-mcp "$HOME/.local/bin/collab-mcp"
```

Ensure `$HOME/.local/bin` is on the `PATH` inherited by your coding-agent process.
No separate daemon launch is needed: the integration starts it automatically.

Developers building from source need **.NET 10**:

```bash
git clone https://github.com/Arrow7000/collab-mcp.git
cd collab-mcp
dotnet build -c Release
```

The source-build executable is `src/Collab.Shim/bin/Release/net10.0/collab-mcp`.

### Pi

Install Pi if needed, then install the local extension:

```bash
npm install -g @earendil-works/pi-coding-agent
pi install /absolute/path/to/collab-mcp/integrations/pi
```

Open two Pi sessions from the same project directory. The status line shows each
agent's generated name and ID; agents can call `roster` immediately. Ask one to send
the other a message and end its turn.

The extension finds a built executable in this checkout or `collab-mcp` on `PATH`.
Set `COLLAB_MCP_BINARY` to an absolute executable path to override discovery.
Pi must use saved sessions; `--no-session` is unsupported. An OpenCode installation
is not required to use Pi.

### OpenCode 2

Merge this server into your user or project `opencode.json`:

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

Keep the server name `collab`. Use an OC2 V2 build that supplies MCP session metadata
(`ai.opencode/sessionID` or `sessionID`); the older beta-17519 is unsupported.
Both Code Mode and direct-tool mode are tested. See [OC2 findings](docs/findings-oc2.md).

### Claude Code — experimental channels

Claude push delivery currently requires Anthropic's **research-preview channel
feature**, including its development-channel launch flag:

```bash
claude mcp add --transport stdio --scope user collab -- collab-mcp
claude --dangerously-load-development-channels server:collab
```

Claude presents its own confirmation at launch. The flag enables this local,
unapproved development channel; it does not bypass tool permissions. Authentication
and applicable organization channel policies still apply. A normal MCP-only launch
is insufficient for push delivery. See [channel requirements](https://code.claude.com/docs/en/channels)
and [our verified Claude findings](docs/findings-claude-code.md).

## Distribution roadmap

- [x] Publish the source and run the core and real-Pi checks on macOS and Linux.
- [x] Build self-contained `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`
  executables on native GitHub runners for version tags; smoke-test each executable
  and publish SHA-256 checksums with the GitHub Release.
- [ ] Choose and add the project license before publishing through package registries.
- [ ] Publish an npm launcher/package that selects and verifies the matching native
  binary. This gives MCP clients a stable `npx` command and supplies the package
  artifact referenced by the official MCP Registry.
- [ ] Publish the Pi extension as an npm Pi package with the `pi-package` keyword,
  so installation becomes `pi install npm:<package>` and it appears in Pi's catalog.
- [ ] Add `server.json` for `io.github.arrow7000/collab-mcp`, validate it with
  `mcp-publisher`, publish it to the official MCP Registry, and check downstream
  catalog ingestion. The registry stores metadata; the installable package must
  already exist in a package registry.
- [ ] Ship a Homebrew formula for a one-command native installation and upgrades.
- [ ] Package the Claude integration as a Claude Code plugin and marketplace entry.
  Test installation from a clean profile before submitting it to Anthropic's official
  plugin marketplace. Full push delivery must still state and satisfy the channel
  preview/approval requirement.
- [ ] Add release signing/provenance, clean-machine install and upgrade tests, and
  documented uninstall/rollback before calling distribution stable.

The [engineering roadmap](DESIGN.md#10-roadmap) remains in `DESIGN.md`; this checklist
tracks how the finished adapters reach users.

## How it works

A small local daemon owns the shared identity registry, routing, and persistent
outbox. Each adapter translates the same delivery contract into its harness's native
input mechanism. Incoming messages retain agent provenance in the terminal and model
context.

The daemon stores its Unix socket, log, and SQLite database in the private
`~/.collab-mcp` directory. Accepted messages survive daemon restarts. A transport write
alone is insufficient to declare delivery: adapters check native admission evidence.
If the result is uncertain, the router retains ownership and does not blindly resend.

Inspect it with:

```bash
collab-mcp --status
```

Set `COLLAB_MCP_HOME` for isolated state. Pi and Claude identities represent native
conversations; closing a terminal does not imply deleting a resumable conversation.
Harness-internal workers need their own supported session integration to appear
individually.

## Development and verification

```bash
dotnet test -c Release
python3 scripts/check-pi.py
python3 scripts/check-oc2.py --oc2 /path/to/opencode2 --mode pi
python3 scripts/check-oc2.py --oc2 /path/to/opencode2 --mode claude
```

The optional integration checks open actual TUIs with private settings and local
scripted model providers. They require the relevant harnesses installed, but make
no paid model calls. OC2 also has `bootstrap`, `identity`, `codemode`, `direct`, and
`tui` check modes. CI runs the core tests on macOS and Linux.

- [DESIGN.md](DESIGN.md) contains the vision, architecture, delivery semantics, and roadmap.
- [Harness findings](docs/) record verified APIs, behavior, and integration limits.
- `src/Collab.Domain` holds the pure identity and routing core.
- `src/Collab.Adapters.*` contains harness-specific transports.
- `src/Collab.Daemon`, `src/Collab.Shim`, and `integrations/pi` connect the core to running agents.

## Current limits

Uncertain delivery with no positive native evidence can remain held indefinitely
and block later messages to that recipient. Safe recovery from absent admission
and cleanup of abandoned resumable conversations remain roadmap work. Pi's queued
follow-ups are volatile until recorded in its native session; quitting before
consumption can therefore leave a message held for confirmation.

Messages are limited to 16 KiB, with bounded per-recipient and global outboxes.
Peer provenance does not enforce a separate permission sandbox: file leases,
permission isolation, and conflict prevention are future coordination work. Agents
sharing a directory can still edit the same files unless they coordinate themselves.
