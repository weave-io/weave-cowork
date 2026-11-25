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
