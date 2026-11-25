# Build brief 4: conversations as events

Read `AGENTS.md`, `docs/build/log.md`, and the design: `docs/architecture/event-sourcing.html` (open it in a
browser, or read the HTML; every tab). The user approved that design and all its recommendations on 25 November 2026:

1. **Storage:** our own append-only `events` table in SQLite, behind a small interface so Postgres can come later. No
   Marten, no KurrentDB.
2. **What we record of an agent's work:** the facts. Prompts delivered, tool name, kind and input detail, output
   size and SHA-256, replies, approvals, turn outcomes. Full tool output is **opt-in per agent** (a switch on the agent,
   off by default).
3. **Streaming text:** live deltas go to browsers but are **not stored**. The answer is stored as `AgentReplied`
   segments: one at each tool-call boundary, one every ~5 s of continuous text, and one at turn end. A crash mid-turn is
   recovered at startup by appending `TurnInterrupted` for turns that started and never ended.
4. **Forgetting:** deletes are events that projections honour (`ChannelDeleted`, `MessageDeleted`). `MessageRedacted`
   replaces an event's content while keeping the fact. No crypto-shredding.

The work is three rounds. **Do only the round you were asked to do**, then report.

## The rules every round keeps

- The table is the one in the design: `position` (global order), `stream_id`, `version` (`UNIQUE(stream_id, version)`
  for optimistic concurrency, with a retry on conflict), `type`, `schema`, `data` (JSON), `actor_id`, `on_behalf_of`,
  `correlation` (the turn), `causation` (the position that caused it), `occurred_at`.
- Events are facts, named in past tense, small, and never carry view-only fields. Secrets (password hashes, token and
  code hashes) never go into events; they stay in a small table of their own.
- The feed, threads, channels and members are **projections updated inline**, in the same transaction as the append,
  so you always read your own writes. The other projections (audit, unread, agent activity) run **async**, from a
  checkpoint position, and can be rebuilt from zero.
- Behaviour doesn't change in rounds 1 and 2: `tests/live/phase1.mjs`, `phase2.mjs` and `phase4.mjs` must pass
  unchanged (step counts 24, 30, 17), plus `dotnet test` and `bun run test`. Add tests where the design calls for them.
- The user's own data is in the podman compose project `cowork` (volume `cowork_cowork-data`). Never touch that
  project or its volume. Use ports 5883–5889 and the compose project `cwp` for scratch stacks, and clean up by label.
- Commit when the round's demo works, log decisions in `docs/build/log.md`, look at any screenshots you take, and
  report to the session that sent you with `fleet_message`. Don't ask questions: decide, log the decision and carry on.

## Round 1: the event store and threads

**Demo:** all three journey checks pass unchanged. In a new `tests/live/phase5.mjs`, a browser whose connection
drops while reviewer answers, and comes back, shows exactly what it missed (the rest of the answer, the tool rows,
the approval) **without reloading the page**. The `events` table holds the thread's history as the design's "One
thread as events" tab shows it.

- The event store: append, read a stream, read all from a position, subscribe. Its interface keeps SQLite specifics out.
- The thread stream and channel top-level posts: `MessagePosted`, `ThreadStarted`, `AgentJoinedThread`,
  `MessageDelivered`, `TurnStarted`, `ToolCallStarted`, `ToolCallDetailed`, `ToolCallFinished`, `AgentReplied`
  (segments as above), `PermissionRequested`, `PermissionGranted` / `PermissionDenied`, `TurnEnded`, `TurnFailed`,
  `TurnInterrupted`. Every write path that today does `INSERT` or `UPDATE posts` appends events instead, and an inline
  projector maintains the `posts` and `threads` tables the app reads.
- Actor, on-behalf-of, correlation (one id per turn) and causation set on every event. An approval's
  `PermissionGranted` is caused by its `PermissionRequested`, and a turn's events carry the person who started it.
- Live: every event pushed to browsers carries its `position`. A reconnecting browser sends the last position it saw
  and gets everything after it for the channels it may see; the membership check happens per event. The deltas stay
  live-only.
- Startup recovery: turns with `TurnStarted` and no end get `TurnInterrupted`, and the thread says the answer was cut off.
- Tool output: the agent switch "Record full tool output" (off). Off records size and SHA-256 only; on also records
  the output text, capped at 64 kB per call.

## Round 2: everything else as events

**Demo:** a test rebuilds every projection from an empty database by replaying the events, and the app looks
identical (compare the API responses before and after). The user's existing data survives the upgrade.

- Streams for `server`, `channel-{id}`, `agent-{id}` and `person-{id}`, with the events in the design's "Streams and
  events" tab. Channels, members, people, agents, invites and channel membership become inline projections.
- `deliveries` goes: a runner catches up from its position on its agent's stream of `MessageDelivered` events.
- `ChannelDeleted` and `MessageDeleted` are events, projections hide what they delete, and the audit keeps the fact.
  `MessageRedacted` (admin or author) replaces the content in the event and the projections.
- **Importer:** on first start of an upgraded server, if the legacy tables have rows and the event store is empty,
  write synthetic events (`schema` 1, `data.imported = true`) that reproduce them, in order. Test it on a copy of a
  database from before the upgrade (make one with the current `main` build).
- Upcasting: the place where an old event's JSON is converted to the current shape, keyed by `schema`, with one test.

## Round 3: the first new views

**Demo:** in the app, an admin opens **Audit** and sees "Rosa asked reviewer (Alex's agent) to run `git log -3`. Alex
allowed it once. It ran on build-01", built from the causation chain. A person sees unread counts per channel and a
**Mentions** inbox. A new `tests/live/phase6.mjs` checks both with two browsers and a real runner.

- Async projections with checkpoints: audit (every event with an actor, readable sentences, filter by person, agent
  or channel), unread counts and a mentions inbox (`ChannelRead` on the person stream), agent activity (threads an
  agent is busy in, its turns and failures) shown on the agent's profile.
- Each projection can be rebuilt from zero with one admin action, and a test does it.
- Fleet-style screens (same `ui/` pieces as the rest): Audit under server settings for admins, unread badges in the
  channel list, Mentions at the top of the channel list.
