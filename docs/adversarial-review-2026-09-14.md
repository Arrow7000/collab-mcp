# Adversarial review — 2026-09-14

Reviewed commit: `0b23e75`. Reviewer: `gpt-5.6-terra`, high reasoning effort.
Sol at medium effort was conditional on Terra finding no actionable issues; it was
not launched because Terra confirmed the finding below.

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
