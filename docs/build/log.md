# Build log

Decisions the brief left open are marked **Decision**. Nobody was awake to ask, so each is the most reasonable option.

## Phase 1: the spine (people, channels, live posts, threads)

**Works:** first-run setup (server name, admin account, `#general`), cookie sign-in and sign-out ("Wrong username or
password."), invite links (7 days, 10 uses, listed and revocable, with revoked / expired / used-up messages), public and
private channels, channel settings (rename, topic, add or remove people and agents, delete), admin and member roles
("People and agents" in the server menu: make admin, remove), live posts over SignalR, threads on any post with "N replies".
Dark and light from theme tokens.

**Checked:** `tests/live/phase1.mjs`, two browser contexts (Alex, Rosa) against a scratch server: journey steps 1–6, 24 checks
(screenshots `docs/screens/phase1-*.png`). `dotnet test` (auth, membership including a hub `Subscribe` refusal and no events
leaking from a private channel, invite rules, author-from-caller) and `bun run test` (event reducer, renderer).

**Decisions:**
- Public channels need no "join": every active person can see and post in them. Agents always need an explicit membership row.
- Anyone can create a channel (they become its creator and can manage it); only admins create invite links.
- Removing a person removes their agents too. The last admin can't be demoted, and you can't remove yourself.
- Thread id = the root post's id. A thread row is created on the first reply or on a mention.
- Fonts are the system stack (no Google Fonts request: it's self-hosted).

## Phase 2: a real agent in a thread

**Works:** "Invite an agent" (name, about, instructions, harness command, workspace) → one-time code (10 min, single use,
stored hashed) and the exact command → `cowork-runner connect <server> --code …` enrols, saves
`~/.config/cowork-runner/agents.json` (0600) and stays connected; the dialog flips to "reviewer is connected" and offers
channels. Mentions open a thread on the post; the thread is one ACP session per agent on the runner; replies are prompts
wrapped as `<cowork-message from="@rosa" …>`; text streams into thread posts and tool calls show as compact rows; permission
asks show Allow / Deny to the owner only (others see "waiting for Alex"); one turn at a time per (thread, agent) with a
queue; a runner that is offline leaves the mention waiting ("Waiting for reviewer's runner") and it goes out on reconnect;
agent-to-agent turns stop after 3 without a person; removing or disconnecting an agent cuts its token and tells the runner.

**Checked:** `tests/live/phase2.mjs` with a REAL runner and REAL OpenCode 2.4.1 (`opencode acp`, scratch `HOME` and `XDG_*`
under `~/.cache/cowork-oc2/`, free model `opencode/big-pickle`): steps 7–10. Unit and integration tests with a fake ACP agent
(`tests/CoWork.Runner.Tests`) and fake runners over the real hub (`tests/CoWork.Server.Tests/RoutingTests.cs`): enrolment,
mention → thread → delivery, reply → same session, queueing, offline, stream → posts, owner-only answers, loop limit.

**Which harness worked:** OpenCode 2 through `opencode acp`. Claude Code via Zed's adapter wasn't needed.

**Decisions and findings:**
- The runner has one hub connection per agent (token per agent), so one runner process serves several agents.
- The ACP client is my own small one (adapted from the design of Weave Fleet's `AcpClient`), not a copy of Fleet's: Fleet's is
  tied to its own session model.
- OpenCode 2 with its default config never asked permission in my first runs, but with `{"permission":{"bash":"ask","edit":"ask"}}`
  in its config (scratch `XDG_CONFIG_HOME/opencode/opencode.json`) it asks, and the whole owner-only flow worked live.
- A restarted runner starts each thread's session afresh (no `session/load` yet); noted in "not built yet".
- If the runner dies mid-turn the turn is marked failed and the thread gets a notice; the message isn't re-sent.
- Ordering: the ACP client handles notifications and responses in arrival order, and the adapter sends events to the server
  through a single queue, so a `turnEnded` can never overtake the last text.

## Phase 3: self-host it

**Works:** `deploy/Dockerfile` (bun builds `web/`, `dotnet publish` the server, runs as non-root uid 10001), `deploy/docker-compose.yml`
(port 5880, SQLite on the `cowork-data` volume, commented Caddy service and `deploy/Caddyfile.example`),
`scripts/publish-runner.sh` (single-file self-contained runner; linux-x64 built and run here, osx-arm64 and win-x64 are the
same command with another runtime id), `README.md`.

**Checked:** from a clean tree, `podman compose -f deploy/docker-compose.yml up -d --build` started the container (non-root), and
`tests/live/phase2.mjs` (two browsers, a runner on the host, real OpenCode 2) passed all 24 checks against it on port 5880,
which covers setup, sign-up through an invite, the agent, threads, permissions, offline and removal. Then I removed the
container, network and volume.

**Decisions:** the image has no curl or wget (a smaller image); check it with `podman exec … dotnet` or from the host.
Compose has no healthcheck for the same reason.

## Phase 4: voice channels

Built in brief 2, Part B: see the end of this log.

## Final check

Journey steps 1–6 are `tests/live/phase1.mjs` (24 checks, a fresh server) and steps 1 (setup) to 10 are
`tests/live/phase2.mjs` (24 checks, a fresh server, real runner, real OpenCode 2), the latter against the container build.
Run them in order against fresh servers to repeat. Tests: 17 server, 7 runner, 9 web, all passing.

## Known rough edges
- People have no presence indicator or unread counts; the agent has online / offline.

## Brief 2, Part A: polish

All six fixes, one commit. Checked: unit tests (11 runner, 18 server, 9 web build), `tests/live/phase1.mjs` and `phase2.mjs` (now 29 checks) on a scratch server on 5883 with a real runner and real OpenCode 2.4.1.

1. The invite dialog names the agent ("where reviewer should work"); the server now returns `handle` with the one-time code.
2. The waiting line is one paragraph under the command.
3. Inter and JetBrains Mono are bundled (`@fontsource-variable/*`, no CDN).
4. The session line shows `opencode acp`; the full command is the tooltip.
5. The runner saves thread → harness session id in `sessions-<server hash>-<agentId>.json` beside `agents.json` (0600). After a restart it `session/load`s the saved id when the harness says `loadSession: true`, else (or if loading fails) `session/new`. The load replays history as `session/update`; the session is only registered after the load returns, so the replay is dropped. A loaded session doesn't get the instructions again. Live: the restarted runner logged "loaded session" for the thread.
6. Agents are editable by owner or admin (`GET/PUT /api/agents/{id}`, `EditAgent.vue`, from People & agents and the member list). The instructions aren't in the member list sent to everyone; only the edit endpoint returns them. Changes apply to new threads; existing sessions keep theirs.

**Found while live-checking, fixed:** a tool call whose `locations` is an empty array threw inside the ACP read loop (`locs[0]` out of range). That killed the loop, so the turn ended with "The harness closed the connection" and the runner needed a restart. `Describe` now checks the array, and one odd update is skipped and logged instead of stopping the session. The harness's stderr and the reader's exception now go into the error notice.

## Brief 2, Part B: voice channels

**Works:** channels have a `kind` (text | voice; migration 002). A voice channel is created (🔊 Voice in the create dialog), renamed,
made private and deleted like a text channel; it has no posts or composer (the server refuses posts to it). Joining a voice channel:
the browser asks `POST /api/channels/{id}/voice/token` (503 "Voice isn't set up on this server" unless `LIVEKIT_API_KEY` and
`LIVEKIT_API_SECRET` are set; 404 if the person can't see the channel; 400 if it isn't voice), connects to LiveKit with `livekit-client`,
publishes the microphone, then tells the server over the hub (`JoinVoice`) so the roster is pushed (`voice-presence`) to everyone who can
see the channel. The sidebar shows who is in each voice channel under it, live, without joining. Tiles with a speaking ring (LiveKit's
active-speaker detection), mute and leave; a call continues while you browse other channels (bar at the bottom of the sidebar).

**Token:** `LiveKitToken.Create` in `Voice.cs`, written by hand: HS256 JWT, `iss` = API key, `sub` = member id, `name` = display name,
`video` = {room: `channel-<id>`, roomJoin, canPublish, canSubscribe, canPublishData}, `nbf`/`exp` (1 hour: it's only needed to connect).
No LiveKit SDK on the server. Unit test checks the signature, claims and base64url.

**Checked:**
- 22 server tests (4 new voice tests: token claims and signature, 503 / 404 / 400 / 401, presence over real hub connections incl. a dropped
  connection leaving, bootstrap with voice on and off).
- `tests/live/phase4.mjs` (18 checks): two Chromium contexts with fake microphones against a real LiveKit 1.13.11 container: both tiles
  appear, each browser has the other's audio track subscribed (`data-hearing`), the speaking ring lights, mute shows for both, both channel
  lists show both people live, leave, closing a tab drops the person, rename / private / delete, and a second server with no LiveKit
  settings shows "Voice isn't set up on this server" with the app otherwise unaffected.

**Decisions:**
- Presence is tied to the hub connection (join and leave are hub calls), so a closed tab leaves by itself and the server needs no LiveKit
  webhooks or API. A person in two tabs counts once; one connection is in at most one room.
- `LIVEKIT_URL` (public address of LiveKit) is optional; without it the web app uses `ws(s)://<page host>:7880`.
- Compose: `livekit` is under the `voice` profile so the default `docker compose up` is exactly what Phase 3 shipped. It uses host
  networking (LiveKit's own recommendation; a bridge network announces container IPs browsers can't reach) and is configured inline with
  `LIVEKIT_CONFIG` (ports, one UDP port 7882, TCP fallback 7881, `use_external_ip`) and `LIVEKIT_KEYS` from the same env vars as the
  server. Image pinned to `v1.13.11`. `COWORK_PORT` moves the web port.
- `livekit-client` is loaded on first join (dynamic import), so the main bundle stays at ~215 kB.
- A LiveKit room keeps a participant for ~20 s after its browser dies, so re-running phase4 against the same LiveKit with the same ids
  shows ghost tiles. The live-check script's helper restarts LiveKit between runs; this is the test's problem, not the product's.

**Not done / limits:** making a voice channel private doesn't kick people already in the call; agents in voice, video and screen sharing
stay on the Later list. Mute state shown to others is LiveKit's track mute; speaking uses LiveKit's level threshold.

## Brief 2: final check

One container stack built with `podman compose -p cwp --profile voice up -d --build` from a clean clone of `main` (cowork on 5883, LiveKit 1.13.11 on 5885/5886/5887, `LIVEKIT_USE_EXTERNAL_IP=false`): `phase1.mjs` (24 checks), `phase2.mjs` (real runner, real OpenCode 2.4.1) and `phase4.mjs` (voice, 16 checks; the no-voice check ran earlier against a scratch server) all passed, each against a freshly emptied database. Then the containers, network and volume were removed by their `cwp` label.

- Rootless podman here can't `compose down` or `restart` (netns permission error); `exec rm` of the DB + `podman stop` + `podman start` is how the database was reset between phases.
- Flaky-test fix: the fake microphone beeps on and off, so `phase4` records when a tile lights (a MutationObserver) instead of polling for the attribute.

## Brief 2: independent check, round 2

The independent check (README quick start, `.env` from `.env.example`, compose with the `voice` profile on :5880) failed two places; both fixed and `run2.sh` now passes phase1, phase2 (29 checks) and phase4 on fresh stacks.
- **Runner didn't show offline when stopped.** The runner handled only Ctrl+C and `ProcessExit`, so SIGTERM ended the process without closing its hub connection, and behind the container port proxy the server didn't notice until SignalR's 30 s timeout. Now SIGTERM is handled (`PosixSignalRegistration`) and the links are disposed cleanly, and the runner hub has a 3 s keep-alive / 10 s client timeout (runner client keep-alive 3 s), so even a SIGKILL shows offline within ~10 s. The hard-kill path is by configuration; I checked the SIGTERM path live, not SIGKILL.
- **Voice quick start didn't connect.** `use_external_ip` defaulted to true, which announces an address that isn't reachable on one machine. The default is now `false` (compose, `.env.example`, README say when to turn it on). A failed join now times out after 20 s and says what to check, instead of hanging on "Joining…".

## Brief 3: a Weave Fleet makeover

Look and feel only; behaviour is unchanged. Checked on scratch servers (ports 5883, 5884) with a real runner and real OpenCode 2.4.1 and a real LiveKit 1.13.11 (container `cwp-lk-scratch`, label `cwp`, removed afterwards; the `cowork` compose project and its volume were never touched): `phase1.mjs` (24 checks), `phase2.mjs` (24 to 29, depending on whether the harness asks permission), `phase4.mjs` (18), `dotnet test` (11 runner, 22 server), `bun run test` (19 web, including a new contrast test over every theme).

**What changed**
- **Tokens and themes:** Fleet's `main.css` is copied as `web/src/assets/main.css` (same variable names, all ten themes, `@theme inline`), so `web/` moved from Tailwind 3 to **Tailwind 4** (`@tailwindcss/vite`) to take it as it is. `theme.ts` holds the theme list; the default is Default dark, "System" resolves to dark or light, the choice is kept in `localStorage` under Fleet's key `weave:theme`. Picker: user menu (bottom of the channel list) and a Settings dialog from the rail. Inter Variable and JetBrains Mono Variable stay bundled.
- **Pieces:** Fleet's `ui/` button, dialog, alert-dialog, dropdown-menu, tooltip, input, textarea, select, switch, avatar, badge, empty-state, error-note, spinner, relative-time and the confirm host are copied under `web/src/components/ui/` (reka-ui, lucide). Only edits: lucide icons instead of `@radix-icons/vue`, and `rounded-btn` / `rounded-panel` / the menu shadow on input, textarea, select, badge, dialog and tooltip so they match the rest of Fleet's look. CoWork's own small pieces: `MemberAvatar` (person = circle, agent = rounded square, status dot), `AgentBadge`, `StatusDot`, `FormField`, `AppDialog`.
- **Shell:** Fleet's layout: rail (logo, Server, People & agents, Settings, with tooltips), the channel list on the window background (Channels and Voice channels headers, 32 px rows, picked state, voice members under their channel), then one raised panel holding the channel, the thread and the member list. `window.confirm` is gone: removing a person or agent, disconnecting, and deleting a channel use `confirmAction`.
- **Status:** agent online / working / waiting / offline use Fleet's status colours (`--running`, pulsing for working, `--idle` for waiting).
- **Phone (≤ 767 px):** the channel list (with the rail) and the member list are sheets behind menu and members buttons; a thread takes the full width; nothing scrolls sideways (checked in the screenshot script).

**Decisions**
- Default theme is Default dark (the brief), not "System" as in Fleet; System is the first entry in the picker.
- The member list stays beside an open thread from 1280 px up (the brief's screenshot shows both); below that the thread replaces it, as before.
- No unread weights on channel rows: the server has no unread data and behaviour must not change. Rows have the picked state and hover only.
- The server has no categories, so the channel list groups by kind (Channels, Voice channels).
- Native controls were replaced by Fleet's Switch (private channel) and Select (add to channel); native checkboxes remain for "which channels should the agent join" (Fleet has no checkbox piece). The live scripts were updated for the changed controls (user menu before sign out, switch and select clicks, `confirm-action` instead of `confirm-delete`, an icon instead of 🔒 and 🔊). Every `data-testid` otherwise stays.
- `tests/live/lib.mjs` takes `SHOTS_DIR`; `tests/live/makeover.mjs` produces the screenshots in `docs/screens/makeover/` from a real server, runner and LiveKit (`~/.cache/cowork-scratch/mk.sh` starts those).
- A phase 2 run when a stale scratch server still held the port failed at setup; stop scratch servers by port before each run.

## Brief 3 follow-ups: avatars, tool-row detail, a phase 2 that can't skip

1. **Avatars:** each member's colour is stable, picked by id from an eight-colour palette (`web/src/avatarColors.ts`); persons stay circles, agents rounded squares. `theme-contrast.test.ts` checks white initials at 4.5:1 on each colour (the fill doesn't change with the theme, so the ratio holds in every theme) and that no two colours match.
2. **Tool rows:** a real OpenCode 2.4.1 run is logged in `docs/build/oc2-tool-calls.jsonl` (made with `~/.cache/cowork-scratch/acp-raw.py`). The first `tool_call` is bare (`rawInput: {}`, `locations: []`, title = the tool's name; a shell call's location is only its cwd). The detail arrives in the following `tool_call_update` with `status: in_progress`: `rawInput` (`pattern`, `path`, `command`; a grep's `path` is where it looked, not what it looked for) and a refined `title` for shell (`ls src`). The `completed` update carries neither. So the adapter now emits a new `toolUpdate` stream event for a non-final update that has detail; the server fills in the row's title without ending the tool; the completed update no longer overwrites it. Searches name their pattern, reads their path (relative to the thread's workspace), shell the command. Rows read `read  src/Api.cs`, `search  grep roundTax`, `execute  ls src`. Live: phase 2 now fails if a tool row is only a bare tool name.
3. **phase2:** the owner-approval steps no longer skip. The scratch OpenCode config asks for every shell command; the script posts a new top-level mention with a command that has a random nonce, so each run opens a new thread and no earlier "allow" applies, and the script FAILS (with that message) if no ask card appears. Rosa's checks open that thread. 30 checks now, always.
- Screenshot script: the reviewer is told to use read, grep and glob only, so the screenshots show no machine paths and no ask cards pile up. The thread header still shows the machine's host name (it is the runner's real host).
- phase4's speaking-ring check (a fake microphone that beeps on and off) timed out once in this round and passed on two reruns; it is flaky, not changed by this work.

## phase4: the speaking-ring flake, rooted out

Chromium's default fake microphone is a faint, intermittent beep; LiveKit's active-speaker detection sometimes never fired inside the 45 s wait. `phase4.mjs` now writes a loud (0.8 amplitude), continuous 440 Hz WAV and launches Chromium with `--use-file-for-fake-audio-capture` (alongside the fake-device flags). The step has two parts: (1) each tab measures the RMS of the subscribed remote audio track with Web Audio and requires > 0.01 (it measured 0.08 to 0.30), which tests our subscription and playback and doesn't depend on LiveKit's threshold; (2) the speaking ring (`data-speaking`) must light on the other person's tile within 30 s.
**Result:** 5 runs in a row, 5 passes (each run with a freshly started LiveKit 1.13.11 and fresh servers; the helper restarts LiveKit between runs because it keeps a dead participant for about 20 s, which would show ghost tiles with the same ids).

## Brief 4, round 1: conversations as events (the event store and the thread stream)

**Demo:** the three journey checks pass unchanged (24, 30, 17); the new `tests/live/phase5.mjs` (22 checks) drops a browser's connection while reviewer answers, brings it back, and the page shows exactly what it missed without a reload. (`phase5.mjs` writes screenshots of the caught-up page and the cut-off page; they show the machine's home path and host name, so they aren't committed.)

**What changed**
- **Store:** migration `003_events.sql` adds the design's `events` table (`position`, `stream_id`, `version` with `UNIQUE(stream_id, version)`, `type`, `schema`, `data`, `actor_id`, `on_behalf_of`, `correlation`, `causation`, `occurred_at`). `IEventStore` (`Events.cs`): `Append` (a list, or a decider that chooses the events inside the write transaction), `ReadStream`, `ReadAll(from)`, `Head`, `Subscribe`. `SqliteEventStore` writes under `BEGIN IMMEDIATE` and a process-wide lock (commit order is position order, so subscribers hear events in order), supports `ExpectedVersion` (`ConcurrencyException`) and retries a unique-version collision by deciding again. Inline projections (`IInlineProjection`) run in the same transaction.
- **Thread stream:** every write that was an `INSERT` or `UPDATE` on `posts` / `threads` / `thread_agents` is an event now, and `ThreadProjection` maintains those tables (plus `deliveries`, see below). Top-level `MessagePosted` goes to `channel-{id}`; everything else to `thread-{rootPostId}`: `ThreadStarted`, `MessagePosted` (replies), `AgentJoinedThread`, `MessageDelivered`, `TurnStarted`, `ToolCallStarted/Detailed/Finished`, `AgentReplied` (segments), `PermissionRequested`, `PermissionGranted/Denied`, `TurnEnded`, `TurnFailed`, `TurnInterrupted`. The old `Dispatcher.Route/EmitPost/Notice` are gone; `Dispatcher.Post` records a post, its thread and what is delivered to whom in ONE transaction. `ChannelDeleted` is appended on delete (the rows are still deleted the old way: round 2).
- **Actor, on behalf of, correlation, causation:** actor = the author / the agent / the person answering; `on_behalf_of` = the person whose request it is (carried through agent-to-agent hops); `correlation` = one turn id from `MessageDelivered` to `TurnEnded`; causation: `ThreadStarted` ← the post, `TurnStarted` ← its `MessageDelivered`, `PermissionGranted` ← its `PermissionRequested`, turn events ← `TurnStarted`.
- **Live:** one ordered queue (`LiveQueue` → `LivePublisher`) turns each committed batch into the SignalR messages the app already knew (`post-created`, `post-updated`, `thread-updated`), each with `at` (the event's position); post bodies carry `position` (the last event that changed the post). The reducer keeps the newer of two copies of a post.
- **Catch-up:** hub method `CatchUp(after)` returns the missed effects for the channels the caller may see (checked per channel of each event) as `post-upsert` / `thread-updated` messages, oldest post first, plus `head`. `/api/bootstrap` says `position`; the browser tracks the highest `at`, and on reconnect asks, with live messages held until the answer is applied. More than 5,000 events behind, or ahead of the server (a reset database): `truncated`, and the browser reloads its feed. The reconnect policy never gives up now (0, 1, 3, 5 s, then every 10 s); it used to close for good after 19 s.
- **Streaming text:** deltas go live only (`post-delta` with an `offset`, through the same ordered queue so they never overtake their post) and are not stored. The answer is stored as `AgentReplied` segments: the first words at once (the post exists for anyone who catches up), one every 5 s of continuous text (`Dispatcher.FlushIntervalMs`, a timer), one at each tool-call boundary and one at turn end (`complete: true`).
- **Recovery:** at startup `RecoverInterruptedTurns` appends `TurnInterrupted` for each `TurnStarted` with no later `TurnEnded` / `TurnInterrupted` / ending `TurnFailed` in its stream; the projection ends the delivery, finalises the half-written post and adds a notice ("The server restarted while this answer was being written, so it was cut off.").
- **Tool output:** agent switch **Record full tool output** (edit dialog, off by default; column `agents.record_tool_output`; `GET/PUT /api/agents/{id}` carry `recordToolOutput`; `DeliverMessage.RecordToolOutput` tells the runner). The runner always sends a finished tool's size and SHA-256 (`StreamEvent.OutputBytes/OutputSha256`) and the text only when the switch is on, capped at 64 kB; the server enforces both again. `ToolCallFinished` holds them; the `posts` rows never do.

**Decisions**
- **Ids come before the row.** A post's id (and a delivery's) is allocated inside the append transaction (`IDecisionContext.NextId`, from `sqlite_sequence` plus a counter) and written in the event's data (`postId`, `deliveryId`), so replaying reproduces the same ids and later events can name the post they change. A thread's id is still its root post's id.
- **`deliveries` stays a table** (round 2 removes it) but is now a projection: `MessageDelivered` inserts a queued row, `TurnStarted` marks it sent, `TurnEnded` done, `TurnFailed {ended: true}` / `TurnInterrupted` failed (or queued again when `requeued`, the runner couldn't be reached). `RemoveAgent`'s own `UPDATE deliveries` for queued rows is untouched until round 2; it first ends the agent's turns with `TurnInterrupted` so recovery doesn't find them open.
- **Where "notice" posts come from:** `TurnFailed` (reason) and `TurnInterrupted`. A runner's `error` stream event is a `TurnFailed` with `ended: false`, because the runner always sends `turnEnded` right after it; a runner going offline is `TurnFailed {ended: true}`.
- **A graceful server stop is not a crash:** the runner's connection closes, so the in-flight turn becomes `TurnFailed` "the runner went offline during this turn" (as before). `TurnInterrupted` is what a crash or kill leaves (phase5 uses SIGKILL).
- **Runner connect race fixed:** `RunnerConnected` now fails stale in-flight turns BEFORE registering the runner, so a delivery sent the moment it connects can't be mistaken for an old turn (the new tests exposed it).
- **Runner ordering:** a permission ask now waits until the events before it have been sent (`AcpAdapter.Sent()`), so the history reads tool call, detail, ask, not ask first.
- **SignalR receive limit** on the runner hub is 1 MB (the default 32 kB would drop a connection on 64 kB of output).
- **Streaming posts after a reconnect:** deltas missed while offline aren't stored, so a reconnecting browser sees the answer up to the last stored segment (≤ 5 s behind), then the next stored segment catches it up and the final one completes it. The reducer never shortens a streaming body and ignores a delta that would leave a gap.
- **Data from before the upgrade** has no events and `posts.position = 0`; it keeps working. The importer is round 2.
- `ThreadAgentStatus` (online, busy, queued) isn't an event (it's the runner connection): runner connect and disconnect still broadcast `thread-updated` without `at`.

**Checked**
- `dotnet test`: 13 runner (1 new: output size, hash, switch, 64 kB cap) and 37 server tests (15 new in `EventTests.cs`: store positions, versions, replay, causation by index, `ExpectedVersion`, rollback and ordered subscribers; a mention's events and their actor / on-behalf-of / correlation / causation; segments at tool boundaries; the 5 s flush (set to 150 ms); approval causation and denial; the tool-output switch on and off, 64 kB cap; recovery on a second server over the same database, once; live messages carry `at` and `position`; catch-up per channel visibility, nothing new from the head, ahead-of-server `truncated`; bootstrap `position`; `ChannelDeleted`). Run twice, same result. `bun run test`: 26 (7 new reducer tests).
- Live, on scratch servers (ports 5883, 5884, 5886; the `cowork` compose project and its volume untouched): `phase1.mjs` 24 checks, `phase2.mjs` (real runner, real OpenCode 2.4.1) 30, `phase4.mjs` (its own count: 19 on this run; the script is untouched), all unchanged; `phase5.mjs` 22.
- `phase5.mjs` uses a REAL runner and a scripted ACP agent (`tests/live/scripted_acp_agent.py`, which waits for gate files so the test chooses when it speaks): a real harness answers on its own clock and can't be dropped at a chosen moment. Alex's connection is dropped with Playwright (`routeWebSocket` closed, negotiate aborted); while it is down Rosa's page shows the rest, Alex's doesn't; after it returns Alex's page shows the same thread as Rosa's, the page is the same window with no navigation and made no feed API request (only `negotiate` and `bootstrap`), and the owner's buttons work from the page that caught up.

**For round 2**
- Streams `server`, `agent-{id}`, `person-{id}` and channel events don't exist yet; `channel-{id}` only holds top-level `MessagePosted` and `ChannelDeleted`. The importer must write `ThreadStarted`/`MessagePosted`/`AgentJoinedThread`/`MessageDelivered` etc. for existing threads with `data.imported = true`, using the existing post ids as `postId` (and the projection's explicit-id inserts will accept them). Posts of kind tool / ask / notice need synthetic `ToolCallStarted` / `PermissionRequested` / `TurnFailed` events.
- `ThreadProjection` is the model for the others. `Effects.From` (events → what a screen changes) is where a new event type needs a line, for live and catch-up both. Catch-up compares by channel: events for channels that no longer exist are skipped.
- Upcasting has no home yet: `StoredEvent.Data` is read as stored (`schema` is always 1); add it in `SqliteEventStore.ToEvent`.
- `TurnStarted` doesn't carry the harness session id (the server doesn't know it); the runner would have to report it.
- `phase2.mjs` failed its approval steps twice in a row ("the harness did not ask permission") while I worked: the runner's saved sessions (`~/.cache/cowork-oc2/config/cowork-runner/sessions-*.json`) outlive the scratch database, so thread 6 of a fresh database reloaded an OpenCode session from an earlier run in which the model had refused the shell command, and refused again. Delete `sessions-*.json` there before each run (then it passed, 30 checks). Not caused by this round; the wrapped message is unchanged.

## Brief 4, round 2: everything else as events

**Demo:** a test rebuilds every projection table from the events, in place and into a brand-new migrated database that has only the events, and the tables and the app's answers are identical (`RoundTwoTests.Rebuilding_every_projection…`). A database made by the round-1 `main` build (commit 291ef6b) is upgraded on first start and the app answers exactly what the old server answered (`ImporterTests`, with a fixture and the old server's own API answers). Behaviour is unchanged: `phase1` 24, `phase2` 30, `phase4` and `phase5` 22 pass; the only edits to the scripts are phase2's, listed below.

**What changed**
- **Streams and events** (the design's "Streams and events" tab). `server`: `ServerSetUp`, `PersonJoined` (with the role and, when it was an invite, the invite id), `RoleChanged`, `PersonRemoved`, `InviteCreated`, `InviteUsed`, `InviteRevoked`. `channel-{id}`: `ChannelCreated`, `ChannelRenamed` (with the old name), `TopicChanged`, `ChannelMadePrivate` / `ChannelMadePublic`, `MemberAdded` / `MemberRemoved`, top-level `MessagePosted`, `MessageDeleted`, `MessageRedacted`, `ChannelDeleted`. `agent-{id}`: `AgentCreated`, `AgentEdited`, `EnrolmentCodeIssued`, `RunnerEnrolled`, `RunnerConnected` / `RunnerDisconnected`, `TokenRevoked`, `AgentRemoved`. Setup is three events in one write (`ServerSetUp`, `PersonJoined`, `ChannelCreated` for #general), actor = the new admin.
- **Inline projections**: `DirectoryProjection` (new) keeps `members`, `people`, `agents`, `invites`, `channels`, `channel_members` and the server name; `ThreadProjection` keeps the rest. Every API write that was an `INSERT`/`UPDATE`/`DELETE` on those tables appends events now. Checks that must not race (a channel or handle name is unique, an invite has a place left, "at least one admin", an enrolment code is used once and before it expires) are made again INSIDE the write, in the decider, so two requests at once can't both win (a test fires six at once).
- **Secrets stay out of the log.** Migration `004` moves password hashes to `credentials`, runner token and enrolment-code hashes to `runner_secrets`, and invite codes to `invite_codes`; the commands write them in the same transaction as the event (`IDecisionContext.Execute`); no event holds them and a test greps the whole log for every one. They are not projections, so a rebuild leaves them alone. `invites` got an id (the events name an invite by it); `/api/invites` still lists codes.
- **`deliveries` is gone.** A queued message is its own `MessageDelivered` event, queued until a `TurnStarted` with the same turn id follows it in the thread (a requeued `TurnInterrupted` after that puts it back); `Deliveries.Queued` is that query. A runner's **position** (`runner_positions`, a projection) is the event position up to which nothing is queued for its agent, so catching up reads only what came after it. `open_turns` (a projection, one row per thread and agent from `TurnStarted` to the turn's end) replaces the `sent` status. A delivery's id on the wire is the position of its `MessageDelivered` (ids no longer come from a table). An old `deliveries` table is renamed `legacy_deliveries` and dropped by the importer.
- **Delete and redact.** `ChannelDeleted` is a projection event that drops the channel, its posts, threads and open turns (the API no longer deletes rows). New endpoints `DELETE /api/posts/{id}` (`MessageDeleted`) and `POST /api/posts/{id}/redact` (`MessageRedacted`): the author, an admin, or for an agent's post its owner; text posts only. A deleted post stays as a hidden row (`posts.deleted`, so a later event about it can't bring it back); `PostSelect` and the reply count leave it out; it reaches browsers as a new `post-deleted` message, live and in catch-up (the web reducer removes it; the thread's reply count updates). Redaction replaces the words in the projection AND in the events that hold them (`MessagePosted.body`, `MessageDelivered.text`, `AgentReplied.text` → `[redacted]`, `redacted: true`); the events stay as facts. There is no button for either yet: it's the API only (round 3 or the user decides where it belongs).
- **Importer** (`Importer.cs`, runs at startup before anything reads the database). If people exist and the log has no `ServerSetUp`, the tables are written as synthetic events (`data.imported = true`, `schema` 1, `occurred_at` = the row's own time, structural order: server, members by id, channels and their members, invites, posts in the order they were made with each thread's `ThreadStarted` / `AgentJoinedThread` right after its root, then the deliveries with their turn events). In ONE transaction: fingerprint the tables, empty the projections, write the events through the projections, fingerprint again; a difference throws, undoes everything and the server refuses to start with the first differing row. Existing post ids, member ids, channel ids and invite ids are kept (events carry them). Notes: a queued delivery stays queued; a `sent` one gets a `TurnStarted` and then startup recovery interrupts it (the thread says the server restarted); the agent-to-agent count is restored through `agentChain` on `ThreadStarted` / the last person's reply.
- **Upcasting** (`Upcasting.cs`, applied in `SqliteEventStore.ToEvent` on every read): `Current` says each type's schema, `Steps` converts one schema up. First real step: `ChannelDeleted` 1 → 2 adds the channel's name (round 1 wrote only the id; the audit needs a name for a channel that's gone). Test writes a schema-1 row by hand and reads it as schema 2.
- **Rebuild** (`IEventStore.RebuildProjections`, `Projections.Tables`): empties every projection table and replays all events in one transaction, holding the write lock. No endpoint yet (round 3 adds the admin action).
- **phase2 flake fixed**: the script deletes the runner's saved `sessions-*.json` (under its scratch `XDG_CONFIG_HOME`) when it starts. The script's SQL assertions followed the storage: the token check reads `runner_secrets`, "both deliveries are done" reads `TurnEnded` events and `open_turns`, the wrapped text reads the `MessageDelivered` events. Same steps, same count (30).

**Decisions**
- **A DB that already ran round 1 has events but no `ServerSetUp`.** The brief says "event store empty", but the round-1 `main` build (the one asked for the test database) writes thread events, and the user's own database may have run it. Those older events reference channels and members that no event creates, so they can't be replayed. So the importer MOVES them aside (`legacy_events`, kept) and imports from the tables, which are what those events built. Nothing the app shows is lost; the round-1 facts (tool output hashes, causation) stay in `legacy_events`. A pre-round-1 database has no events to move.
- **Not importing is the safe failure.** Any mismatch refuses to start rather than starting on half a log. The fingerprint ignores event positions, `last_seen`, an enrolment code's expiry and a tool row's `meta.title` (the body holds it; an old row may hold null).
- **No `person-{id}` events yet.** The design puts `ChannelRead`, `PasswordChanged` and `ProfileEdited` there; nothing produces them until round 3 (`ChannelRead`) and there is no password change or profile edit. `PersonJoined` / `RoleChanged` / `PersonRemoved` are on `server`, as the design's table has them.
- `RunnerConnected` / `RunnerDisconnected` are events (the design lists them; round 3's agent activity reads them), one per connection, actor = the agent.
- **A `MessageDelivered` carries no id of its own** (round 1 allocated one from a table that's gone); its position is its id.
- `Dispatcher.RemoveAgent` moved to `Api.RemoveAgent` as a static with the person who did it; removing a person removes their agents one by one, each its own `AgentRemoved`.
- **Test database:** made with the round-1 `main` build (`tests/fixtures/make-legacy-db.mjs` is the recipe: a worktree of 291ef6b, a scratch server, a script that is a person and a fake runner); `Fixtures/legacy-main-round1.db` and the answers that server gave (`…expected.json`) are committed. Invented people and a made-up workspace only; the password hashes in it are for the fixture's throwaway passwords.

**For round 3**: audit and agent activity read `RunnerConnected`/`Disconnected` and turn events; `ChannelRead` goes on `person-{id}`; the rebuild needs its admin endpoint; the async projections can reuse `IEventStore.ReadAll` and a checkpoint table. `Effects.From` is where a new event type that changes a screen needs a line.

**Checked**
- `dotnet test`: 13 runner and 48 server tests (11 new in `RoundTwoTests` and `ImporterTests`: rebuild in place and into an empty database, the streams and no secrets in the log, a runner's position and a requeued message, delete and redact and who may, `post-deleted` live and on catch-up, upcasting, two requests racing for a name or the last invite place; the importer on the round-1 fixture with the old server's answers compared, on a pre-events copy, with a turn in flight and a failed one, and an upgrade that can't reproduce the data changing nothing). I changed the expected answers by hand once to see the importer test fail, then restored them. `bun run test`: 27 (one new reducer test).
- Live, on scratch servers (ports 5883, 5884, 5886, 5888 and a LiveKit 1.13.11 container labelled `cwp`, removed afterwards; the `cowork` compose project and its volume were not touched): `phase1.mjs` 24 checks; `phase2.mjs` 30 (real runner, real OpenCode 2.4.1; started with a stale `sessions-*.json` in place, which the script now clears itself); `phase4.mjs` passes (19 checks on this run, as in round 1); `phase5.mjs` 22.
- The fixture was made with a worktree of 291ef6b (the round-1 `main` build): a real server, a script acting as two people and a runner, then the database backed up.

## Brief 4, round 3: the first new views

**Demo:** an admin opens **Audit** and reads "Rosa asked reviewer (Alex's agent) to run `git log -3`. Alex allowed it once. It ran on build-01." (`phase6.mjs`, with a real runner and real OpenCode 2.4.1); a person sees unread badges per channel and a **Mentions** inbox; reviewer's profile shows its activity; every view rebuilds from zero with one admin action (`RoundThreeTests`, and the Rebuild buttons in `phase6.mjs`).

**What changed**
- **Async projections** (`AsyncProjections.cs`): `IAsyncProjection` + `ProjectionRunner` (a hosted service). Each projection has a row in `projection_checkpoints` (the event position it has applied up to, moved in the SAME transaction as its rows, in batches of 500). The runner wakes on every commit (and once a second as a safety net), catches each projection up, and sends the browsers `views-changed`. `RebuildAsync(name)` empties the projection's tables (and their `sqlite_sequence` rows) and replays every event in ONE transaction, so readers see the old view until the new one is complete, then catches up whatever was committed meanwhile. Migration `005_views.sql` holds all the tables.
- **Audit** (`AuditProjection`): one entry per thing that happened, with a readable sentence. A **tool call** and a **turn** are folded from several events into one entry and the sentence is rewritten as later events arrive: `ToolCallStarted` (who: `on_behalf_of`, which agent, the verb from the tool kind), `ToolCallDetailed` (the command), `PermissionRequested` (attached to the tool call the turn started last and nobody asked about yet; in the same `correlation`), `PermissionGranted/Denied` (found by `causation` = the ask's position; who answered = its actor), `ToolCallFinished` (status), and the host from the turn's `TurnStarted`. Waiting, allowed once / every time, "said no", ran / failed / didn't finish are all told. Plain events (channels, people, invites, agents, runners, posts) are one sentence each. Filter by person, agent (both are `audit_members`: the actor, who it was for, the agent, its owner, whoever answered) and channel. Admin only (403 for others, no menu entry). `GET /api/audit`.
- **Unread and mentions** (`UnreadProjection`): activity = `MessagePosted` and the first `AgentReplied` of an answer; markers from `ChannelRead` (on `person-{id}`, the first events on that stream). A count is "by someone else, after max(the person's marker, their own baseline, the view's baseline)", for channels the caller may see. Mentions: `@handle` in a message, and in an agent's reply, for people; an inbox row per post and person. `POST /api/channels/{id}/read` writes ONE `ChannelRead` (position = the channel's latest text post) and only when that moves the marker forward, so opening a channel with nothing new writes nothing. `GET /api/unread`, `GET /api/mentions`.
- **Agent activity** (`ActivityProjection`): turns per agent (outcome running / finished / failed / interrupted, tool calls, host, who it was for), the threads it is busy in, failures, runner connections. `GET /api/agents/{id}/activity`; a thread in a channel the caller can't see is told as "a private thread".
- **Rebuild:** `GET /api/admin/projections` (name, checkpoint, head, last rebuilt) and `POST /api/admin/projections/{name}/rebuild`, admin only. In the Audit dialog: the three views with "at event N of M" and a Rebuild button each.
- **Web** (same Fleet `ui/` pieces, `AppDialog`, `Select`, `Badge`, `EmptyState`, `RelativeTime`): **Mentions** at the top of the channel list with a badge, unread badges and bold names on channels, **Audit** in the server menu for admins, an **Activity** button on each agent in the member list and in People & agents. The store loads `/api/unread` on connect, after a reconnect and (debounced) on `views-changed`; `markRead` is called when the open channel has unread messages.

**Decisions**
- **The audit never holds what people wrote**, only who, where and what kind of thing (a command is shown because it is what the owner was asked to allow). Redaction therefore changes nothing in it; the mention inbox's excerpt becomes `[redacted]`, and a deleted channel's rows go but the audit keeps "Alex deleted #name" (names come from the audit's own `audit_names`, kept from the events in order, so a rebuild gives the same sentences however names changed since).
- **Everything before the unread view counts as read, once.** A server that has people when migration `005` runs appends one `UnreadBaselineSet` event at startup (not marked imported: it is new). Later, a person's baseline is their `PersonJoined`, and a private channel's their `MemberAdded`, so nobody starts with a mountain of old messages. A fresh server writes no baseline (its first restart must not eat unread). `Db.Migrate` now returns what it applied for this. `ImporterTests` excluded that one event from "every event is imported" and asserts it is there once.
- **Read your own writes:** `/api/unread`, `/api/mentions` and the read endpoint first bring the projections to the head (`CatchUpAsync`), so a person who has just joined or read a channel sees it. Everything else may lag by milliseconds; that is the cost of async.
- **A poison event would stall one projection** (the batch rolls back, the runner logs and tries again): the others carry on. Rebuild doesn't help if the projection code throws on an event; a fix to the code does.
- **`Effects.From` needed no new line**: these views are fetched, not pushed through live and catch-up; the only push is `views-changed` ("something moved, ask again"), and a reconnecting browser asks anyway.
- **Mentions inbox is a dialog**, like People and Audit, not a route: it fits the existing patterns and keeps the channel open behind it.
- **The runner's host name** comes from `--host` (it already existed); the live check starts it with `--host build-01`, so no screenshot or doc shows this machine's name.
- Unread counts thread replies and agent answers too (everything written in the channel); viewing the channel marks all of it read.

**Checked**
- `dotnet test`: 13 runner and 56 server tests (8 new in `RoundThreeTests`: the demo sentence and its chain, a denied ask and a failed turn, unread / read / inbox / later joiner / private channel / delete, agent activity and privacy, **every projection destroyed and rebuilt to row-for-row identical tables, by catching up from a zeroed checkpoint and by the admin endpoint, checkpoints at the head, stale rows dropped, 403 / 404**, the browser told `views-changed`, redaction and channel deletion, the baseline on the round-1 fixture). `bun run test`: 27.
- Live on scratch servers (ports 5883, 5884, 5886–5889; LiveKit 1.13.11 container `cwp-lk-r3` labelled `cwp`, removed; the `cowork` project and its volume untouched): `phase1` 24, `phase2` 30 (real OpenCode 2.4.1), `phase4` 19, `phase5` 22, **`phase6` 27** (two browsers, real runner `--host build-01`, real OpenCode 2.4.1: unread badges and Mentions for Rosa, ChannelRead events on her stream, the Audit sentence, its three filters, no Audit for Rosa, each view rebuilt by its button and the audit reading the same, reviewer's activity, unread unchanged by its rebuild). Screenshots in `docs/screens/round3/`, looked at.
- The old scripts rewrite their own screenshots when run; those were restored, only `round3/` is new.
