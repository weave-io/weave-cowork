# Weave CoWork: agent instructions

Weave CoWork is a self-hosted Discord for people and agents: text and voice channels, people who sign in, agents with
their own identities on any harness, and threads that are harness sessions. The design is in `mockups/cowork/`
(`index.html` is the click-through, `plan.html` the architecture). Read both before changing behaviour.

## Toolchain

- **.NET 10** for the server, the runner and their tests (`dotnet build`, `dotnet test`). Solution: `CoWork.slnx`.
- **bun** for the web app in `web/`: `bun install`, `bun run <script>`, `bunx <tool>`. Never `npm` or `npx`.
  bun lives in `~/.bun/bin` on the dev machine: `PATH=$HOME/.bun/bin:$PATH`.
- SQLite through `Microsoft.Data.Sqlite` + Dapper, with numbered SQL migrations.

## Layout

```
src/CoWork.Server     ASP.NET Core: minimal APIs, SignalR hubs, auth, SQLite, serves the built web app
src/CoWork.Runner     cowork-runner: connects out to a server, runs agents' harness sessions over ACP
src/CoWork.Contracts  what the server and the runner say to each other
web/                  Vue 3 + Vite + TypeScript + Tailwind; built into the server's wwwroot
tests/                xUnit projects; web tests with vitest
deploy/               Dockerfile, docker-compose.yml, examples
docs/                 build briefs, screenshots
```

## The model (don't blur these)

- **Member:** a person or an agent. Both have a handle and a display name; posts have an author member.
- **Person:** signs in with single sign-on (the server's OpenID Connect provider; no passwords) and joins with an invite
  link. The first person to open a fresh server sets it up and is admin.
- **Agent:** has an **owner** (a person), **instructions**, a **harness command** and a **runner** where it works. A
  runner enrols with a one-time code and then authenticates with a token the server stores only as a hash.
- **Channel:** text or voice, in a category. Members see a channel only if they may.
- **Thread:** hangs off a post in a channel. **A thread that involves an agent is backed by one harness session per
  agent**, on that agent's runner. Every reply in the thread is a prompt to that session, wrapped with its author.
- **Owner approves:** an agent's permission requests go to its owner only; everyone else sees that it's waiting.

## Rules for building for agents (learnt in Weave Fleet)

1. **Harness specifics stop at the runner's adapter.** The server never knows which harness answers. ACP (Agent
   Client Protocol, JSON-RPC over stdio) is the first adapter; any ACP agent command works (`opencode acp`, Zed's
   Claude Code adapter, `copilot --acp`). Expect gaps and add native adapters only when ACP can't do something.
2. **One piece of work, one session.** A thread is a session: small contexts, threads run side by side.
3. **Agents pull context; we don't inject it.** No channel history pasted into prompts. Agents get tools
   (`cowork_read_channel`, …) and use them when they need to.
4. **Say who's speaking.** A message reaches a session as
   `<cowork-message from="@rosa" name="Rosa" owner="false" channel="#general" thread="…">text</cowork-message>`.
5. **Asks go to whoever answers for the agent**, as cards with buttons; others see "waiting for Alex".
6. **Limit loops.** Agent-to-agent turns in a thread stop after 3 without a person.
7. **Harness updates break things.** Pin the harness version in tests; live-check with a real harness.
8. **Processes:** start harnesses in their own process group and kill by your own PID or group. Never kill by name
   (`pkill -f opencode`, `pkill node`): the dev machine runs other agents and a live Weave Fleet.

## Rules for building with agents

- **Thin vertical slices.** Each phase has a demo sentence. Get the demo working end to end first, then harden.
- **Small commits** with tests, on `main` (the repo is local-only for now). Commit messages say what and why.
- **Live-check every phase yourself** on a scratch server: two browser contexts signed in as two different people and a
  real runner with a real harness. Tests miss what a live check finds.
- **Screenshot the real app** for each phase into `docs/screens/<phase>-<what>.png`, and look at them.
- **Invented data only** in fixtures, seeds, screenshots and docs: Alex, Rosa, Sam, "Lumen Labs", #checkout-app.
  Never real people, companies or projects.
- **No wall-clock waits in tests.** Wait on signals (a hub message, a row, a process exit), with a generous timeout.
- **Design rules from day one:** theme tokens as CSS variables (dark and light), shared components in
  `web/src/components/ui/`, sentence case, "…" not "...", "Try again" not "Retry". Match the mockup's look.
- **Security basics:** no passwords (standard OIDC, nothing vendor-specific; a person is the provider's issuer + subject,
  never the email), tokens stored hashed, cookies HttpOnly + SameSite=Lax, every API and hub method checks membership,
  authors come from the caller and never from the body.

## The dev machine (Ubuntu, 14 GB)

- **Memory:** cap heavy things (builds, browsers, servers):
  `systemd-run --user --scope -q -p MemoryMax=2500M -p MemorySwapMax=0 -- <cmd>`.
- **TMPDIR** under `~/.cache` (e.g. `export TMPDIR=$HOME/.cache/cowork-tmp`); `/tmp` is often full.
- **Ports:** CoWork's scratch servers use 5880–5889. Check a port is free (`ss -ltnpH "sport = :5880"`) and stop
  your own server by PID or port, never by name.
- **Long commands:** run them in the background and wait with `timeout 580 bash -c 'until …; do sleep 5; done'`;
  never start a command with `sleep`.
- **Playwright:** `PLAYWRIGHT_HOST_PLATFORM_OVERRIDE=ubuntu24.04-x64`. A working install is at
  `~/source/crumb/node_modules/playwright/index.mjs` (import it by path from a script outside the repo).
- **Containers:** rootless podman; use fully qualified images (`docker.io/library/…`). Published ports aren't
  reachable from the agent's shell; check from inside with `podman exec <name> wget -qS --spider http://127.0.0.1:<port>/`.
- **Harnesses here:** OpenCode 2.4.1 at `~/.weave/harnesses/opencode2/.opencode/bin/opencode` (`opencode acp`),
  Claude Code (`claude`) and Codex (`codex`) on PATH. **Don't touch `~/.weave`'s data or `~/.weave/fleet.db`**: they
  belong to the live Weave Fleet. Run harnesses for tests with a scratch `HOME`/`XDG_*` under `~/.cache/cowork-…`.
- **Models for live checks:** OpenCode's free models hit their usage limits. The user agreed (25 Nov 2026) that live
  checks use the GitHub Copilot subscription when it's signed in locally: `tests/live/lib.mjs` then links (never copies)
  `~/.local/share/opencode/auth.json` into the scratch home and uses `github-copilot/claude-haiku-4.5`.
  `OPENCODE_MODEL` picks another model; `OPENCODE_SIGN_IN=none` goes back to the free models.
