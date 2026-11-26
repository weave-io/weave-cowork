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

**Round 3 follow-up:** one sign-up is one audit sentence ("Rosa joined with an invite link from Alex."). `InviteUsed` no longer makes an entry; the audit keeps who created each invite (`audit_invites`, migration `006`, part of the audit's rebuild) and `PersonJoined` says whose link it was. Covered in `RoundThreeTests`; `phase6` 27 and the unit tests pass; round3 screenshots regenerated.

## Licences (25 Nov 2026)

AGPL-3.0-only for everything except `src/CoWork.Runner` and `src/CoWork.Contracts` (Apache-2.0); `NOTICE`, `CONTRIBUTING.md` (no CLA text yet), README section. `THIRD-PARTY-NOTICES.md` is generated by `python3 scripts/third-party-notices.py` (web prod closure read from `web/node_modules`, the two fonts, NuGet packages of Server and Runner; exits 1 when a licence isn't known permissive).

**Decisions**
- Only the packages installed on this machine are listed, so platform-specific optional packages (e.g. `lightningcss-linux-x64-*`) appear for linux-x64 only. Re-run on the build platform (the Docker image) before a release.
- lightningcss (MPL-2.0) was listed only because tailwindcss and @tailwindcss/vite were in `dependencies`; they are build-only, so they moved to `devDependencies` and the notices no longer list it. No non-permissive licence remains.
- NuGet packages without licence text in the package get their SPDX expression only. The .NET runtime isn't listed (Microsoft, MIT).

## Brief 6: search

**Demo:** Rosa opens **Search** (the entry under Mentions, or `Ctrl+K` / `⌘K`), types `refund rounding` and the results arrive as she types: **Threads** first (the opening post, "3 replies", `@reviewer`, and the best match inside it), then **Messages** (posts, thread replies and reviewer's answers), with the matching words marked and `rounds` found by `rounding`. The chips insert `in:#checkout-app`, `from:@reviewer`, `from:@rosa` and `is:thread` (and take them out again). ↓ and Enter open the channel at the post, lit for a moment; a thread result opens the thread with the matching reply in view. Nothing from #leads (private, she isn't in it), nothing deleted, and the redacted message is found by nothing it used to say. reviewer's real answer (runner + OpenCode 2.4.1) is found by a word only it used. `tests/live/phase8.mjs`, 31 checks.

**What changed**
- **`SearchProjection`** (async, like `UnreadProjection`; in the admin Rebuild list as "Search"): migration `010_search.sql` adds `search_docs` (one row per document: `kind`, `ref_id`, `channel_id`, `thread_id`, `author_id`, `position`, `occurred_at`, `gone`) and `search_fts`, an FTS5 table (`title`, `body`, `tokenize = 'porter unicode61'`) whose rowid is the document's id. `MessagePosted` adds a document; `AgentReplied` adds one per answer (segment 0) and appends later segments to it; `ThreadStarted` marks the opening post as its thread's; `MessageDeleted` / `MessageRedacted` take the words out of the index and mark the document `gone`; `ChannelDeleted` drops the channel's documents. A database from before this brief is indexed by the projection catching up from position 0 at startup.
- **`GET /api/search?q=…&limit=&offset=`** (`Search.cs`): `SearchQuery.Parse` takes `in:#channel`, `from:@handle` and `is:thread` out of `q` (any case; two of one filter mean either; unknown `x:y` words are searched for as words; `"a phrase"` stays together). Each word is quoted (nothing typed is FTS syntax) and the last one is a prefix, so results come while a word is half typed. Ranked by `bm25` (title weighted 4×), highlights from `snippet()` with `\u0002`/`\u0003` markers that the browser turns into `<mark>` (never HTML). Visibility is in the SQL (public channels, or private ones the caller is in), so totals and pages are of what the caller may see. Threads are grouped from their matching messages (opening post or replies), best match first, with reply count, agents and the best match; an opening post shows as its thread, not again under Messages. Filters alone (`from:@reviewer`) list the newest first. It brings the projections to the head first, so what you just wrote is found.
- **Web:** the Search view in the main area (`/search?q=`, `SearchView.vue`), as in step 6 of the mockup: box with the count, chips, Threads and Messages groups, empty and no-results states, ↑ ↓ Enter, "Show more". `Ctrl+K` / `⌘K` from anywhere (again from Search: focus the box). Results open `/c/:id?post=` or `/c/:id/t/:root?post=`; the shell waits for that channel or thread, scrolls the post to the middle and lights it. The channel list gets one entry (Search, under Mentions, with the shortcut). Helpers in `search.ts` (highlight segments, chip toggling, result paths) with vitest tests.

**Decisions**
- **A redacted message leaves the index.** Redaction rewrites the events to `[redacted]`, so a rebuild would index that word; the projection deletes the document's words on `MessageRedacted` instead, and a document marked `gone` ignores later segments (an answer redacted while it was still being written stays out).
- **One document per answer means per answer post.** The dispatcher already starts a new post at each tool-call boundary, so an answer with tool calls in the middle is two or three posts, and so two or three documents; each one is complete (all its segments).
- **`kind` and `title` are there for outcomes and the library.** Messages have an empty title; an outcome or a library document will be a row with a title and the same visibility rule (its channel, or a project once projects land). The UI's groups are Threads and Messages for now.
- **The chips** are the channels the current results are in (most first) plus the one you came from, then each agent, you, and `is:thread`. A chip that is in the box is shown on and takes itself out when clicked. The mockup's chips were examples; a fixed list would offer filters that find nothing.
- **Threads are capped at the best 20** with "the best 20 of N" under them; Messages page with "Show more" (`offset` applies to messages).
- **No live refresh of open results**: they answer what was typed. An agent writing in the background would otherwise reshuffle the list under the keyboard.
- **Search is for people** (403 for anything else). Agents get the same query through `cowork_search` when CoWork's MCP tools exist; `Search.Run(c, memberId, …)` is ready for a boundary check in place of the channel rule.
- **Ports and memory:** scratch servers on 5886–5888, every heavy command under `systemd-run … MemoryMax`, LiveKit for phase4 as container `cwp6-lk` (label `cwp6=1`) on host ports 7980–7982 (the user's `cowork` project uses 7880), removed by label afterwards.

**Checked**
- `dotnet test`: 13 runner and 61 server tests (5 new in `SearchTests`: the demo's grouping, stems, prefix, markers and one document per three-segment answer; a private channel, added and removed, counts and paging over what Rosa may see, signed out; redaction of a person's post and an agent's answer, deletion, channel deletion; every filter, case, unknown channel, two of a filter, filters alone, FTS syntax typed into the box; read-your-own-writes and a rebuild to the same rows and the same answers, FTS5 `integrity-check`). The round-3 rebuild test now covers four projections. I broke the projection's redaction case once to see a test fail, then restored it. `bun run test`: 30 (3 new).
- Live: `phase8` 31 checks (two browsers, real runner `--host build-01`, real OpenCode 2.4.1, fresh scratch home), `phase1` 24, `phase4` 19 (LiveKit 1.13.11), `phase5` 22, `phase6` 27 (it now lists four views and rebuilds Search with its button too). Screenshots in `docs/screens/brief6/`, dark and light, looked at, from the passing `phase8` run; its phone shot showed a thread line running out of its card, fixed (and phase8's phone check now looks inside the cards). `8-phone-dark.png` is after the fix, from a scratch server seeded with people's posts only (see below for why not from phase8).
- **`phase2` could not pass here at the end:** steps 1–14 passed (real answers, tool rows, Rosa's wrapped message), then OpenCode's free model answered "Free usage limit reached. Connect a provider … or switch models" as a notice, so the harness never asked for the shell command. A second try and a final `phase8` re-run (after the CSS-only phone fix) stopped at the first answer for the same reason. Nothing in brief 6 touches the runner, the dispatcher or the thread stream; `phase5` (real runner, scripted agent) and `phase6`/`phase8` (real OpenCode, run before the quota ran out) pass. Re-run `phase2` and `phase8` when the quota is back.

**Next**
- `project:` filter: brief 5 (projects) had not landed on `main` when this finished. With it: `project:checkout` in `SearchQuery`, a chip, and the visibility rule by project.
- Opening a result older than the channel's last 200 posts: the feed loads only those, so the post isn't there to scroll to (the channel opens at the bottom). Needs paging around a post (`/channels/{id}/posts?around=`).
- Outcomes and library documents (`kind` `outcome` / `library`) and their groups first, as in the mockup; `is:waiting`, `has:branch`; "Ask an agent instead" with `cowork_search` limited to the agent's boundary.
- Search people and agents by name (the design's "people search" lists them).

## Brief 5: projects, workspaces and an agent's boundary

**Demo:** `tests/live/phase7.mjs` (31 checks; four browsers, a real runner `--host build-01`, real OpenCode 2.4.1, a bare `checkout-app` repository the script makes). Alex makes **Checkout app** from the repository with Rosa and Sam, `#checkout-app` and 🔊 `checkout-standup`, and lands in its settings. The channel list shows the project as a group with a gear; `#general` stays under Channels; Rosa sees the project live, Jo (not a member) sees nothing of it and the API says 404 to her. Alex adds reviewer: the dialog shows its boundary in eight sentences from the server and "Runs unsandboxed on build-01". Rosa and Sam mention reviewer at the same moment: both turns start before either ends, each thread works in its own git worktree on `cowork/<slug>` (one clone for reviewer and the repository), `rosa-notes.md` is only in Rosa's worktree and `sam-notes.md` only in Sam's, the pre-push hook refuses a push to `main`, OpenCode's data for the project is in the project's own folder with the sign-in linked in, and the session line says the branch and "unsandboxed on build-01". The settings show the workspace "cloned on build-01", Alex lead / Rosa and Sam members, reviewer's row and the rules. Set to take work from Alex only, Rosa's mention gets "reviewer only takes work from Alex." and no turn; the audit says "Rosa asked reviewer (Alex's agent) for work in #checkout-app. It didn't take it: it takes work from Alex only."

**What changed**
- **Events.** `project-{id}`: `ProjectCreated`, `ProjectUpdated`, `ProjectMemberAdded` (role `lead` or `member`; adding again changes the role), `ProjectMemberRemoved`, `WorkspaceAdded` / `WorkspaceRemoved`, `AgentAddedToProject` / `AgentGrantsChanged` / `AgentRemovedFromProject` (`takesWorkFrom` = `owner` | `members`). `ChannelCreated.projectId` and `ChannelMovedToProject` on the channel stream. In threads: `ThreadStarted.slug`, `WorkRefused` (the agent, who asked, the grant that refused, the reply) and `ThreadWorkspaceReady` (what the runner prepared). `ProjectProjection` is inline; `DirectoryProjection` and `ThreadProjection` got the channel and thread parts. Migrations `007_projects.sql` (projects, project_members, project_agents, workspaces, `channels.project_id`, the `channel_audience` view) and `008_thread_workspaces.sql`. No `009`.
- **Who sees what** is one SQL rule, `Access.SeesChannel`, used by the API, the hub, catch-up, unread, the answer check and (after the merge) search: added to a channel → sees it; public outside a project → every person; public inside a project → its members and admins; private → only those added. A project channel's members (`channel_audience`) are the project's people and agents, so an agent added to the project takes part in its public channels without being added to each. Leads and admins edit a project; the project's lead manages its channels too.
- **Routing.** A mention from someone the agent's grant doesn't allow writes `WorkRefused` (a notice in the thread, by the agent) instead of `MessageDelivered`; agent-to-agent mentions are checked against the person they're for. `DeliverMessage` carries `ProjectId` and a `ThreadWorkspace` (the project's first workspace this agent's runner can reach, and the branch `cowork/<slug>`); outside projects it's the agent's own folder, as before.
- **Runner** (`Workspaces.cs`, `Acp/HarnessHome.cs`). Under `<runner data>/<server>-<agent>/`: `repos/<name>-<hash>` (one `git clone --no-checkout` per agent and repository, `git fetch --prune` before each new thread), `worktrees/<name>-<hash>/<slug>` (`git worktree add -b cowork/<slug> … origin/<branch>`, or the branch again if it exists), `projects/<id>/threads/<slug>` (no workspace), `projects/<id>/harness/{data,state}`. A git folder gets worktrees too (from its HEAD; the person's own checkout is untouched); a plain folder is used as it is. The same thread finds the same place after a restart, so `session/load` gets its cwd (tested). Each worktree has its own `core.hooksPath` (per-worktree config, `extensions.worktreeConfig`) pointing at a `pre-push` hook that refuses the project's branch. One harness process per agent and project. The runner logs where every worktree is; they're kept.
- **Web.** Project groups in the channel list (gear, +, New project), `NewProject.vue`, the settings page `ProjectSettings.vue` (route `/p/:id`), `AddAgentToProject.vue` with `AgentBoundary.vue` (reusable: lines with icons, code in backticks, the unsandboxed warning), the branch and "unsandboxed on <host>" in the thread's session line, a project's channel in Create channel.
- **Boundary** (`GET /api/projects/{id}/agents/{agentId}/boundary[?takesWorkFrom=]`): sees (the project's channels, and anything outside it the agent was added to), takes work from (owner, or the members by name), works on (the workspace it would use, what it can't reach), reaches, asks, spends, remembers, stops, each built from the tables the server enforces.

**Decisions**
- **Remembers: what OpenCode 2.4.1 keeps where** (checked with a scratch home): `XDG_DATA_HOME/opencode` holds `opencode.db` (sessions and messages, projects, remembered permissions) **and** `auth.json` (the sign-in); `XDG_STATE_HOME/opencode` recent models and history; `XDG_CONFIG_HOME/opencode` settings and global instructions; `XDG_CACHE_HOME/opencode` downloads. So a harness process per (agent, project) gets its own `XDG_DATA_HOME` and `XDG_STATE_HOME`; config and cache stay shared (the owner's settings apply everywhere, nothing is downloaded twice); `opencode/auth.json` (and `mcp-auth.json`) is **symlinked** from the runner's own data folder into each project's, so signing in once, or a refreshed token, works everywhere. `HOME` isn't changed: other harnesses keep their sign-in under it. Live: the project's folder had its own `opencode.db`, the link, and the runner's folder got nothing of the project's.
- **Which workspace a thread uses:** the project's first workspace (by when it was added) that the agent can reach. A project with several workspaces doesn't yet give a thread more than one; "can't reach" is told in the boundary and on the settings page.
- **Slug:** the first five words of the root post without mentions, then the thread id (`fix-rounding-in-refunds-12`), decided once in `ThreadStarted`, so redacting the post doesn't move the branch.
- **Admins see every project** (they look after the server and already read the audit); private channels in a project still need an explicit place.
- **Adding an agent** takes a lead (or admin) who owns it (or an admin); changing its grant: its owner, a lead or an admin. Its owner is always heard.
- **A thread reply (no mention) from someone the grant doesn't allow isn't passed on**, silently; only a mention gets the short reply, so a busy thread doesn't fill with refusals.
- **The runner reports the place** (a `workspace` stream event) rather than the server guessing, because only the runner knows whether a folder is a git folder and where it cloned.
- **Live checks and the model:** OpenCode's free models (`big-pickle`, `glm-5-free`, then `deepseek-v4-flash-free`) answered "Free usage limit reached" today. `lib.mjs` takes `OPENCODE_MODEL` and `OPENCODE_SIGN_IN=link` (links, never copies, `~/.local/share/opencode/auth.json` into the scratch home as the runner's own sign-in, which also exercises the per-project link); every live run here used `OPENCODE_SIGN_IN=link OPENCODE_MODEL=github-copilot/claude-haiku-4.5`. phase7 allows edits and denies shell in its scratch config, so nothing needs approving.
- **Merging with search:** `Search.cs` had its own "public or added" filter, which after the merge found project messages for people outside the project. It now uses `Access.SeesChannel` (test: Jo finds nothing until she joins). A `project:` filter is still for search's next round.
- The importer and rebuild tests: the busy server now makes a project with a workspace, a grant change, a moved channel and a refusal, and the rebuild (in place and from events only) gives identical tables; the importer's answer comparison ignores what brief 5 added (`projects`, `projectId`, a null thread workspace). A database from before events has no projects, so the importer writes none.

**Checked** (rebased on `main` with search, ports 5883–5885, LiveKit 1.13.11 container `cwp5-lk` labelled `cwp5`, removed; the `cowork` project, its volume and `~/.weave` untouched)
- `dotnet test`: 69 server (7 new in `ProjectTests`, plus projects in the rebuild and streams tests), 18 runner (5 new in `WorkspaceTests`, real git and the fake ACP agent: two threads side by side, the pre-push hook, a restart finding the same worktree, a fresh fetch, git and plain folders, no workspace, per-project processes and data, the sign-in link). `bun run test`: 33.
- Live: `phase1` 24, `phase2` 30, `phase4` 17 (no `BASE_NOVOICE` server: three ports only), `phase5` 22, `phase6` 27, `phase8` (search) 31, **`phase7` 31**. Screenshots in `docs/screens/brief5/` (dark and light), looked at; the old scripts' own screenshots were restored.

**Not done / next**
- Worktrees are never removed (the runner logs where they are); clones aren't pruned.
- The agent profile doesn't show its boundaries yet (`AgentBoundary` is ready for it); projects can't be deleted; a channel can be moved by the API only (no button); people's roles change by API (no "make lead" button).
- `--sandbox`, project rules as settings, sources, budget, board, library and guests: later briefs. Claude Code and other harnesses keep one data folder (their sign-in is under `HOME`).

## Brief 7, round 1: a sandbox per thread

**Demo:** `tests/live/phase9.mjs` (37 checks; browsers for Alex, Rosa and Sam, a real pool on this machine, real containers from
`cowork-sandbox-opencode:2.4.1`, real OpenCode 2.4.1 talking to the server's gateway, which forwards to this machine's GitHub Copilot
sign-in with `claude-haiku-4.5`). Alex opens **Where agents run → Add a pool**, runs the command it shows (`cowork-runner pool connect … --slots
2 --memory 1500M`), and **pool-01** shows online with 2 slots. He sets **Server settings → Models** to GitHub Copilot and the idle time to 25
s, makes the **researcher** team template (Runs on: a pool) and enables it in Checkout app (a repository). Rosa and Sam ask researcher at the
same moment: two containers run side by side; Alex's third thread says "Waiting for room on pool-01 (2 of 2 busy), 1st in line". Rosa's
thread shows the starting rows (pool-01 has room, started a sandbox from the image with 1500 MB, made a copy of `app` on
`cowork/what-is-the-function-in-1`, connected; starting `opencode acp`), then the answer streams with a tool row `read src/tax.ts` and names
`roundTax`. After 25 idle seconds a container goes (`SandboxSuspended`), its volume stays, and Alex's waiting thread starts in the freed slot.
Rosa replies; her sandbox resumes on the same volume, the runner logs `loaded session ses_…`, and researcher answers "`roundTax`" without
reading the file again. Inside the container: the environment names only `COWORK_*` settings and the thread token, no `auth.json` or pool or
runner config anywhere, neither the GitHub token nor the pool token in its volume or its changed files, and OpenCode's config takes the token
by name (`{env:COWORK_THREAD_TOKEN}`). The gateway recorded each thread's calls and tokens, per turn, on behalf of whoever asked.

**What changed**
- **Contract** (`CoWork.Contracts`, Substrate-shaped): `CreateSandbox` / `ResumeSandbox` carry a `SandboxSpec` (the template's image, harness
  and model; the thread's workspace; limits; network rules as an `EgressPolicy` of hosts and `replaceHeaders` with header, prefix and a
  credential reference; a fresh thread token); `SuspendSandbox`, `DeleteSandbox`; `SandboxStateReport` (`suspended`, `resuming`, `running`,
  `suspending`, `failed`, `deleted`, with how long the volume and the container took); `PoolEnrolRequest/Response`, `PoolHello`.
- **Templates:** an agent with `placement = pool`, an image and a model (`AgentCreated` carries them; migration `011_pools.sql`). Admins
  make them (they spend the server's models). No runner, no code; routing treats "has a runner token or is a template" as able to work.
  The agent dialog has **Runs on: a runner I connect / a pool**; edit shows image and model for a template.
- **Pools** (`pool-{id}`: `PoolCreated`, `PoolCodeIssued`, `PoolEnrolled`, `PoolConnected`, `PoolDisconnected`, `PoolRemoved`; `pools`
  projection; `pool_secrets` holds hashes). The pool token opens `/hubs/pool` only: there it receives sandbox commands and reports its own
  sandboxes' state; a report about a sandbox the server didn't place on it is ignored. **Where agents run** lists pools (slots, memory,
  running sandboxes) and runners; **Add a pool** gives the code and command.
- **Thread tokens** (`thread_tokens`, hashes only, never in an event): minted per create and per resume, bound to sandbox, agent, project
  and thread; revoked on suspend, failure, delete, the template's removal or leaving the project. The runner hub accepts one and binds the
  connection to its thread: it is sent only that thread's deliveries, and `Stream` / `RequestPermission` for any other thread throw. A
  revoked token's live connection is told and aborted.
- **Scheduler** (`Sandboxes.cs`): a delivery for a template with no connected sandbox in its thread `Ensure`s one: `SandboxRequested`
  (it waits in a global FIFO queue), then `PlaceWaiting` gives the oldest a slot on the online pool with the most room (`SandboxPlaced`),
  mints its token and sends Create or Resume. The pool's report becomes `SandboxStarted` / `SandboxResumed`; the runner inside connecting
  is `SandboxConnected`, and the queued message goes. `SandboxReaper` (every second) suspends a running sandbox with no open turn and nothing
  queued once it has been idle for the server's idle time (`SandboxIdleSet`, default 15 minutes): `SandboxSuspending` (token revoked at
  once) and, when the pool says the container is gone, `SandboxSuspended`; a freed slot goes to the next in line, and a message that arrived
  meanwhile resumes it. A failure is `SandboxFailed` plus `TurnStarted`/`TurnFailed` for each waiting message ("researcher's sandbox
  couldn't start: …"). Every sandbox event names the person it's for (`on_behalf_of`).
- **The starting card**: a post of kind `sandbox` (from `SandboxRequested`) whose rows the projection rewrites as the events arrive; a resume
  gets a new card. The thread's session line says "team template · sandbox on pool-01 · `cowork/<slug>` · started by Rosa" and the state
  (waiting for room, starting…, stopped, volume kept). The thread panel now decides whether to follow the end before new rows grow it.
- **The pool** (`cowork-runner pool`, `Pool.cs`): one `git clone --mirror` per repository, fetched before each new thread; a volume per
  thread prepared by a short-lived container of the image (`/work/repo`: a copy of the mirror on `cowork/<slug>` with the real URL as
  origin, `/work/home`: the harness's home); then `podman run` with `--memory`/`--memory-swap`, `--pids-limit 512`, the labels, and only
  `COWORK_SERVER`, `COWORK_AGENT`, `COWORK_HANDLE`, `COWORK_THREAD`, `COWORK_MODEL`, `COWORK_WORKSPACE` and `COWORK_THREAD_TOKEN` (passed
  by name, so the value isn't on podman's command line). On connect it says which sandboxes it runs (the server reconciles), and it notices
  a container that stopped by itself. It only removes containers and volumes carrying its `cowork.pool` label.
- **The runner inside** (`cowork-runner sandbox`, the image's entry point): writes OpenCode's config for the gateway, connects with the
  thread token, serves the one workspace the pool made, keeps its session map in the volume (so a resume calls `session/load`), and exits
  when the token is revoked.
- **The image**: `deploy/sandbox/Containerfile` and `build.sh` (551 MB): `debian:trixie-slim`, git, ripgrep, curl, the runner published
  self-contained for linux-x64, OpenCode 2.4.1, user `cowork` (uid 1000), `HOME` and the XDG folders in `/work/home`.
- **The gateway** (`Gateway.cs`): `POST /gateway/v1/chat/completions` and `GET /gateway/v1/models`, thread token as the key. It forwards
  with the server's credential (OpenAI-compatible: base URL and key in `server_secrets`; Copilot: see below), asks for usage in the stream,
  passes the server-sent events through line by line, and records `ModelUsageRecorded` (prompt, completion and total tokens; cost when the
  provider says) in the turn in flight, for whoever asked; `model_usage` sums them per turn. **Server settings** has Models and Sandboxes.
- Project settings show a template as "pool-01, a sandbox per thread", asks "Whoever started the thread, then Alex", sandboxed, and a rule
  for templates. A template's boundary has its own sentences (works on the pool's mirror, reaches, asks, spends through the gateway,
  remembers its volume, stops when idle) and a note instead of the unsandboxed warning.

**Decisions**
- **Reaching the server from a container:** rootless podman here uses pasta, and `host.containers.internal` (169.254.1.2) reaches the host's
  network address, not its loopback, so a server on 127.0.0.1 refused the connection. With `--network pasta:--map-host-loopback=169.254.1.2`
  it answers. The pool rewrites a loopback server address to `host.containers.internal` and adds that option; any other address is used as
  it is. Checked 25 Nov 2026 with a server bound to 127.0.0.1.
- **OpenCode 2.4.1 in the image:** it isn't on npm (that stops at 1.x), so `build.sh` copies the binary already on this machine
  (`~/.weave/harnesses/opencode2/.opencode/bin/opencode`, only read; nothing else of `~/.weave`) into a build folder under `~/.cache`, and
  refuses it unless it says `opencode v2.4.1` and matches the pinned SHA-256. `OPENCODE_BIN` / `OPENCODE_SHA256` point it at another copy.
  Old builds of the image are removed by their label, never other images.
- **Copilot as OpenCode 2.4.1 does it:** 2.4.1 doesn't exchange the sign-in for a Copilot token any more. Its `auth.json` keeps the GitHub
  OAuth token (as `refresh` and `access`, `expires: 0`); it asks `api.github.com/copilot_internal/user` for the account's API address
  (`endpoints.api`; this account's is an enterprise one) and calls `<api>/chat/completions` with the GitHub token as the bearer and
  `X-GitHub-Api-Version: 2026-08-01`, `Openai-Intent: conversation-edits`, `x-initiator`. The gateway does exactly that, reading the file
  each call (read only, never copied; `COWORK_COPILOT_AUTH` names another file). Copilot returns token counts but no cost, so `cost` is
  null for it.
- **OpenCode's provider in the sandbox:** `provider.cowork` with `npm: @ai-sdk/openai-compatible` (2.4.1 bundles it), `baseURL` the gateway,
  `apiKey: {env:COWORK_THREAD_TOKEN}`, `enabled_providers: [cowork]`, `model` and `small_model` the template's; `OPENCODE_DISABLE_MODELS_FETCH`
  and `OPENCODE_DISABLE_AUTOUPDATE` in the image. It worked on the first try.
- **A copy, not a worktree,** in the volume: a worktree would point at the mirror outside the volume. The copy is made inside a short-lived
  container with no network, trusting `/mirror` through a throwaway global git config (git refuses a repository owned by another uid).
  There is no push credential in the sandbox, so no pre-push hook either; pushing is round 2's proxy.
- **Names and labels** are `cowork-<server id>-p<pool>-sb-<id>`: the server makes an id once (`ServerIdAssigned`, at the first pool
  enrolment) and returns it on enrolment. Keying on the server's address let a fresh scratch server reuse an old run's volume (and its saved
  session) on the same port.
- **`COWORK_RUNNER_CONFIG`**: a pool can't be given a scratch `XDG_CONFIG_HOME` / `XDG_DATA_HOME`, because rootless podman keeps its images
  and settings there (the first smoke run found no image). The runner's config folder can now be named on its own.
- **The thread token is a random 256-bit token stored as a hash**, like the runner's, not yet the signed JWT the design suggests for a proxy
  that checks without asking the server; that comes with the proxy.
- **Asks of a template** go to whoever started the thread (`ownerId` on the card is that person; `agentOwnerId` is the template's owner); the
  project's leads may answer once 30 minutes have passed (`leadIds`, `leadAfter`, checked when they answer). Nobody is notified at 30
  minutes yet: the lead sees the buttons when they look.
- **Nothing retries a failed start by itself:** the waiting messages end with the reason, and the next message from a person tries again.
  Otherwise any pump of the template would loop on a broken image.
- **One queue for all pools,** oldest first; the waiting cards' "Nth in line" moves up as others are placed. A pool's slot counts a sandbox
  that is starting, resuming, running or suspending.
- **Usage is one event per model call,** in the turn's correlation (or the thread's last turn for a call between turns, such as a title);
  "per turn" is the `model_usage` projection. Budgets read it in round 2.
- Network rules travel in the command as data (the server and the gateway); nothing enforces them in round 1, and the boundary says the
  rest of the network is open until the egress proxy.
- A template's agents table entry has an empty `workspace` (the column is NOT NULL); the importer's answer comparison ignores the fields
  brief 7 added.

**Checked** (ports 5883–5888; containers, volumes and LiveKit only with my labels, removed by label; the `cowork` compose project, its
volume and `~/.weave` untouched; OpenRouter never used)
- `dotnet test`: 77 server (8 new in `SandboxTests`: the demo with a fake pool and sandbox; every "can't" of the thread token (another
  thread's deliveries and events, the API, the pool hub, after suspend at the hub and the gateway, after the template's removal) and of the
  pool token (runner hub, gateway, API, another pool's sandbox); a full pool's queue in order; idle suspend (5 s, waiting on the command) and
  resume with a new token on the same sandbox; a failed start; the gateway against a fake provider on a real port (the server's key
  upstream, the stream back, usage in the turn, for Rosa, no key in the log); asks to the thread's starter, then a lead; Where agents run
  and a template's boundary), and the rebuild and no-secrets tests now run a pool and template through the busy server (in place and
  from events only: identical tables). 27 runner (9 new: the loopback mapping, memory sizes, OpenCode's gateway config, the fixed
  sandbox workspace with the fake ACP agent). `bun run test`: 33.
- Live: **`phase9` 37 checks, twice** (screenshots in `docs/screens/brief7/`, dark and light, looked at; the second run after fixing
  code chips that broke mid-name and the thread panel not following a new card). The earlier checks against this build, each on a fresh
  scratch server: `phase1` 24, `phase2` 30 (real OpenCode 2.4.1 through Copilot), `phase4` 19 (LiveKit 1.13.11 as container `cwb7-lk`,
  label `cwb7`, removed), `phase5` 22, `phase6` 27, `phase7` 31, `phase8` 31. phase7 first failed on my rewording of the project rule
  "…go to its owner only…"; a project without templates now reads exactly as before, and one with a template gets a rule of its own. The old
  scripts' own screenshots were restored.

**Not done / next (round 2 and 3, as briefed)**
- The egress proxy and the project's network list (sandboxes still have an open network), the setup command, budgets, Pause all agents.
- A container that dies mid-turn fails the turn ("…'s sandbox stopped during this turn"); the design's "start it again once with
  `session/load`" isn't built. A pool that goes away leaves its threads waiting for it; nothing moves them to another pool.
- `DeleteSandbox` is in the contract and the pool, but nothing sends it yet ("7 days after done" needs a thread's outcome, round 3).
- Nobody is told when a pool is missing, or when an ask passes to a lead after 30 minutes; they see it when they look.
- The gateway is OpenAI-compatible only (no Anthropic-shaped endpoint); the thread token isn't a signed JWT yet; a pool is picked by free
  slots alone (no matching of templates to pools).
- CoWork's MCP tools in `session/new` are round 3.

## Brief 7, round 2: locked down

**Demo:** `tests/live/phase10.mjs` (27 checks; Alex, Rosa and Sam in browsers, a real pool, real containers with no network of their own,
real OpenCode 2.4.1 through the gateway to this machine's Copilot sign-in). Alex sets Checkout app's **network list** (github.com,
registry.npmjs.org), its **setup command** (`sh scripts/agent-setup.sh`) and its **budget** (2,000,000 tokens a month, 200,000 a thread) in
project settings. Rosa asks researcher to fetch example.com: the setup runs first as a tool row ("registry.npmjs.org answered 200 … installed 3
packages": a listed host, through the proxy), the proxy refuses example.com, and Rosa gets the ask "researcher wants to reach example.com. It
isn't on Checkout app's network list. Asked by researcher, for Rosa's thread." Alex, though he owns researcher and leads the project, sees
"researcher is waiting for Rosa". Rosa allows it once; the session gets `<cowork-system thread="1">Rosa allowed example.com once. Try
again.</cowork-system>` and researcher fetches the page ("Example Domain"). The setup ran once in a thread of two turns. With the thread cap
lowered to 10,000 tokens, Sam's thread stops cleanly with "Reached this thread's cap of 10,000 tokens. A lead can raise it." (one notice, no
turn left open); Sam has no Raise button, Alex raises it ("raised this thread's cap to 20,000 tokens."), and the Budget card shows the
month's real use in tokens. Rosa's container is killed (`podman kill`) while researcher reads: "researcher's sandbox stopped unexpectedly.
Trying again.", it resumes on its volume, the runner loads the session, the turn finishes, and the tool row that was running is marked
failed. Alex presses **Pause all agents**: the sandbox stops, the project shows "Agents paused by Alex", Rosa's next message waits (her
thread says "paused by Alex", its card "Stopped: Alex paused the project's agents."); Resume agents, and it's answered. Inside the container:
with the proxy bypassed nothing resolves, through it registry.npmjs.org answers 200 and example.net gets 403; the container's network is
`none`, with its volume at `/work` and the pool's sockets at `/run/cowork`; the setup's result and marker stay in the volume.

**What changed**
- **No network, two sockets** (`Egress.cs`): a sandbox runs with `--network none`. The pool makes `sockets/<sandbox>/` (mounted read-only at
  `/run/cowork`) with `server.sock`, a plain pipe to the server (its hub and gateway, always allowed), and `proxy.sock`, an HTTP proxy
  (`CONNECT` for https, absolute URLs for http) that admits the sandbox's hosts (exact, or `*.example.com`), refuses anything else with a
  403 that says why, and reports the refusal (`EgressRefusal`, at most once a minute per host). The runner inside offers them on loopback
  (127.0.0.1:3129 is `COWORK_SERVER`, 127.0.0.1:3128 is `HTTP_PROXY`/`HTTPS_PROXY`, `NO_PROXY` loopback). OpenCode's webfetch, curl and git
  use the proxy; the runner and the gateway calls go straight to the server socket.
- **The project's network list** (`ProjectNetworkChanged`, `project_network`) travels in each create or resume as Substrate-shaped rules (the
  server and gateway, then the listed hosts and what was allowed for the thread). Project settings → **Sandboxes** edits it.
- **Asks to reach a host:** a refusal is `EgressRefused` (an ask card: Allow once, Allow for this thread, Deny) for whoever started the thread,
  a lead after 30 minutes, deduplicated while one is pending. `EgressAllowed` tells the pool (`AllowEgress`) and queues a `MessageDelivered`
  marked `system` with `<cowork-system …>… Try again.</cowork-system>`, delivered when the agent's turn ends; "for this thread" is kept
  (`thread_egress`) and is in every later start's rules. `EgressDenied` records the answer only.
- **Setup command** (`SetupCommandChanged`, `projects.setup_command`): `DeliverMessage.SetupCommand` carries it to a sandbox; the runner runs
  `sh -c` once per thread in the workspace before the first prompt, through the proxy, as a tool row (`cowork-setup`) whose output is always
  kept (whatever the record-output switch) and shown folded under the row; `~/.cowork/setup.json` in the volume marks it done.
- **Budgets** (`Budgets.cs`; `BudgetSet`, `ThreadBudgetRaised`, `BudgetReached`; tables `budgets`, `thread_budgets`, `project_usage` by
  project, UTC month and agent): caps for a month and a thread, each in tokens and in dollars. The gateway checks before each call; at a cap
  it answers 402 with an OpenAI-shaped error and the thread gets one notice per turn ("Reached this thread's cap of 10,000 tokens. A lead can
  raise it.", or the project's month), with **Raise this thread's cap** for its leads (the thread gets the per-thread cap again). The Budget
  card shows the month's use (dollars when the provider reports cost, else tokens), per agent, and the four caps.
- **Pause all agents** (`ProjectAgentsPaused` / `ProjectAgentsResumed`): every running sandbox of the project is suspended, a turn running in
  one goes back in line ("Paused by Alex. researcher picks this up again when the agents are resumed."), and the dispatcher holds every
  delivery in the project (pinned agents too) until Resume, which pumps them.
- **One restart** when a sandbox dies mid-turn (`Dispatcher.SandboxLost`, from the runner's connection dropping or the pool finding the
  container stopped, whichever comes first): the first time the message goes back in line with "…stopped unexpectedly. Trying again." and the
  sandbox resumes on its volume; the second time for the same message the turn fails. Tool calls that were running are marked failed.

**Decisions**
- **No network at all, instead of an internal podman network.** A container on an internal network shares it with every other sandbox there
  and still needs a proxy container that can reach both the internal network and a server on the host's loopback, which rootless podman here
  doesn't combine with pasta. With `--network none` and a socket of its own, a sandbox has no route to anything, not even another sandbox,
  and the proxy knows which sandbox is asking without trusting a header. Round 1's pasta loopback option is no longer used. Tested first with
  a plain container: no network, the host's socket reachable as the sandbox's user (sockets 0666, folder 0755).
- **The proxy runs in the pool process**, which holds no secret but its own token; the server and gateway are reachable by construction (their
  own socket), so "always allowed" can't be misconfigured. Plain http is one request per connection (`Connection: close`, `Proxy-*` headers
  dropped), so a client can't hop to another host on a kept-alive connection; an `Upgrade` passes through.
- **The agent isn't held while it waits for an answer:** the proxy refuses at once (OpenCode reports the failed fetch and ends its turn) and
  the allow brings a short system message as the next prompt, so it tries again. "Allow once" lives in the proxy only, so a sandbox suspended
  before using it loses it.
- **A pool that restarts stops the containers it left running** (their proxy went with the old process); the server sees them gone and
  resumes them when their threads need them.
- **Budgets count what the gateway forwarded,** checked before each call: a call that starts under the cap may end past it, and the next is
  refused. GitHub Copilot reports no cost, so dollar caps don't stop its threads; the card and its hint say the token caps hold. OpenCode
  then fails its prompt with the gateway's words; the dispatcher drops that error when it repeats the turn's `BudgetReached`, so the thread
  says it once.
- **Pausing holds pinned agents' deliveries too** ("all agents"), but a pinned turn already running finishes; only sandboxes are stopped.
- **The restart is counted per message** (a `restart` flag on `TurnInterrupted`, traced through `TurnStarted` to the `MessageDelivered`), so a
  later message gets its own retry.
- phase10 uses a 90 s idle time so a sandbox doesn't stop between its steps; phase9 now finds its threads by who started them (two
  simultaneous posts may land in either order).

**Checked** (ports 5883–5888, everything in podman labelled and removed by label; the `cowork` project, its volume and `~/.weave` untouched;
OpenRouter never used)
- `dotnet test`: 82 server (5 new in `LockedTests`: an ask to the thread's starter, deduplicated, refused to the lead and to another pool,
  allowed once (the pool told, the system message delivered after the turn), for the thread (in the next start's rules) and denied; the
  gateway stopping a thread at 200 tokens with one notice and no second error, a member refused and the lead raising it, the month's cap, a
  dollar cap, the card's numbers; pause with a turn running and a new message held, resume sending both; a sandbox dying twice mid-turn;
  the setup command in the delivery and its output kept). 32 runner (5 new in `EgressTests` on real unix sockets and a loopback web
  server: a listed host tunnelled, others refused and reported once, allowed once and for good, plain http rewritten, the server socket;
  and the setup command running once before the first prompt). `bun run test` 33.
- Live: **`phase10` 27 checks, three runs** (screenshots `docs/screens/brief7/10-*` to `17-*`, dark and light, looked at; fixed from
  them: the budget stop said twice, a tool row left "running…" by the killed turn, a paused card saying "Stopped after 0 seconds idle", and
  this machine's repository path on the settings page). On the locked-down network: `phase9` 37 (round 1's demo), and the earlier checks on
  fresh scratch servers `phase1` 24, `phase2` 30, `phase4` 19 (LiveKit 1.13.11 as `cwb7-lk`, label `cwb7`, removed), `phase5` 22, `phase6`
  27, `phase7` 31, `phase8` 31. The importer's answer comparison ignores the two thread-status fields this round added (`pausedBy`,
  `usage`). The older scripts' own screenshots were restored.

**Not done / next**
- Leads aren't told at 80% of a budget, nor when an ask passes to them after 30 minutes; nobody is told when no pool is online.
- The proxy doesn't replace credential placeholders yet (the rules carry `replaceHeaders` as data; pushing from a sandbox needs it) and the
  thread token isn't a signed token the proxy could check itself (the proxy needs neither today: each sandbox has its own socket).
- The mockup's "web search" on the network list is a service, not a host; there is no web search in the sandbox yet.
- Round 3: CoWork's MCP tools in `session/new`, thread status and outcomes; deleting a done thread's volume.

## Brief 7, round 3: threads as work

**Demo:** `tests/live/phase11.mjs` (19 checks; Alex, Rosa and Sam in browsers; a real pool with real containers and real OpenCode 2.4.1 for the
researcher template, and a real runner on `build-01` with real OpenCode 2.4.1 for Alex's pinned reviewer). Sam writes in #checkout-app what
finance wants; #leads is private to Alex. Rosa asks researcher to read the channel and resolve the thread: it calls `cowork_read_channel`
(the row says `cowork_read_channel #checkout-app`) and `cowork_resolve_thread`; the post's pill went starting → working → done live and shows
the outcome's title beside it; the thread shows the outcome card ("done", title, summary, its branch "in its sandbox's volume on pool-01",
8 s, 16,158 tokens, 2 tool calls, 0 asks, Reopen). Sam asks it to search for "Q4 refund budget" and read #leads: the read is refused ("outside
its boundary"), the search's results come only from #checkout-app, and nothing from #leads reaches the answer. Rosa's search for "refunds"
shows **Outcomes** first, marked done; opening it opens the thread. Alex asks the pinned reviewer to do the same: it uses the same tools with
its runner's token, and its outcome says "on Alex's own access", its branch in a worktree on build-01. With 20 s idle and 20 s retention,
Rosa's done thread loses its volume ("no changes, deleted with its volume") and a thread where researcher edited README.md keeps it
("cowork/append-the-line-reviewed-by-25 has 1 changed file not committed, so its volume is kept").

**What changed**
- **CoWork's tools over MCP** (`Mcp.cs`): `/mcp` is an MCP server (Streamable HTTP, JSON answers; `initialize`, `tools/list`, `tools/call`,
  `ping`) with `cowork_read_channel` (a channel the agent takes part in, by default its thread's, or one thread), `cowork_search` (the
  search query with the agent's boundary in place of a person's: the channels in its audience; outcomes first) and `cowork_resolve_thread`
  (title and summary). A sandbox authenticates with its thread token; a pinned runner with its own token and `X-Cowork-Thread`, a thread
  its agent is in. Each call is for that one thread and is recorded as `CoworkToolCalled` (tool, refused or not, what it touched), on
  behalf of whoever asked.
- **The runner passes it in `session/new` and `session/load`** to every harness: over HTTP with the token and thread as headers when the
  harness says `mcpCapabilities.http` (OpenCode 2.4.1 does), else as a stdio server, the runner itself (`cowork-runner mcp <url>`) bridging
  each line to the endpoint. In a sandbox the URL is the server socket's loopback port, so it needs nothing from the proxy.
- **Outcomes** (`ThreadResolved`, migration `013`): a post of kind `outcome` with the title, summary, time since the thread began, tokens and
  cost (and the project's per-thread cap), calls, tool calls, asks, the branch and where it is, and the sandbox. `threads.resolved_*` remembers
  it. A person's new message (`MessageDelivered` not from the system) or **Reopen** (`ThreadReopened`) opens the thread again; the outcome
  stays, marked reopened.
- **Thread status** (`ThreadStatus.cs`): `waiting` (a pending ask: for whom), `working` (a turn open), `starting` (a message waiting for its
  agent, or a sandbox on its way), `done` (resolved), from the tables at read time. It rides on the root post (`threadStatus`), so the feed,
  the thread and live `thread-updated` carry it; permission answers and egress answers now update the thread too.
- **Search**: `ThreadResolved` is a document of kind `outcome` (title weighted 4x); `/api/search` returns `outcomes` first, with who asked
  and whether it's still the thread's outcome; threads and messages leave outcomes out. The web shows them as their own group, keyboard
  order included.
- **Retention** (`SandboxRetentionSet`, `SandboxDeleteRequested`, `SandboxKept`): the reaper asks the pool to delete a done thread's stopped
  sandbox once the retention time has passed since it was resolved (7 days unless set; Server settings). `DeleteSandbox` carries
  `KeepIfChanged`, the image and the base branch; the pool looks at the branch in the volume with a short-lived container (no network):
  changes to tracked files or commits not on the base keep it (`kept`, with the reason), else the volume goes with its branch. The outcome
  card says which. A thread whose volume was deleted starts afresh (`CreateSandbox`, not resume) if someone writes again.
- **Web**: `ThreadStatusPill` on the thread card and in the thread's header ("waiting for you" to whoever answers), the outcome's title on
  the post, `OutcomeCard`, Outcomes in search, the retention field in Server settings, a card footer for a deleted volume.
- **Design**: `docs/architecture/provisioning.html` says what was built where the flow changed (a copy in the volume, `--network none` with
  `server.sock` and `proxy.sock`, the setup step, `CreateSandbox`/`ResumeSandbox`/`SuspendSandbox`, budgets in tokens and dollars, CoWork's
  tools, retention) and marks what is still to build; same look.

**Decisions**
- **HTTP MCP, from the server.** OpenCode 2.4.1 advertises `mcpCapabilities: { http: true }` (checked with `initialize`), so the tools are an
  endpoint on the server rather than a process per session; the stdio bridge keeps any other ACP harness working.
- **OpenCode 2.4.1 calls MCP tools through a code tool.** Its ACP updates say `execute` with no detail; the call is in `rawInput.code`
  (`await tools.cowork.cowork_read_channel({ channel: '#checkout-app' })`; the finished update has `rawOutput.metadata.toolCalls`). The
  adapter names the row from the code (`cowork_read_channel #checkout-app`: the tool and its first quoted argument) and logs any tool call it
  can only name by its bare title. Checked with a probe against real OpenCode (built-in Copilot provider and an OpenAI-compatible one).
- **A refused tool call is an MCP error** (`isError`), as the protocol means it. OpenCode's code tool then throws, so a block that makes two
  calls stops at the refused one; the agent sees the refusal either way.
- **The boundary is the agent's channel audience** (added to the channel, or in its project), the same rule routing uses. Nothing in round 3
  writes outside the thread: resolving writes only its own outcome; a library is later.
- **Done is "resolved, and nothing since".** Status is computed, not stored, so it can't drift; `starting` covers a queued message for any
  agent in the thread and a sandbox on its way.
- **"Changes" means commits not on the base branch or changed tracked files.** Untracked files (what a setup command installed, build output)
  don't keep a volume. If the pool can't look, the volume is kept and the outcome says why.
- **`build.sh` publishes from a clean Release build:** one phase11 run used an image whose runner was an earlier build (its tool rows said
  `execute` though the source named them); a clean publish every time costs about 20 s.

**Checked** (ports 5883–5888, everything in podman labelled and removed by label; the `cowork` project, its volume and `~/.weave`
untouched; OpenRouter never used)
- `dotnet test`: 86 server (4 new in `WorkTests`: the MCP handshake and the three tools for a sandbox, #leads refused and not found,
  resolving shows on the post, in search for Rosa and not for Jo outside the project, and an agent's search finds it first, a new message
  reopens; a pinned agent with its runner's token for a thread it's in and refused otherwise, and a pool's token, a person's cookie, no
  thread all refused, Reopen; starting → working → waiting for Rosa → working → done; retention deleting one volume and keeping one with a
  commit, a deleted one starting afresh), and the importer's answer comparison ignores `threadStatus`. 36 runner (4 new: the HTTP MCP
  server for a harness that takes it, the stdio bridge otherwise, the bridge itself, MCP calls named from OpenCode's code tool, parsed from
  the line it really sends). `bun run test` 33.
- Live: **`phase11` 19 checks** (screenshots `docs/screens/brief7/18-*` to `22-*`, dark and light, looked at; fixed from them: the branch name
  breaking across lines in the outcome card, "3 replies" wrapping on the thread card, a kept or deleted volume showing "starting…" in the
  thread's header, one MCP call left as `execute` when its arguments held brackets). Every earlier check on this build, each on a fresh
  scratch server: `phase1` 24, `phase2` 30 (its pinned sessions now get CoWork's tools too), `phase4` 19 (LiveKit as `cwb7-lk`, removed),
  `phase5` 22, `phase6` 27, `phase7` 31, `phase8` 31, `phase9` 37, `phase10` 27.
- **Found by the regression:** phase10 failed once because adding a host made the project settings page reload, which overwrote the setup
  command Alex was typing, so an empty one was saved. The page now keeps a field someone is editing until they save it. phase10's budget step
  was model-dependent (a turn can finish in two model calls, under a 10,000-token cap checked before each call); it now uses 5,000 tokens,
  which any turn with a tool passes on its second call.
- **Not explained:** in one phase10 run the pool refused example.com and said so, but no ask was recorded and neither side logged an
  error; the next runs recorded it. The server now logs every refusal and what it did with it, and the pool retries a report that fails, so
  a repeat will show where it went. One run (the one I disturbed by rebuilding the web app under it) left a container whose start was
  already on its way when the pool stopped; it carried the pool's label and was removed by it, and the live checks now sweep twice.

**Not done / next**
- A project library (`cowork_save_doc`) and more tools (`cowork_start_thread`); web search in sandboxes; credentials by placeholder in the
  proxy; a signed thread token; an Anthropic-compatible gateway endpoint; notifications (80% of a budget, an ask passing to a lead, no pool
  online); a board of threads by status (the mockup's). A `deleting` sandbox whose pool restarts mid-delete stays `deleting`.

## Brief 7, round 3 follow-up: naming MCP tool rows reliably

**The report:** a phase11 run on 2f66421 failed at step 9: Rosa's thread had two rows reading "tool execute", though `CoworkToolCalled`
recorded both calls. That run's database showed each call's two `in_progress` updates arriving (two `ToolCallDetailed`) and still naming
nothing. With a temporary trace of every tool update in the sandbox runner, phase11 failed again on its first run, and the trace showed why:
the model wrote `await tools["cowork"].cowork_read_channel({ channel: "#checkout-app" })`, bracket access, which the parser (`tools.x.y(`)
didn't read. The code in `rawInput.code` is the model's, so its shape varies from run to run. The `completed` update is OpenCode's own:
`rawOutput.metadata.toolCalls: [{ tool: "cowork.cowork_read_channel", status: "completed", input: { channel: "#checkout-app" } }]`.

**The fix:** the finished row's title comes from that record (`AcpAdapter.Called`: the tool without its server, and its first string
argument), so a row is always named when it finishes; the in-progress parser also reads `tools["s"].t(`, `tools.s["t"](` and
`tools["s"]["t"](`, so it is usually named while it runs. The temporary trace is gone; a tool the adapter can only name by its bare title
is still logged, and phase11 can keep the sandbox runner's log (`DUMP_LOGS=<file>`).

**Checked:** a runner test replays the nine updates recorded from the failing sandbox (`tests/CoWork.Runner.Tests/oc2-mcp-tool-calls.jsonl`)
and a code shape no parser reads (the model destructuring `tools`): both rows end named. 37 runner tests. `phase11` three runs in a row,
19 checks each, every call named while running.
Then the full regression on this build: `phase1` 24, `phase2` 30, `phase4` 19, `phase5` 22, `phase6` 27, `phase7` 31, `phase8` 31,
`phase9` 37, `phase10` 27, `phase11` 19 (a fourth run in a row); `dotnet test` 37 runner and 86 server, `bun run test` 33. Nothing labelled
left in podman. The round 3 screenshots are from that phase11 run.

## Brief 8: threads you can find

**Demo:** `tests/live/phase12.mjs` (32 checks; Alex and Rosa in browsers, a real pool with real containers and real OpenCode 2.4.1 for the
researcher template, model calls through the gateway to this machine's Copilot sign-in). Rosa asks researcher what `roundTax` does: the thread
appears under #checkout-app in her list and in Alex's (he is in #general), live, titled "What does roundTax in src/tax.ts do?" with researcher's
avatar, and its dot goes starting → working → idle. Alex's row is bold with 2 unread; he clicks it and the thread fills the main area at
`/c/2/threads/1` (header: title, status, "in #checkout-app", the session line; the post, the replies, the `read src/tax.ts` row, its own
composer); the count goes, a reload opens the same view, and "in #checkout-app" goes back to the post, lit. Rosa's quick look has "Open full
view"; from there she asks researcher to resolve it: the outcome card appears and the title becomes the outcome's in the header and both
lists, the dot says done. She renames it ("roundTax rounds each line") and Alex's list follows. Alex starts five more threads: Rosa's list shows
the five most recent, newest first, then "All threads · 1 more"; a reply moves the oldest to the top; she can't rename Alex's thread until she
writes in it; the chevron folds #checkout-app's threads and a reload keeps them folded. Alex's private #leads thread is only in his list (404
to Rosa). researcher asks to reach example.com for Rosa: her row says "waiting for you" (amber, pulsing), Alex's "waiting for Rosa". The
Threads button opens All threads (7, each with title, agent, who started it, status, last activity); "Waiting for me" has Rosa's ask, "Done"
the resolved one, "Open" the other six, and Alex's "Waiting for me" is empty. Rosa denies the ask in the full view. In #billing (project
Billing, which researcher isn't in) her mention gets "researcher isn't in Billing. A lead can add it in the project's settings." in a new
thread, and no sandbox. Alex adds researcher to Billing (no repository): Rosa's next mention's starting rows say "No repository: add one in the
project's settings to give researcher something to work on." and it answers. On a phone the threads are in the drawer and a thread fills the
screen.

**What changed**
- **Events.** `ThreadRenamed` (thread stream: `title`, null to go back to the automatic one), `ThreadRead` (on `person-{id}`: the thread and
  the position read up to), `MentionUnreachable` (thread stream, by the agent for the person: `reason` project | channel | runner, the reply).
- **Projections.** Migration `014`: `threads.title`, `titled_at`, `last_at`, `last_position` (inline, in `ThreadProjection`: anything new in a
  thread moves it: a reply, an answer, a tool row, an ask, a notice, a card, an outcome); the `unread` view (async) gets `unread_replies`,
  `unread_thread_markers` and `unread_joined`, and the migration resets its checkpoint so it replays and counts existing threads.
- **`ThreadList.cs`**: a thread's summary (title and where it came from, agents, who started it, status, last activity, replies, unread), the
  title rule, the list rule (open, or done less than a day ago, or unread; at most five per channel, by last activity), All threads' filters,
  who may rename. Read at read time from the projection, like `ThreadStatus`.
- **API.** `GET /api/thread-list` (each visible channel's listed threads and its total), `GET /api/channels/{id}/threads?filter=open|waiting|done`,
  `PUT /api/threads/{id}/title`, `POST /api/threads/{id}/read`; `GET /api/threads/{id}` adds `thread` and `canRename`. `thread-updated` carries
  the summary, so the list and the full view follow a thread live, and a reconnect catches up as before.
- **Routing.** A person's mention of an agent that can't take it (not in the channel's project, or not in the channel, or with no runner) gets
  a short reply by the agent: in a new thread from a channel post, in the thread from a reply. The audit says "Rosa mentioned researcher
  (Alex's agent) in #billing. It couldn't take it: it isn't in Billing."
- **Sandbox card**: for a project with no repository the workspace row says so, before and after it starts.
- **Web.** `components/threads/`: `ChannelEntry` (a text channel and its threads in the list: avatar, title, unread count, status dot with its
  words on hover, the fold chevron, All threads), `ThreadDot`, `ThreadView` (the full view, rename in its header), `ThreadsView` (All threads),
  and `SessionLines` / `ThreadConversation`, taken out of `ThreadPanel` so the quick look and the full view share them. Routes: `/c/:id/threads`
  (All threads) and `/c/:id/threads/:root` (full view) beside `/c/:id/t/:root` (the quick look). `threads.ts` holds the route parsing, status
  words and the fold memory (localStorage, per channel). The channel header has Threads; the quick look has "Open full view" and the title.

**Decisions**
- **Title** (as the user put it in a follow-up, below): the name someone gave it; until anyone does, the outcome's title once an agent
  resolves it (and it keeps it if the thread is reopened); until then the first words of the opening post without mentions (at most 8 words,
  about 60 characters, "…" when cut, a capital first letter). A thread always has a title: an empty one is refused.
- **Who may rename:** anyone who may post in the thread, that is, any person who sees its channel.
- **Unread per thread** counts text replies by others after what the person read of that thread (opening it, beside the channel or in full,
  or writing in it), and not before they joined, were added to the channel, or the view began. Reading the channel doesn't read its threads:
  the feed shows only "N replies", not the replies. The channel's own unread count is unchanged (it still includes replies).
- **The list rule is on the server** (`/api/thread-list`), so it is one rule with tests; the browser asks again (shortly, once per burst)
  when a thread changes or a view moves, and patches a listed thread's title and status from `thread-updated` at once. "Five" counts the
  threads shown (open, done within a day, or unread); "All threads · N more" says how many others the channel has.
- **Status words:** starting, working, waiting for you (to whoever answers), waiting for Alex (to everyone else), done, and idle when nothing
  is going on (an open thread between turns, or a people-only thread).
- **Order** is by the position of the last thing that happened (positions are strictly ordered; times can tie within a millisecond).
- **A thread's row shows the first agent's avatar,** or whoever started it when no agent is in it (a people-only thread, a refused mention).
- **Refused mentions** come from people only: an agent naming another agent it can't reach stays silent, so an answer that says "@scribe"
  doesn't fill threads with notices. A reply that isn't a mention is still not passed on silently (brief 5).
- **Clicking a row opens the full view;** the post's "N replies" still opens the quick look, as the brief says.

**Checked** (ports 5883–5888 only; containers and volumes only with my labels, removed by label; the trial server on 5889, its data, pool and
containers, the `cowork` compose project and `~/.weave` untouched)
- `dotnet test`: 97 server (11 new in `ThreadListTests`: five title cases; the title rules with every rename permission, the live
  `thread-updated`, the outcome, a rename after it and back, reopen; five per channel newest first, a reply and an answer moving a thread up,
  the total, done for a day and kept while unread, the filters; unread per person, read once, writing reads, waiting for me; a private
  channel's threads, and joining it later with nothing unread; refused mentions for a project, a channel and no runner, in a thread too, the
  audit line, plain text left alone; the no-repository card), and the rebuild test replays a rename, a refused mention and a thread read into
  identical tables. 37 runner. `bun run test` 38 (5 new: the routes, the status words, a live update keeping the person's unread count,
  folding remembered, the open thread's summary). I broke the done-a-day rule (dropped the time) to see its test fail, then restored it; the
  first version of that test didn't fail (Rosa still had an unread reply at that point), so it now checks "done, read, under a day: listed".
- Live: **`phase12` 32 checks** on the final build. Four runs in all: the first stopped at the phone step on the test's own mistake (the
  thread it looked for had been pushed out of the five by then), then 31, 31 (in the regression below), and 32 once it also checks Rename. Screenshots in `docs/screens/brief8/` (dark and light), looked at; fixed from them: the fold chevron
  covering the channel's unread count, and the sandbox card growing past a narrow column (the quick look, a phone's full view; its grid now
  has a `minmax(0,1fr)` track, so long code chips truncate). Live-checked on a scratch server with a scripted harness too: Rename appears once
  you write in someone else's thread, and saves.
- The whole regression on this build, each on a fresh scratch server or its own: `phase1` 24, `phase2` 30 (real OpenCode 2.4.1 through
  Copilot), `phase4` 19 (LiveKit 1.13.11 as container `cwb8-lk`, label `cwb8`, on 7980–7982, removed), `phase5` 22, `phase6` 27, `phase7` 31,
  `phase8` 31, `phase9` 37, `phase10` 27, `phase11` 19, `phase12` 31. The older scripts' own screenshots were restored.

**Follow-up: threads the way Discord lists them** (the user showed Discord's channel list as the example). Each thread is its own row directly
under its channel, indented to where the channel's name starts and joined to it by a thin └ line drawn from the channel's `#` (one continuous
spine down the channel's threads, a curve into each); the row shows the thread's name, with CoWork's additions kept small on the same row (the
agent's avatar at three quarters size, the status dot, unread in bold with a count), and the open thread's row is highlighted like an open
channel. "All threads" sits under the last one, without a line. Only `ChannelEntry.vue` changed (the line is two pseudo-elements per row, in a
token mixed from `--muted`, so it follows both themes). `phase12` again: 32 checks; its screenshots in `docs/screens/brief8/` are this look.

**Follow-up: a thread you can focus on, with a title you can edit** (the user: "If I click on that, I can focus on it. Threads should also
have a Title and that title should be editable."). Clicking a thread's row opens it in the main area with its row highlighted (as built). The
title is now the heading of both the full view (19 px) and the side panel (16 px, with "Thread in #checkout-app" under it), edited in place
(`ThreadTitle.vue`: click the heading or the pencil; Enter saves, Esc cancels, an empty title is refused with "A thread needs a title.")
and saved as `ThreadRenamed` (now carrying `channelId`) by whoever edited it, anyone who may post in the thread. A rename always wins: the
outcome names the thread only if nobody has (`threads.title` holds either, `titled_at` says it was a person's; migration 014 sets it from
existing outcomes). The same title is on the channel-list row, in All threads, on the post's "N replies" chip (`threadTitle` on a root post,
live through `thread-updated`; when it is the outcome's it keeps `data-testid=thread-outcome`, so phase11 reads it as before), in search
(the opening post's document carries the thread's title in the 4x-weighted `title` column, so a thread is found by its name, and thread and
message hits show the title; `search_docs.named` stops an outcome overwriting a person's name; the index replays once) and in the audit
("Rosa renamed a thread in #checkout-app to “Coupon copy for checkout”."). `ThreadListTests`' title test was rewritten for these rules
(Sam, who never wrote in the thread, renames it; empty and too long refused; a rename survives a later outcome; an unnamed thread takes its
outcome's title and keeps it after Reopen; the chip, the feed, live; search by name; the audit line).
Checked: `dotnet test` 97 server and 37 runner, `bun run test` 38; **`phase12` 35 checks** (new: clicking the heading edits in place and
Esc saves nothing; an empty title is refused and stays in the editor; Enter saves as Rosa and Alex's list row and the post's chip follow
live; Rosa renames Alex's thread from the side panel's heading; search finds it by that title and the audit says so; All threads shows the
same titles), then the whole regression again on this build: `phase1` 24, `phase2` 30, `phase4` 19, `phase5` 22, `phase6` 27, `phase7` 31,
`phase8` 31, `phase9` 37, `phase10` 27, `phase11` 19, `phase12` 35. Screenshots in `docs/screens/brief8/` are from that run.

**Not done / next**
- A post whose thread holds only a refused mention's reply says "0 replies" on its card (notices aren't counted as replies); the reply is in
  the thread, which opens for whoever posted.
- The list rule is evaluated when the list is asked for (every thread change and view move asks); a done thread crossing its day while nothing
  happens leaves at the next change. `/api/thread-list` and `views-changed` go to every browser: fine at this size, worth batching later.
- An agent that mentions an agent it can't reach is silent (only people's mentions get the reply).
- The mockup's board (every thread in a project by status) is still to come; All threads is per channel.

## Brief 9: artifacts and markdown

**Demo:** `tests/live/phase13.mjs` (32 checks; Alex, Rosa and Sam in browsers; a real pool with real containers and real OpenCode 2.4.1 for the
researcher template, model calls through the gateway to this machine's Copilot sign-in; a real runner on `build-01` with real OpenCode 2.4.1
for Alex's pinned reviewer). Rosa asks researcher for a report she can share with finance and opens the thread's full view; Alex opens it too.
researcher writes `reports/tax-apis/index.html`, `style.css` and `chart.svg` and calls `cowork_show`: `ArtifactPublished` names the three files
by SHA-256. Its answer renders as markdown (a heading, a list, a table, `roundTax` as inline code, a link with `target=_blank` and
`rel=noopener noreferrer nofollow`), and its message has a card: thumbnail, "Tax APIs compared", HTML, "page · v1 · 9 KB · shown 10:34 PM".
Rosa started the thread and is looking, so her canvas opens by itself with the report in a tab (its stylesheet and image load); Alex sees
only the card, and clicking it opens the canvas for him (`?a=1`). Full screen gives the canvas the whole area; copy link puts
`/c/2/threads/1?a=1` on the clipboard; download gives `tax-apis-compared-v1.zip`; "Open full view" shows it at `/artifacts/1`; close removes
the canvas and `?a=`. From inside the report, `document.cookie` throws SecurityError, `fetch('/api/bootstrap')` (relative and absolute) and
`fetch('https://example.com/')` are refused, storage throws, and its origin is "null". Sam, not in Checkout app, gets 404 for the artifact and
its download, and its page says "No such artifact". Rosa types "add the cost for 50,000 refunds a month" under the canvas: it's posted in the
thread as her reply, "About Tax APIs compared · v1", and researcher hears `artifact="Tax APIs compared" version="1"
path="reports/tax-apis/index.html"`. researcher edits the page and shows it again: v2; Rosa's and Alex's open tabs and both cards show v2
live, v2 has the 50,000-refund costs, the picker lists v2 and v1, and v1 is still as it was. Alex asks his pinned reviewer for a markdown
review: it shows `reviews/tax/review.md` from its worktree on build-01, and Alex's canvas opens with the document rendered. The audit has every
version: "researcher (Alex's agent) showed “Tax APIs compared” v2 (html, 10 KB) in #checkout-app, for Rosa." On a phone the canvas fills the
screen with a back button.

**What changed**
- **The tool** (`Artifacts.cs`, `Mcp.cs`): `cowork_show({ path, title, open })` on CoWork's MCP server, described so a harness knows when to use
  it (a report, document, chart or table people should look at; show it again after changing it). The server asks the agent's runner over the
  runner hub (`CollectArtifact`, to its sandbox's connection or its pinned runner's) and waits for the upload (2 minutes at most).
- **The runner** (`ArtifactFiles.cs`): reads the file in the thread's workspace; a page (HTML or markdown) brings its folder (subfolders too),
  nothing hidden, no `node_modules`, no link out of the workspace (symlinks resolved), at most 25 MB and 500 files; a page at the workspace's
  top level, and anything that isn't a page, brings only itself. It uploads a manifest and the files to `/api/runner/artifacts/{request}` with
  its own token (a sandbox's thread token; a pinned runner's token and `X-Cowork-Thread`), or says why it couldn't, and the agent hears that.
- **Storage** (migration `015`, `ArtifactProjection`): `ArtifactPublished` on the thread's stream (artifact, version, thread, channel, project,
  path, entry, title, kind, size, each file's path, SHA-256, size and type, the card's post, open; by the agent, on behalf of whoever asked).
  The bytes are in `<data>/artifacts/ab/abcdef…` (content-addressed, kept once), never in events. The same path in the same thread is the
  next version. A card post (kind `artifact`) carries what the thread shows, and every card of an artifact shows its latest version.
  `ArtifactRedacted` (the agent's owner or an admin) removes a version's files and keeps the fact; deleting the channel removes the rows and
  then the bytes nobody names. Both are in the audit.
- **Serving**: short-lived signed addresses (`/a/{id}-{version}-{expiry}-{hmac}/{path}`, 30 minutes, the key a file in the artifacts folder),
  so a page's relative files work; with `default-src 'self'`, `connect-src 'none'`, `frame-ancestors` the app, `form-action 'none'`,
  `base-uri 'none'` and a CSP `sandbox allow-scripts` (an opaque origin even when opened on its own), nosniff and no referrer. The app frames
  it with `sandbox="allow-scripts"` and no `allow-same-origin`. `COWORK_ARTIFACT_ORIGIN` puts it on a host name of its own (then `/a/` answers
  only there) and `COWORK_APP_ORIGIN` is who may frame it. Markdown, CSV and JSON are served there as text; the app reads them from
  `/api/artifacts/{id}/versions/{v}/raw/…` (cookie, membership, attachment, never rendered). SVG is served as an image with a sandbox and no
  scripts. `GET /api/artifacts/{id}` (404 unless the person sees the channel), `…/download` (a file, or a page and its files as a zip),
  `POST …/redact`.
- **Replies about an artifact**: a post may quote an artifact of its thread and a version (`quote` on `MessagePosted`, refused for another
  thread's); the agent's `<cowork-message>` carries `artifact`, `version`, `path` and a short note to show it again if it changes.
  `cowork_read_channel` on a thread lists what was shown in it.
- **Markdown** (`render.ts`): markdown-it with `html: false`, highlight.js and DOMPurify, Weave Fleet's choices; task lists, tables in a
  scrolling wrapper, links in a new tab, mentions of known handles. Only real addresses become links ("Levy.io" in a sentence doesn't). A post
  never loads an image itself (it becomes a link). Search indexes the text as written.
- **Web** (`artifacts.ts`, `components/artifacts/`, `ui/kind-chip`): `ArtifactCard`, `ArtifactCanvas` (tabs, version picker, full screen, Open
  full view, copy link, download, redact, close, "Ask … to change this…"), `ArtifactView` (by kind), `ArtifactPage` (`/artifacts/:id`),
  `QuoteLine` on a reply. The full view's header says "N artifacts". The member list steps aside while the canvas is open.

**Decisions**
- **The canvas opens by itself only for whoever started the thread** (as decided before this brief), when the card was shown with
  `open: "canvas"` and the person is looking at the thread. From the quick look that means moving to the full view, where the canvas sits.
  Everyone else sees the card.
- **The runner fetches, the server asks.** CoWork's tools are an MCP endpoint on the server (brief 7), but the file is on the runner, so the
  tool call goes server → runner hub → upload over HTTP with the runner's own token, and the tool answers when the upload is in.
- **Only agents show artifacts for now**; people attaching files comes with the project library (option C), as decided.
- **No popups from a report**: the frame has no `allow-popups`, so a link inside a report doesn't open. A script could otherwise open a window
  to anywhere with what it holds.
- **A PDF** is framed without a sandbox and served without the CSP sandbox (the browser's viewer refuses sandboxed documents); nosniff keeps
  it a PDF. Not live-checked (headless Chromium has no PDF viewer).
- **The agent has to be told to show a change.** In the first live run researcher edited the page after Rosa's reply and said it was updated
  without showing it, so nobody saw v2. The fix is in the product, not the test's prompt: the tool's description and answer say a change is
  seen only when shown again, and a reply quoting an artifact says so too. The next runs showed v2 every time.
- **Web tests that use DOMPurify run in jsdom**: happy-dom 21 made DOMPurify drop headings and `pre`.

**Checked** (ports 5881–5888 only, LiveKit as `cwb9-lk` on 7980–7982 labelled `cwb9`, every container and volume removed by its label; the
trial on 5889, its data, pool, image and containers, the `cowork` compose project and `~/.weave` untouched; the sandbox image
`cowork-sandbox-opencode:2.4.1` rebuilt with this runner)
- `dotnet test`: 101 server (4 new in `ArtifactTests`: a sandbox shows a page with its files, the event names them by SHA-256 and holds no
  bytes, the card, the signed address and its policy, a forged or expired address and Jo outside the project get 404, the raw text is an
  attachment, a quoted reply and what the agent hears, another thread's quote refused, v2 with v1 kept as it was, the zip, a thread read lists
  both versions, the audit, a rebuild to identical tables; a pinned agent's CSV with its runner's token, the runner's reason passed on, someone
  else's token or request refused, an unsafe path and more than 25 MB refused; redaction keeps a file v2 still names and removes the rest,
  the fact stays, Rosa can't, deleting the channel removes everything; an artifact host name: `/a/` answers only there, framed only by the
  app), and `WorkTests` lists four tools. 41 runner (4 new in `ArtifactFilesTests`: a page's folder without hidden files or `node_modules`, a
  page at the top level and a CSV alone, nothing outside the workspace through `..`, an absolute path or a symlink, missing, hidden, a folder
  or too big refused, the upload's manifest, files, token and thread). `bun run test` 50 (8 markdown, 7 artifacts: who the canvas opens for,
  the quick look moving to the full view, tabs and versions, a redacted version skipped, links, CSV and JSON tables).
- Live: **`phase13` 32 checks**, three runs: the first stopped at step 5 (researcher edited without showing v2; fixed in the product, above),
  then 32 and 32, the last on the final build. Screenshots `docs/screens/brief9/01-…` to `10-…`, dark and light, looked at; fixed from them:
  table cells in a narrow column broke words letter by letter ("Provide r"); they now wrap at words and the table scrolls.
- The whole regression on the final build, each on a fresh scratch server or its own: `phase1` 24, `phase2` 30, `phase4` 19, `phase5` 22,
  `phase6` 27, `phase7` 31, `phase8` 31, `phase9` 37, `phase10` 27, `phase11` 19, `phase12` 35. The older scripts' screenshots went to a
  scratch folder (phase6 and phase8 still write their own; restored).

**Not done / next**
- Search doesn't find artifacts by title or text yet; the project library (option C) and people attaching files come next.
- A report's links don't open (no popups from the frame, on purpose); a page that loads anything from the internet shows without it.
- A PDF in the canvas wasn't live-checked (headless Chromium has no PDF viewer).
- Thumbnails are the page itself in a small frame, so a thread with many cards runs each page's scripts.
- An upload whose runner disconnects mid-way leaves the agent waiting the full 2 minutes before it hears so.

**Follow-up: the live checks' own sandbox image.** The user's trial pool runs sandboxes from `cowork-sandbox-opencode:2.4.1`, so the checks
no longer build or use that tag. `tests/live/lib.mjs` has `SANDBOX_IMAGE` (`COWORK_SANDBOX_IMAGE`, default `cowork-sandbox-opencode:2.4.1-b9`);
phases 9 to 13 fill it into the template's image field (phase9 still checks that the form offers `…:2.4.1` by default, then fills its own),
and say `TAG=… deploy/sandbox/build.sh` when it's missing. Built with `TAG=cowork-sandbox-opencode:2.4.1-b9 deploy/sandbox/build.sh`. Earlier
in this brief, before this rule, `deploy/sandbox/build.sh` was run once with its default tag, so `…:2.4.1` was rebuilt from this branch's
runner (it only adds `CollectArtifact`; phases 9 to 12 passed on it); the trial's image is to be rebuilt from main after the merge.

**Follow-up, found by running on that image: the egress refusal brief 7 couldn't explain.** phase10's first run on `…:2.4.1-b9` stopped at
step 5: the pool logged "refused example.com:443" and the server never heard, so Rosa got no ask (brief 7's "Not explained"). The proxy
wrote its 403 before reporting the refusal; when the client had already hung up (OpenCode's webfetch gave up: "transport error"), the write
threw, and the connection's handler swallowed the exception, so the report never ran and nothing was logged. The report now goes first, and a
403 that can't be delivered is logged. A runner test (ten clients that send CONNECT and hang up at once) reported none of the ten on the old
code and all ten with the fix. On the next phase10 run the pool logged exactly that case ("the client hung up before the refusal reached it")
and still told the server; Rosa got the ask. Checked: 42 runner tests; on `…:2.4.1-b9` `phase9` 37, `phase10` 27, `phase11` 19, `phase12` 35,
`phase13` 32 (phases 1 to 8 use no pool and passed on this server and web build).

## Brief 10: panels you can size

**Demo:** `tests/live/phase14.mjs` (27 checks; Alex and Rosa in browsers; a real pool with real containers and real OpenCode 2.4.1 for the
researcher template, model calls through the gateway to this machine's Copilot sign-in). researcher's instructions don't say where files go.
Rosa asks it for a report "so I can read it beside this thread" without naming a path: it writes `reports/…html` and shows it with a single
`cowork_show` call, not refused. Her canvas opens beside the thread at its default (612 px of 1274). Hovering the canvas's left edge shows a
2 px accent line and a resize cursor. She drags it 120 px narrower: the canvas and the thread resize live, the report inside reflows
(587 → 467 px), and a cover is over the window. She drags on past the minimum with the pointer over the report's iframe: the canvas stops at
320 px. She drags back across the iframe and further out: 812 px, remembered when she lets go. All the way out, the thread keeps 360 px.
Double-click puts the default back and forgets the chosen width. A chosen 762 px survives a reload (`localStorage` `cowork:panel:canvas`), and
Alex's browser keeps its own default. On a 1100 px window the canvas opens clamped to 414 px, leaving the thread its 360. Back on the wide
window it has 762 again. The handle is a focusable `separator` (`aria-valuenow` 762, `min` 320, `max` 914). Arrows move it 16 px, Shift+arrow
64, Home goes to 320 and End to 914. Keyboard focus shows the line drawn 4 px wide. The quick look (400 px) and the member list (220 px
beside it) have the same handle. Dragged to 520 and 280, each is remembered under its own key and kept on reload. Dragged all the way, the
quick look stops with the channel at 360. The member list alone beside the channel keeps its 280. On a phone the canvas and the quick look are
full screen with no handle. Asked to write a summary at `/tmp/opencode/summary.html` and show it from there, researcher's call is refused
with the new answer, and it writes `reports/summary.html` and shows that.

**What changed**
- **`ui/resize-handle`** (`ResizeHandle.vue`): a 0-wide box between a panel and what's beside it, with an 8 px grip over the border.
  Dragging uses pointer capture, and while dragging a fixed cover over the window (and `html.panel-resizing`: no text selection, iframes
  without pointer events) keeps a report's iframe from taking the pointer. Double-click resets, Esc while dragging puts the width back, and
  the width is remembered when the drag or key press ends. The line uses the accent token, so it follows every theme. Keyboard focus is the
  line at 4 px in place of the global ring (a base-layer rule in `main.css`, beside the menu rows' own).
- **`panels.ts`**: each panel's spec (canvas: at least 320, the thread beside it at least 360; quick look: at least 320, the channel at least
  360; member list: 180 to 420, the channel at least 360). Also `limitsFor`, `clampWidth`, `keyStep` and `dragWidth` (pure, tested),
  `loadWidth`/`saveWidth` (`cowork:panel:<key>`), `useElementWidth` (a ResizeObserver on the container's inner width) and `usePanelWidth`.
- **Layout**: the widths are CSS variables on the containers (`--canvas-w` on the thread view, `--thread-panel-w` and `--members-w` on the
  main area). The member list's room leaves the quick look its minimum, and the quick look gets what the member list leaves, so the two never
  squeeze the channel below 360 and never depend on each other in a loop. The canvas's default is unchanged (48% of the area, 420 to 860).
- **`cowork_show`**: a path outside the workspace gets "…isn't in your workspace (/work/repo); cowork_show only shows files inside it. Write
  the file inside your workspace (/work/repo), for example /work/repo/reports/summary.html, then call cowork_show with that path." (the
  same for a link pointing out). The description leads with "It takes the file's path, not its content, and the file must be inside your
  workspace (your working directory): first write it there with your file tool, …". **CoWork's MCP instructions** now give each tool's call
  shape and that rule (see below).

**Decisions**
- **What's remembered is what was chosen; what's shown is clamped.** A narrower window shows the canvas narrower without overwriting the
  chosen width, so a wider window gets it back.
- **The panel wins when there's no room for both** (a window just above phone width): the canvas keeps 320 and the thread gets what's left,
  as before this brief (when the canvas's minimum was 420).
- **Home is the narrowest and End the widest** (`aria-valuenow` is the panel's width; the handle is on the panel's left edge, so ArrowLeft
  widens it, the way the handle moves).
- **The member list has one width** wherever it shows (beside the channel, a quick look or All threads). Its default stays 220 beside a quick
  look and 244 otherwise until someone sizes it.
- **Found live: OpenCode 2.4.1 calls MCP tools from code.** In the first runs researcher's first call was
  `tools.cowork.cowork_show({ format: "html", content: html, title })`. The harness refused it ("path: Missing key") before it reached CoWork.
  Then it wrote `/tmp/opencode/tax_api_report.html`, got the new answer, and showed it from the workspace on the third try. The model never
  saw the tool's description: code mode lists only the MCP server's `instructions` up front, and descriptions come from `search(...)` after
  a guess. So the instructions now carry each call shape (`cowork_show({ path, title?, open? })`) and the rule (a path in the workspace,
  never the content). Two runs since showed the report on the first try. phase14 now counts the harness's own `cowork_show` rows, not only
  the calls that reach CoWork, and on failure copies each sandbox's OpenCode data (its database has every tool call's input) to
  `~/.cache/cowork-p14/diag/` before the cleanup. That is how this was found.

**Checked** (ports 5883 to 5888 only; LiveKit as `cwb10-lk` on 7980 to 7982 labelled `cwb10`, removed; every pool container and volume removed
by its label. The trial on 5889, `cowork-try`, the images `cowork-sandbox-opencode:2.4.1` and `:try`, the `cowork` compose project and
`~/.weave` untouched. `cowork-sandbox-opencode:2.4.1-b9` rebuilt from this branch with `TAG=… CTX=~/.cache/cowork-sandbox-build-b10`.)
- `dotnet test`: 101 server (`WorkTests` checks the instructions and the description's first sentences) and 43 runner (new:
  `/tmp/opencode/tax-report.html` gets the exact answer naming the workspace and `reports/tax-report.html`, `../secret.txt` too; the runner
  sends it in place of the files; following it works). `bun run test`: 56 (6 new in `panels.test.ts`: the limits, clamping, keyboard
  steps, drag, remembered and clamped widths and reset; the handle mounted in jsdom: its separator attributes, every key, double-click, a
  drag with the cover, and Esc).
- Live: **`phase14` 27 checks** on the final build. Six runs: the first three stopped at "first try" (above; fixed in the product), the
  fourth on the test's own mistake (it moved the pointer past the window's edge), the fifth passed with 26, and the sixth passed with 27
  after the focus cue changed and the area was measured inside its border (the channel had stopped at 358). Screenshots
  `docs/screens/brief10/01-…` to `06-…`, dark and light, looked at. Fixed from them: the global focus ring drew two extra lines around
  the 8 px grip.
- The whole regression on the final build, each on a fresh scratch server or its own: `phase1` 24, `phase2` 30 (real OpenCode 2.4.1
  through Copilot), `phase4` 19, `phase5` 22, `phase6` 27, `phase7` 31, `phase8` 31, `phase9` 37, `phase10` 27, `phase11` 19, `phase12` 35,
  `phase13` 32. The older scripts' screenshots went to a scratch folder (phase6 and phase8 still write their own; restored).

**Not done / next**
- The left channel list and the rail aren't resizable. The brief named the canvas, the quick look and the member list.
- No mockup was made for this brief. The handle is a thin line on an existing edge, so it was built straight from the brief. While the work was
  running the user asked why the quick look and the member list got handles too (they asked only for the canvas). That's brief item 4; taking
  them out is a few lines in `Shell.vue` if the user prefers only the canvas.
- Touch devices between phone and desktop width get the handle, but it's 8 px wide, which is small for a finger.

## Brief 11: sign in with single sign-on, no passwords

**Demo:** `tests/live/phase15.mjs` (21 checks; browsers for Alex, Rosa and Lee; a real OpenID Connect provider, Dex v2.44.0, in podman on
the host network at 127.0.0.1:5556). Alex opens a fresh server: the setup screen has a server name and "Continue with single sign-on", no
password. Dex signs him in and sends him back. CoWork says "alex@lumen.example · Signed in with single sign-on", suggests the handle `alex`,
keeps the server name he typed, and he is the admin in #general. Rosa's invite says "Alex invited you to Lumen Labs" and has no fields at all.
Dex sends her back to `/invite/<code>`. `alex` as her handle gets "Someone here is already @alex. Try another." With `rosa` she's in as a
member, at `/`. Lee, whom Dex knows but nobody invited, gets "lee@lumen.example isn't a member of Lumen Labs. Ask someone here for an invite
link." The API says 401 to him, and "Use another account" goes back to Dex. Rosa signs out: the sign-in screen is one button. She signs back
in and is the same member. The database has no `credentials` table and no password column. `logins` has two rows, Dex's issuer plus its
subject. The event log holds neither subject nor email. A second server started without the settings says "Sign-in isn't set up yet",
names the three settings, and has no form.

**What changed**
- **Server** (`Sso.cs`, `Api.Auth.cs`): ASP.NET Core's OpenID Connect handler, configured from `COWORK_OIDC__AUTHORITY`, `__CLIENTID` and
  `__CLIENTSECRET`. It uses the authorization code flow with PKCE, `response_mode=query` (a redirect back, so its correlation and nonce
  cookies can be SameSite=Lax), scopes `openid profile email`, userinfo, standard claim names and no tokens kept. `/auth/sso?intent=…` goes
  to the provider and `/signin-oidc` is the callback. The provider's answer goes into a 15-minute pending cookie (`cowork-sso`), not the
  session. `/auth/sso/done` then signs a member in (and drops the pending cookie). Anyone else goes back to the app: `/invite/<code>` or
  setup to choose a handle (`/api/auth/pending` gives the suggestion), or the sign-in screen to be told they aren't a member.
  `POST /api/setup` and `/api/invites/{code}/accept` now need the pending identity and take `handle` and `displayName`. A provider that
  says no ends at `/?sso=failed`. `/api/auth/login`, the password hasher and the "Wrong username or password." path are gone.
  `UseForwardedHeaders` takes `X-Forwarded-Proto` (only the scheme), so behind Caddy the redirect URL is `https://…`.
- **Data:** migration `016` adds `logins (issuer, subject, member_id, email, created_at)` and drops `credentials`. Like the secrets in `004`,
  a sign-in is written in the same transaction as `PersonJoined` and never put in an event. `/api/setup/status` adds `sso` (configured)
  and `passwordAccounts` (people but no sign-ins).
- **Web** (`AuthCard.vue`): the screens from the mockup. Step 1 is the provider button (with the server name for setup, and "X invited you
  to" for an invite). Step 2 shows who the provider signed in, with "Not you?", plus the handle and display name. Also: not a member, the
  provider said no, sign-in not set up, and a password-era server. An error clears once a field is edited. The invite dialog says "A
  link lets someone sign in with single sign-on and join."
- **Docs:** README "Sign-in" (the redirect URL, scopes and settings), `deploy/docker-compose.yml` and `deploy/.env.example` carry the
  three settings, and `AGENTS.md`'s model and security basics.

**Decisions**
- **Who someone is is the issuer and subject, never the email.** An email can change or be claimed; tests cover the same email under another
  subject, and the same subject under another issuer, both refused.
- **Not a member until a member.** The provider's answer never signs anyone in to CoWork by itself. A member who opens an invite is just
  signed in, and the invite isn't used. A member removed in CoWork who signs in at the provider is nobody.
- **Plain `http` authority only for loopback** (`RequireHttpsMetadata` is off only for `127.0.0.1`/`localhost`), for development and the live
  checks.
- **No migration** (the user's call): a server from before says "Accounts here used passwords" and asks for a fresh data folder.
- **Passkeys deferred** (the user's call, after weighing that a CoWork passkey would skip the provider and so IdP offboarding).

**Checked** (ports 5881, 5886 and 5887; Dex as `cowork-live-dex`, left running for the other checks. 5889, `~/.weave` and the user's compose
untouched.)
- `dotnet test`: 110 server, 10 of them new in `SsoTests`. They go through the real OpenID Connect handler against `FakeIssuer`, a provider
  inside the test process that checks client credentials, single-use codes and PKCE. Covered: setup through the provider, sign out and
  back in, not a member, issuer + subject not email, a member's invite unused, handle rules, the provider saying no, no passwords, no
  provider, and a password-era database. Every test that signed in with a password now signs in through it. `bun run test`: 56.
- Live: **`phase15` 21 checks** (twice; the first run failed on the test reading `textContent`, which runs the lines together), and
  **`phase1` 25 checks** on a scratch server with Dex (screenshots to a scratch folder). Screenshots `docs/screens/brief11/`, dark and
  light, looked at. Fixed from them: the "already @alex" error stayed after the handle was changed.
- The other live checks were changed the same way: `setUp`, `ssoJoin` and `ssoSignIn` from `lib.mjs`, and `SSO_ENV` for the servers they
  start. The whole regression ran on the final build, one check after another, each on its own scratch server with Dex (screenshots to a
  scratch folder): `phase2` 30 (real OpenCode 2.4.1 through Copilot), `phase4` 19 (LiveKit as `cwb11-lk` on 7980 to 7982, removed), `phase5`
  22, `phase6` 27, `phase7` 31, `phase8` 31, `phase9` 37, `phase10` 27, `phase11` 19, `phase12` 35, `phase13` 32, `phase14` 26. phase6 and
  phase14 first failed without reaching CoWork: another session's server held their default ports (5887, then 5888), so their start-up
  probe was answered by an older build. Rerun with `PORT=5881` and `PORT=5883`, both passed. `makeover` (a screenshot script, not in the
  regression list) wasn't run.

**Not done / next**
- Passkeys of CoWork's own (deferred above). Several providers at once, joining without an invite (e.g. by email domain), groups or roles from
  the provider, and single sign-out: none were asked for.

## Brief 12: a showcase and a README

(Written and run as brief 11 beside the sign-in brief; renumbered 12 when both reached main. Its live runs signed in with
passwords, before sign-in was merged.)

**Demo:** `tests/live/showcase.mjs` (19 steps; four people in browsers, a real pool with real containers, a real runner on `build-01`, real
OpenCode 2.4.1 for both agents through this machine's Copilot sign-in, LiveKit with fake microphones) builds Lumen Labs from nothing and
photographs it into `docs/screens/showcase/`. Alex Morgan sets the server up; Rosa Delgado, Sam Okafor and Priya Nair join with invite links.
Checkout app comes from a small invented repository (a cart that rounds VAT per line, refunds that round once on the total, a test, a setup
script) with #checkout-app, #checkout-alerts and 🔊 checkout-standup, a network list, a setup command and a budget; Research has no
repository and #research; #general is outside both. People talk in four channels. researcher (a team template) compares Tallyrate, Levy.io and
Vatbook and shows an HTML report on Rosa's canvas, answers Priya in the thread and makes the next version when Rosa asks under the canvas;
reviewer (Alex's own, pinned) reads the code with tool rows, explains `roundTax`, and resolves the thread when Sam asks; Rosa's sandbox is
refused example.com, she allows it once and researcher reads it; Priya's ask for status.tallyrate.example is left waiting; Sam's thread is
photographed while researcher works; Alex, Rosa and Sam meet in the voice room. The README's top is rewritten around the pictures.

**The pictures** (1600×1000 at device scale 2, dark and light; the phone 390×844 at scale 3): `01-hero`, `02-channel`, `03-agent-at-work`,
`04-ask-rosa` and `04-ask-alex`, `05-add-agent`, `06-project-settings`, `07-search`, `08-voice`, `09-audit`, `10-phone-thread`, and
`social.png` (1200×630: the hero, the logo, the name, one line and "Experimental"). Each is 250 to 700 KB as Chromium writes it; the script
reduces any over 700 KB to a palette PNG with [sharp](https://sharp.pixelplumbing.com) (libvips with libimagequant), installed with
`bun add sharp` in a scratch folder outside the repository (`SHARP_DIR`), so nothing needs root. In the final run none needed it.

**What changed**
- **`tests/live/showcase.mjs`**: the script, with what it needs and how to run it at its top and in the README's contributing notes. It starts
  its own server (5886), LiveKit (7980 to 7982, container `cowork-showcase-livekit-<port>`, label `cowork.showcase=1`), a pool with 4 slots and a
  60 s idle time, and the pinned runner with a scratch home. Before each picture it swaps this machine's paths (the bare repository, the
  runner's home, the data folder, the home folder) and the sandbox image's local tag for invented ones on the page, then checks the page's
  text for the home folder, the user name, the host name and the local image tag; a picture that still shows one fails the run. Posts go
  through the same API the composer uses (`POST /api/channels/{id}/posts`), so mentions start threads the usual way.
- **`tests/live/lib.mjs`**: Playwright comes from `PLAYWRIGHT_MODULE`, then wherever node finds it, then the dev machine's install, so the
  live checks run elsewhere too.
- **Member list (found in the pictures):** an agent's name and its AGENT badge ran under the Activity and Edit buttons (worst for its owner,
  who has both). With a mouse the buttons now show over the end of the row on hover or keyboard focus, and the name has the row; on a touch
  screen they stay in the row and the name truncates.
- **Search snippets (found in the pictures):** a snippet is cut from a message's markdown, so `**bold**`, `*it*` and `` `code` `` showed their
  markers. `plain()` in `search.ts` takes them out before the hits are marked (a lone `*` between words and `snake_case` stay); tested.
- **README:** the name and one line, the experimental notice (a GitHub warning block), the hero following the reader's theme (`<picture>`),
  what it is, what you can do today (a grid of the pictures, two a row), how it fits together (a mermaid diagram), the quick start, the
  detailed sections as before (configuration moved under the quick start; `cowork_show` added to the tools), what's not built yet (search,
  unread and the phone layout are built now; presence for people isn't), built with, contributing (tests, live checks, the showcase).

**Decisions**
- **The hero has no member list.** The full view gives the canvas the member list's room when it's open (brief 10), so the hero shows the
  thread, the canvas and the threads under their channels; the member list is in `02-channel` and `03-agent-at-work`. Changing that would
  change the app's layout, which the brief didn't ask for.
- **Display names are full names** (Alex Morgan, Rosa Delgado, Sam Okafor, Priya Nair), so the asks say "waiting for Rosa Delgado".
- **The sandbox image shows as `cowork-sandbox-opencode:2.4.1`** in the pictures: the run uses this machine's `:2.4.1-b9` tag (the trial owns
  `:2.4.1`), and the local tag means nothing to a reader.
- **Real hosts in the ask:** the allowed host is example.com, so researcher can really read it after Rosa allows it; Priya's waiting ask is
  for an invented `.example` host that's never allowed.
- **reviewer resolves only when asked.** In one run it resolved the thread in its first answer; Sam's reply then reopened it and the channel
  showed no "done". Its instructions now say so, and the script checks the thread is resolved at the end (and asks once more if not).

**Checked**
- Three full runs; each picture of the last one looked at in both themes. Fixed between runs: the working thread had finished before the
  channel was photographed (the channel is now taken first, as soon as the turn is open, with a longer task); the third sandbox waited 161 s
  for a slot (4 slots, 60 s idle); muted tiles still lit their speaking ring (the script waits for the next speaker update); the budget was
  cut off in project settings (the page is scrolled so the workspace stays at the top).
- With the two app fixes: `dotnet test` (Runner 43, Server 101), `bun run test` (57, with the new snippet test), and the live checks that
  cover them on the final build: `phase8` 31 (search), `phase6` 27 (the member list's Activity button), `phase14` 26 (the member list's
  width and handle). Their screenshots went to a scratch folder (phase6 and phase8 still write their own; restored).
- Ports 5886 to 5888 only; LiveKit on 7980 to 7982; every pool container and volume and the LiveKit container removed by their labels after
  each run. The pictures and the committed files were searched for this machine's user name, host name and home paths: none.

**Not done / next**
- The website page (`cowork.tryweave.io`) is a later brief in the website's own repository; the pictures and `social.png` are ready for it.
- The pictures come from real model answers, so each run's wording differs (the report's title and figures, the outcome's title). The script
  checks what must be true (a report shown and changed, an outcome, an ask allowed and one waiting, a turn photographed while it works), not
  the words.
- The member list's Activity and Edit now show on hover; on a touch screen wider than a phone the name still truncates beside them.

## Brief 13: pages that look chosen

(Built as brief 12 on the showcase's line; renumbered 13 when it reached main after sign-in and the showcase.)

**Demo:** `tests/live/phase16.mjs` (10 checks; Alex and Rosa in browsers, Rosa's computer light and CoWork in its default dark; a real pool with
real containers on `cowork-sandbox-opencode:2.4.1-b13`, real OpenCode 2.4.1 through this machine's Copilot sign-in, Haiku 4.5). researcher's
instructions say nothing about design, and Rosa asks in brief 10's words. The sandbox's runner has put `cowork-design` in the harness's home;
OpenCode loads it with its own skill tool before it writes the page; the page has its own dark and light, links `/a/fonts/fonts.css` and loads
nothing from the internet, and its fonts load in the sandboxed frame; it's dark beside dark CoWork although Rosa's computer is light; she
switches CoWork to light and the same page (no reload) turns light, and back; the card's thumbnail is told too; opened on its own it follows
the computer; the app's copy is the page as researcher wrote it.

**What changed**
- **`cowork-design`** (`src/CoWork.Contracts/Skills/cowork-design/SKILL.md`, embedded once, read by `Skills.Design`): Weave Fleet's
  fleet-design adapted for CoWork (the user agreed to bring it in). Kept: the plan, the rules for type, colour and layout, the generic looks to
  avoid, copy. New: where the page goes and `cowork_show`; nothing from the internet; the font kit; both themes always; the canvas's widths; a
  five-point recipe for reports (the answer first, amounts as bars with a pattern that works, quiet tables, no side stripes or boxed sections,
  one accent); a self-check instead of Fleet's browser check. NOTICE says where it came from.
- **Native skill in sandboxes:** `HarnessSkills.Install` (runner) writes it to `~/.claude/skills/cowork-design/` (Claude Code; OpenCode reads
  it too) and `~/.agents/skills/cowork-design/` (Codex; OpenCode too) when the runner starts in a sandbox. OpenCode 2.4.1's folders were read
  from its binary (its config folder, `~/.claude`, `~/.agents`). Not on a pinned runner: that home is its owner's.
- **Through MCP, everywhere:** `cowork_design_guide` returns the same text (without its front matter), and the MCP instructions say to read
  the guide before writing a page ("the cowork-design skill if you have it, otherwise cowork_design_guide({})") and that fonts come from
  `/a/fonts/fonts.css`. Calls are recorded like the other CoWork tools.
- **Font kit** (`src/CoWork.Server/PageKit/fonts`, embedded): Fraunces, Instrument Serif, Source Serif 4, IBM Plex Sans, Bricolage
  Grotesque, IBM Plex Mono; Latin, 13 files, 290 KB; served at `/a/fonts/` with `Access-Control-Allow-Origin: *` (a font is fetched with CORS
  and the frame's origin is opaque). A page's own font files get the same header. Licences beside them and in THIRD-PARTY-NOTICES (the
  generator lists them).
- **Light and dark follow CoWork:** the canvas and the card's thumbnail add `#scheme=dark|light` (from the chosen theme's colour scheme) to the
  page's address. The server adds `PageKit/scheme.js` just before a served page's last `</body>`: it rewrites the page's own
  `prefers-color-scheme` media rules (style sheets, imports, nested rules, `<link media>`, `<source media>`) to match, sets `color-scheme` on
  the root only if the page declared both, and runs again on `hashchange`, so a theme change doesn't reload the page. Downloads and the app's
  raw copy are untouched.

**Decisions**
- **One text, two routes.** The skill is embedded in Contracts so the runner and the server can't disagree. Changing it means a new sandbox
  image (the runner installs its own copy); the MCP route changes with the server.
- **The kit is served, not copied.** The proposal had agents copy font files into each report's folder; serving them at `/a/fonts/` is less
  for an agent to get wrong and works the same on every runner. A downloaded page falls back to its stack.
- **Literata out, Source Serif 4 in.** Literata's static Latin file sets words almost touching at body sizes ("acrossall27"), seen in the
  first live report.
- **The frame keeps its white background** while it loads: a page without a background of its own and without dark rules stays readable.
- **The skill's tool row says only "skill".** OpenCode titles its skill tool that way; which skill it loaded is in its database. phase16
  reads it from there; naming it in the row is a later change to the adapter.
- **Haiku needed the recipe.** With the guide alone, its first report used a filled header row and side stripes (both on the avoid list) and
  didn't open with the answer. With the recipe: the answer first, but bars sized by preference (100, 80, 62 for $24, $30, $18.50) and in
  dollars; then right widths that `flex: 1` overrode; then, with the bar pattern, right on all counts (55, 100, 68 % for €0.22, €0.40,
  €0.27). phase16 prints what each report did of the recipe and the bars as drawn, without failing on the model's taste.

**Checked**
- phase16 passed five times on the way (each run: a new sandbox, a new report); the last one's pictures in `docs/screens/brief13/`, looked
  at in both themes. Claude Code and Codex each list `cowork-design` from a scratch folder holding the skill (project level), so the file is a
  skill both accept.
- `dotnet test` (Runner 44, Server 104), `bun run test` (58), and the live checks for the canvas on the b12 image: `phase13` 32 (its frame
  checks too: no cookie, no API, no network, with CoWork's script in the page) and `phase14` 26, screenshots to a scratch folder.
- **phase14's focus check raced the line's fade** (it read opacity 0.84 mid-transition, twice); it now waits for the computed opacity to be
  1 before reading. Not this brief's change, found running it.
- **Two sessions, one scratch folder:** the first phase13 and phase14 runs failed because the sign-in worktree ran the same phases at the
  same time, and the live checks use fixed folders (`~/.cache/cowork-p13`, `-p14`). Rerun alone, both passed; that session was told.

**Not done / next**
- A pinned runner's agent was checked through the MCP tool in the server's tests, not live with a real harness.
- Pages that pick their theme with `matchMedia` in JavaScript still follow the computer; the guide says not to.
- The theme follows CoWork's light or dark, not its exact palette (Nord, Dracula…).
