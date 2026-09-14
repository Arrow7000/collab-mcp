# Adversarial review — 2026-09-14

Reviewed commit: `0b23e75`. Reviewer: `gpt-5.6-terra`, high reasoning effort.
Sol at medium effort was conditional on Terra finding no actionable issues; it was
not launched during that initial review because Terra confirmed the finding below.
The user subsequently requested Sol after the fix; that follow-up is recorded below.

Review scope: original vision and roadmap, identity/addressing, migration and
persistence, routing/delivery/recovery, attribution and concurrency, transport,
resource bounds, and regression/integration coverage. The baseline working tree
was clean and all 105 Release tests passed. This is a review, not a guarantee that
all faults have been found. No implementation changes or live service restarts
were performed for this review.

## High — legacy address-shaped names become unusable after migration

Locations: `src/Collab.Domain/Router.fs:64`, `:167`, `:198`, `:203`, `:214`;
`src/Collab.Daemon/Store.fs:101`.

Earlier versions accepted display names such as `deadbeef`. Version-3 migration
preserves that name, but current lookup treats eight hexadecimal characters as an
ID before considering registered names or aliases. This creates an identity that
appears in the roster but cannot be addressed by its preserved name.

An isolated version-3 snapshot reproduction was independently confirmed:

1. Start with two registrations, Red and Blue, in one directory.
2. In a version-3 wire snapshot, change Red's name to `deadbeef` and represent the
   short IDs in their old `peer-xxxxxxxx` format.
3. Decode the snapshot with the current `StateWire.decode`.
4. The bound registration retains name `deadbeef`, but `RouterState.lookup` of
   that name returns `None` when no short ID has that value.
5. Send from that bound registration to Blue. The accepted envelope has neither
   `FromPeer` nor `FromAddress`, losing reliable reply identity.
6. Sends to `deadbeef` are refused as `UnknownPeerAddress`; repeating
   `hello("deadbeef")` is refused as `ReservedName`.

If another registration actually owns that short ID, the old name instead resolves
to that other identity. The compatibility problem also applies to legacy aliases,
earlier `peer-xxxxxxxx` names, and full-ID-shaped names permitted in version 1.

This breaks the documented preservation of existing names during migration and
can remove sender provenance. The existing tests cover ID continuity and old
addresses, but omit previously valid names that become reserved address syntax.

Recommended fix: define compatibility resolution explicitly. Preserve legacy names
and aliases where unambiguous, detect collisions before committing migration, and
provide actionable recovery for genuinely ambiguous addresses. Resolve a bound
sender from its endpoint/stable identity rather than reinterpreting its display name
as an address. Add regressions for each legacy address shape, aliases, sender
provenance, collisions, and migration persistence/restart.

## Other observations

Terra found no additional confirmed defects. Known roadmap omissions—including
session reconciliation, absent-input recovery, traffic/interrupt limits, loop
suppression, cross-harness delivery, leases, and permission isolation—remain open
and were not counted as newly discovered bugs.

Attribution's held-fact bound does not also bound outstanding waiters. This belongs
to the existing resource/traffic-limit work and should be included in its acceptance
checks. `senderMustAct` was examined but is not a confirmed defect: its contract
allows reporting/reconciliation, not just retry, and it has no current consumer.

## Resolution

The legacy-name finding is fixed in the subsequent implementation. Address resolution
now considers names/aliases and IDs separately, preserves unambiguous legacy names,
and refuses cross-identity ambiguity. Migration reports ambiguous addresses before
saving, leaving the original payload and audit unchanged. Endpoint-bound sender
registrations stamp provenance directly. Existing owners can repeat hello or restore
an address-shaped alias; new claims still reserve ID syntax. Allocation accounts for
normalized legacy address shapes. Regression coverage includes earlier snapshot
versions, names/aliases, sender identity, actual SQLite migration/restart, and collision
rejection without database changes.

Validation of the fix: committed as `02303ac`; all 122 Release tests passed,
Debug built without warnings or errors, and the two-real-OC2 scripted-provider
identity/bidirectional delivery check passed. The original isolated reproduction
now reports successful legacy-name lookup and preserved sender ID. The live daemon
was updated while retaining all five existing names, IDs, and session bindings.

## Sol follow-up — corrected version

Reviewer: `gpt-5.6-sol`, medium reasoning effort. Reviewed commit: `02303ac`.
Sol found no additional actionable defect in the legacy-name fix. It reported two
additional P2 findings, both independently reproduced by the parent agent.
No source changes were made during the review.

### P2 — delayed automatic registration resurrects a deleted session

Locations: `src/Collab.Daemon/Daemon.fs:338`, `:351`, `:353`;
`src/Collab.Adapters.OpenCode/OpenCodeApi.fs:417`.

Lifecycle announcements run in detached asynchronous work and may retry eligibility
checks for the project's collab MCP registry. Deletion handling runs independently.
That eligibility check establishes project connectivity, not session existence.

Reproduction: register an endpoint, hold its next announcement eligibility response,
process `SessionEnded`, then allow the delayed eligibility response to succeed.
`engine.Announce` binds that same deleted endpoint again with a new ID. The sequence
also resurrects an endpoint deleted before its first registration.

Impact: the roster shows a dead agent and its generated name becomes reserved again.
Both lifecycle events were received; this is a concurrency defect, not the already
planned recovery from missed events.

Correction: represent lifecycle ordering in the engine. Check a generation or deletion
tombstone atomically before accepting automatic registration, and cancel or discard
pending obsolete work. Test eligibility completing after deletion deterministically.
A check outside the engine alone would still leave a check/registration race.

### P2 — a slow tool call blocks every session sharing the shim

Location: `src/Collab.Shim/Mcp.fs:203` (incorrect single-session assumption at `:165`).

The stdio read loop runs each tool with `Async.RunSynchronously` before reading the
next request. OC2 shares this MCP process across sessions. A slow attribution join or
admission therefore blocks independent requests from other agents. The engine keeps
IO outside its serialized decision loop, but the shim reintroduces that blocking.

Reproduction: use a private fake Unix-socket daemon, start a roster request whose
response is held, then send MCP ping on the same stdio stream. Ping has no response
while the roster waits; it responds only after the fake daemon releases the tool.
This was reproduced against the Release executable without touching live services.

Impact: a lost-attribution call can block other agents' tools for ten seconds; a slow
admission can block them for sixty seconds. Even an independent ping is delayed.

Correction: use bounded concurrent request dispatch, serialize complete response
writes, and retain JSON-RPC request IDs. Add a regression proving that an independent
request completes while a tool waits, plus response framing/concurrency checks.

Recommended next order: lifecycle ordering and deletion safety, then bounded shim
concurrency. Re-run the two-agent integration check and adversarial regressions after
these changes. Known roadmap omissions remain separate from these findings.

## Sol findings resolution

Both Sol findings are fixed in the subsequent implementation:

- Definitive deletion records a persistent harness/session tombstone in version-5
  state. The engine checks it atomically during automatic registration and explicit
  naming. Late eligibility results cannot recreate a bound registration, including
  after daemon restart. Confirmed SessionGone delivery results apply the same guard.
- OC2 2.0.3's real deletion event has no location. The adapter now forwards that
  authenticated session deletion globally across its locations; creation and tool
  attribution continue to require real project context. This format was exposed by
  expanding the real integration check with rapid create/delete sessions.
- The shared shim forwards up to 32 tools concurrently and rejects excess work
  before forwarding. Control requests remain responsive. Complete JSON responses
  are serialized, preserve request IDs, and drain after input EOF. Tool exceptions
  receive an error response rather than silently losing an answer.

The original isolated reproductions now show ping available while the tool is held
and no binding after a delayed announcement for a deleted session. Automated
regressions cover both eligibility/deletion orderings, deletion persistence and
scope changes, current unscoped deletion mapping, overload rejection, concurrent
response framing, exception handling, and EOF drainage.

Validation: all 132 Release tests pass; Debug builds without warnings or errors.
Real OC2 checks pass for bidirectional identity-based delivery, rapid create/delete
with persistent tombstones, direct tools, and idle/busy TUI notices and streaming.
The live version-5 migration and shared MCP refresh preserved all five existing
identities, names, and bindings. The two-agent tests use an isolated scripted local
provider rather than paid or autonomous-model calls.
