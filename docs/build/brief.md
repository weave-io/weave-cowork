# Build brief: the smallest working Weave CoWork

Read `AGENTS.md` first, then `mockups/cowork/index.html` and `plan.html` (open them in a browser or read the HTML).

The user wants **the smallest working prototype to test in the morning**: a server they can self-host, where people
sign in, a real agent takes part, and threads are harness sessions. Smallest means: no feature beyond the demo
sentences below. Everything else goes on the Later list at the bottom.

## The user journey (this is the acceptance test)

The user will walk through exactly this in the morning, on a server they host. Every step must work in the real app,
and the final live check must walk all of it with Playwright (Alex and Rosa in two browser contexts, a real runner).

1. **Set up.** Alex opens a fresh server and is asked to set it up: server name ("Lumen Labs"), his username,
   display name and password. He's the first **admin**. He lands in the app with a `#general` channel.
2. **Sign in and out.** Alex signs out and back in. A wrong password says so.
3. **Create channels.** Alex creates `#checkout-app` with a topic, and a private `#leads` channel.
4. **Invite people.** Alex opens "Invite people", gets a link (expires in 7 days, can be used 10 times), and can see
   and revoke the links he made. Rosa opens the link in another browser, signs up (username, display name,
   password) and lands in Lumen Labs as a **member**. A revoked or expired link says so instead.
5. **Run channels.** In each channel's settings, an admin (or the channel's creator) can rename it, change its topic,
   add or remove people and agents, and delete it. Public channels: every person in the server can see and join them.
   Private channels: only the people and agents added. Rosa doesn't see `#leads` until Alex adds her. A member can't
   delete a channel they didn't create. Admins can make another person an admin in "People" (server settings), and
   remove a person from the server.
6. **Post and start threads.** Alex and Rosa post in `#checkout-app` and see each other's posts at once. Either can
   start a thread on any post ("Reply in thread"), and the thread shows under the post as "N replies".
7. **An agent joins.** Alex opens "Invite an agent": name `reviewer`, about, instructions, harness command (default
   `opencode acp`), workspace folder. He gets a one-time code and the exact command to run. He runs
   `cowork-runner connect <server> --code …` and the dialog shows "reviewer is connected". He picks which channels
   reviewer joins (`#checkout-app`). reviewer appears in the member list as an agent, owned by Alex, online.
8. **The agent takes part.** Alex posts "@reviewer what does this repository do?". A thread opens on that post (the
   thread is a harness session) and reviewer's answer streams in, with tool calls as compact rows. Rosa replies in
   the thread "@reviewer and what tests does it have?"; reviewer answers in the same session and knows Rosa asked.
   If reviewer asks permission for something, only Alex sees Allow / Deny; Rosa sees "reviewer is waiting for Alex".
9. **The agent goes offline.** The runner stops: reviewer shows offline, a new mention waits in the thread ("waiting
   for reviewer's runner"), and it's answered when the runner comes back.
10. **Admin over agents.** Alex can remove reviewer from a channel, and remove (or disconnect) the agent entirely,
    which cuts off its runner token.

## Phase 1: the spine (people, channels, live posts, threads)

**Demo:** journey steps 1–6.

- Server: ASP.NET Core 10 minimal APIs, cookie auth, SQLite (Dapper, numbered `.sql` migrations), SignalR hub
  `/hubs/app` for browsers (join a channel's group only if a member; events: post created/updated, thread updated).
- Accounts: username + display name + password (`PasswordHasher<T>`). First-run setup when there are no people.
  Invite links: admin creates one (expiry 7 days, max uses); `/invite/{code}` lets a new person make an account.
- Roles: admin and member at the server level. Channels: public or private, with a creator and members; settings to
  rename, set the topic, manage members and delete (admin or creator). Every API and hub call checks this.
- Threads for people: any post can start a thread; replies in the panel; "N replies" under the post.
- Web: Vue 3 + Vite + TS + Tailwind. Screens: setup, sign in, invite sign-up, the app shell from the mockup
  (servers rail can be one static icon, channel list, feed, composer, member list). Dark and light from theme tokens.
- The server serves the built web app from `wwwroot` and falls back to `index.html` for client routes.

## Phase 2: a real agent in a thread (the point of the prototype)

**Demo:** journey steps 7–10. In detail: Alex opens "Invite an agent", names it `reviewer`, gives instructions, picks the harness command
(default `opencode acp`) and a workspace folder, and gets a one-time code and the command to run. On the same
machine he runs `cowork-runner connect http://localhost:5880 --code K7QF-2M9X`; reviewer shows as online in the member
list. In `#checkout-app` Alex posts "@reviewer what does this repository do?". A thread opens under the post, and
reviewer's answer streams into it, with its tool calls as compact rows. Rosa replies in the thread "@reviewer and
what tests does it have?"; reviewer answers in the same session (it remembers the first answer) and knows Rosa asked.

- Agent = member with kind `agent`, owner, instructions, harness command (string array), workspace path.
- Enrolment: the agent's one-time code (10 min, single use) is exchanged by the runner for a token (32 random bytes,
  stored as a SHA-256 hash). The runner saves `{server, agentId, token}` in `~/.config/cowork-runner/agents.json`
  (respect `XDG_CONFIG_HOME`) and reconnects with it: `cowork-runner run` serves every saved agent.
- Runner: .NET console app, connects to `/hubs/runner` with the token, reconnects with backoff. The server sends it
  "deliver this message to thread T" and the runner sends back stream events (text delta, tool call started/finished,
  turn ended, error). The server turns those into thread posts and pushes them to browsers.
- ACP adapter: spawn the agent's harness command once per agent (own process group), `initialize`, then one
  `session/new` (cwd = workspace) per thread, `session/prompt` per message, map `session/update` notifications
  (`agent_message_chunk`, `tool_call`, `tool_call_update`) to stream events. The agent's instructions go in the first
  prompt of each session, before the first message. Wrap every message as in `AGENTS.md` rule 4.
  Weave Fleet has a working C# ACP client you may copy and adapt, with a comment naming where it came from:
  `~/source/weave-fleet-worktrees/fleet-interesting-concept-something-likely/src/WeaveFleet.Infrastructure/Harnesses/Acp/AcpClient.cs`
  and `AcpAgentProcess.cs` (same owner; read `AcpMapper.cs` for how updates map).
- Permission requests (`session/request_permission`): send them to the server, which shows an approval card **only to
  the agent's owner** in the thread (Allow once / Deny); everyone else sees "reviewer is waiting for Alex". The answer
  goes back to the runner.
- Routing: a top-level post that @mentions an agent creates a thread on that post and delivers it. A reply in a thread
  goes to every agent already in that thread plus any agent it @mentions. One turn at a time per (thread, agent):
  later messages queue. Runner offline: the message waits and goes out when it connects; the thread says so.
- The thread panel shows the session line from the mockup: agent, harness command, runner host, who started it.
- Agent-to-agent: an agent's own post never triggers itself; agent-to-agent turns stop after 3 without a person.

**The real harness:** use OpenCode 2 at `~/.weave/harnesses/opencode2/.opencode/bin/opencode acp`, run with a scratch
`HOME` (and `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, `XDG_STATE_HOME`, `XDG_CACHE_HOME`) under `~/.cache/cowork-oc2/` so it
never touches the live Weave Fleet's data. With no sign-in, OpenCode 2 offers free models; pick one in its config if it
needs one. If OpenCode 2 can't answer, try Claude Code through Zed's ACP adapter (`bunx @zed-industries/claude-code-acp`)
and say which worked. A fake ACP agent (a tiny script answering canned updates) is for automated tests only; the
demo must use a real harness.

## Phase 3: self-host it

**Demo:** from a clean clone, `docker compose -f deploy/docker-compose.yml up -d --build` (podman here) starts the
server with its SQLite file on a volume; the setup page opens; a runner started on the host connects to it and the
phase 2 demo works against the container.

- `deploy/Dockerfile`: multi-stage (bun builds `web/`, `dotnet publish` the server), runs as a non-root user.
- `deploy/docker-compose.yml`: the server on port 5880, a volume for data. A commented Caddy example for TLS.
- The runner as one file: `dotnet publish src/CoWork.Runner -c Release -r linux-x64 --self-contained
  -p:PublishSingleFile=true` (also `osx-arm64`, `win-x64` documented). `scripts/publish-runner.sh` builds them.
- `README.md`: what it is, run it locally (`dotnet run` + `bun run dev`), self-host with compose, connect a runner,
  invite people and agents, and what's not built yet.

## Phase 4 (only if 1–3 are done, checked and committed): voice channels

**Demo:** Alex and Rosa join 🔊 Standup and hear each other; the channel list shows who's in it.
LiveKit server in the compose file (`docker.io/livekit/livekit-server`, dev keys from env), the CoWork server mints
room tokens (HS256 JWT with a video grant), the web app uses `livekit-client`. Agents in voice are on the Later list.
Check with Playwright's fake media devices. If it isn't working within about 2 hours, stop and leave it on the list.

## How to work

- One phase at a time, in order. Commit when its demo works live, with its screenshots in `docs/screens/`.
- **Live check each phase yourself** with Playwright: two browser contexts (Alex and Rosa) against a scratch server
  on a port in 5880–5889, data under `~/.cache/cowork-scratch/`, and in phase 2 a real runner with OpenCode 2.
  Look at the screenshots. Fix what you find before moving on.
- Tests that matter: auth and membership checks (a non-member can't read or join a channel's group), invite rules,
  routing (mention → thread → delivery, reply → same session, queueing, offline), the author wrap, enrolment (code
  once, token hashed), the ACP mapping (with the fake agent). Web: the event reducer. Keep them fast.
- After each phase, append a short entry to `docs/build/log.md`: what works, how you checked it, what's left.
- If you're blocked on something only the user can decide, write it in `docs/build/log.md`, choose the most
  reasonable option, and carry on. Don't stop and wait.
- When all phases you can finish are done, report back with `fleet_message` to the session that sent you this brief.

## Later (not now)

OpenID Connect and passkeys; agent keys (ed25519) instead of tokens; hosted runners in containers; agent tools
over MCP (`cowork_read_channel`, `cowork_start_thread`); agents starting threads; resolving threads; reactions;
roles and channel permissions beyond membership; several servers in the rail; agents in voice; search; phone layout
polish; sharing agents across servers.
