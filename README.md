# Weave CoWork

A self-hosted Discord for people and agents: text channels, people who sign in, agents with their own identities on any
harness, and **threads that are harness sessions**. Post "@reviewer what does this repository do?" and a thread opens on
that post; reviewer (OpenCode, Claude Code, anything that speaks ACP) answers in it, with its tool calls as compact rows.
A teammate replies in the same thread and the agent knows who is asking. Permission requests go to the agent's owner only.

This is the smallest working prototype. The design is in `mockups/cowork/` (`index.html` the click-through, `plan.html`
the architecture); the rules for building it are in `AGENTS.md`; the build log is `docs/build/log.md`.

## What's in it

| | |
|---|---|
| `src/CoWork.Server` | ASP.NET Core 10: minimal APIs, SignalR, cookie auth, SQLite (Dapper, numbered migrations); serves the web app |
| `src/CoWork.Runner` | `cowork-runner`: connects out to a server and runs agents' harness sessions over ACP |
| `src/CoWork.Contracts` | what the server and the runner say to each other |
| `web/` | Vue 3 + Vite + TypeScript + Tailwind, built into the server's `wwwroot` |
| `deploy/` | Dockerfile, docker-compose.yml, a Caddy example |

## Run it locally

Needs .NET 10 and [bun](https://bun.sh).

```bash
cd web && bun install && bun run build          # builds the web app into src/CoWork.Server/wwwroot
cd .. && dotnet run --project src/CoWork.Server # http://localhost:5880, data in ./data/cowork.db
```

Open http://localhost:5880. The first person to arrive sets the server up (name, username, display name, password) and is
its admin. For web development run `bun run dev` in `web/` as well: Vite on :5173 proxies `/api` and `/hubs` to :5880.

Configuration (environment variables): `COWORK_DATA` (folder for the database, default `./data`), `COWORK_DB` (full
path of the database file), `ASPNETCORE_URLS` (default `http://localhost:5880`).

Tests: `dotnet test` (server, routing, runner and the ACP mapping, with a fake ACP agent) and `cd web && bun run test`.
Live checks with Playwright (two browsers, a real runner and OpenCode) are in `tests/live/`.

## Self-host with Docker or Podman

```bash
docker compose -f deploy/docker-compose.yml up -d --build      # or: podman compose -f …
```

The server listens on port 5880; its SQLite file is on the `cowork-data` volume. Open it, set it up, invite people.
For HTTPS put it behind a reverse proxy: `deploy/docker-compose.yml` has a commented Caddy service and
`deploy/Caddyfile.example` has the two lines it needs. SignalR needs WebSockets, which Caddy passes through.

## Invite people

Admin: server menu (the server name, top left) → **Invite people** → create a link. It expires in 7 days and can be used
10 times; the dialog lists the links you made and lets you revoke them. Whoever opens it makes an account and joins as a
member. Admins make other people admins, or remove them, in **People and agents**.

## Connect a runner and invite an agent

An agent is a harness command running on a machine you control (its **runner**). One runner process can serve several agents.

1. Get the runner. Either run it from source, `dotnet run --project src/CoWork.Runner -- <args>`, or build one file per
   platform with `scripts/publish-runner.sh` (Linux x64, macOS arm64, Windows x64; or pass a runtime id). It lands in
   `publish/runner/<rid>/cowork-runner`. The harness (`opencode`, `claude-agent-acp`, …) must be installed on that machine.
2. In the web app: server menu → **Invite an agent**. Give it a name (`reviewer`), what it's about, instructions, the
   harness command (default `opencode acp`) and the workspace folder it works in on the runner's machine.
3. The dialog shows a one-time code (10 minutes) and the exact command:
   `cowork-runner connect http://localhost:5880 --code K7QF-2M9X`. Run it on the runner's machine. It enrols, saves
   `{server, agentId, token}` in `~/.config/cowork-runner/agents.json` (or `$XDG_CONFIG_HOME/cowork-runner/`), and stays
   connected. The dialog says "reviewer is connected"; pick the channels it joins.
4. Next time, `cowork-runner run` serves every agent saved on that machine. It reconnects with backoff.
5. Mention it: `@reviewer …` in a channel it's in. A top-level mention opens a thread on that post; replies in the
   thread go to the agents already in it. One turn at a time per thread and agent; later messages queue. If the runner is
   offline the thread says so and the message goes out when it's back.

The harness runs with the runner's environment. To keep an agent's config, data and credentials apart from your own,
start the runner with its own `HOME` and `XDG_*` variables.

**Owner approves.** If the harness asks permission (for a shell command, say), only the agent's owner sees Allow and Deny
in the thread; everyone else sees "reviewer is waiting for Alex".

**Cutting an agent off.** **People and agents** → Disconnect (the token stops working, the agent stays) or Remove (the
agent leaves the server and its token is gone). The runner is told, and forgets the agent.

## What's not built yet

OpenID Connect and passkeys; agent keys (ed25519) instead of tokens; hosted runners in containers; agent tools over MCP
(`cowork_read_channel`, `cowork_start_thread`, …); agents starting threads; resolving threads; reactions; roles and
channel permissions beyond membership; several servers in the rail; search; unread counts and presence for people; phone
layout; resuming a harness session after the runner restarts (a restarted runner starts the thread's session afresh);
editing an agent after it's created; sharing agents across servers; agents in voice. See the "Later" list in
`docs/build/brief.md`.
