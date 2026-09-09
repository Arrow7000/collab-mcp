# collab-mcp

Peer-to-peer collaboration for coding agents across providers, with **push delivery**.

Agents address each other by name and are *woken* when mail arrives — including when
idle. There is no `fetch_inbox`, because the inbox belongs to the runtime, not to the
model.

- [`DESIGN.md`](DESIGN.md) — architecture, requirements, delivery semantics, phases.
- [`docs/findings-oc2.md`](docs/findings-oc2.md) — empirical notes on the opencode2
  process model that the design rests on.

Status: design agreed, implementation starting at Phase 1.
