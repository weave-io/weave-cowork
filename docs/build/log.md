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
