# Build brief 7: agents on demand (pools, sandboxes, the gateway)

Read `AGENTS.md`, `docs/build/log.md` (briefs 4 to 6), and the approved design
`docs/architecture/provisioning.html`, every tab: **One request, step by step** is the flow you're building, **Who may
do what** is the authentication model, **Agent Substrate** is the shape the pool contract takes. The user approved all
of it on 25 November 2026: OpenCode first, the egress proxy from day one, a stock image per harness plus the project's
setup command, a Substrate-shaped contract with podman as the first provider. Step 4 of `mockups/cowork/projects.html`
is how the thread looks.

The work is three rounds. **Do only the round you were asked to do**, then report.

## Round 1: a sandbox per thread

**Demo:** Rosa mentions a team agent that no runner serves, and a container starts for her thread, answers from the
project's repository, stops when idle, and picks up where it left off when she replies.

1. Alex opens **Where agents run → Add a pool**, gets a one-time code and the command, and runs
   `cowork-runner pool connect <server> --code … --slots 2 --memory 1500M` on this machine. **pool-01** shows online
   with 2 slots.
2. Alex sets **Server settings → Models**: a provider for the gateway (see "The gateway" below).
3. Alex creates a **team template**, researcher: about, instructions, `opencode acp`, the model, the image
   `cowork-sandbox-opencode:2.4.1`, runs on **a pool**. He enables it in Checkout app (a repository workspace).
4. Rosa posts "@researcher …" in `#checkout-app`. The thread shows the starting rows from the mockup (pool picked,
   sandbox started, workspace ready, harness starting), then the answer streams with tool rows. It reads `src/tax.ts`
   from the project's repository, so the workspace is really there.
5. Sam asks researcher in another thread at the same moment: a second container. A third thread says "waiting for room
   on pool-01" and starts when a slot frees.
6. After the idle time (a server setting, short in the live check), the container is gone and its volume kept
   (`SandboxSuspended`). Rosa replies; the sandbox resumes on the same volume, the runner calls `session/load`, and
   researcher remembers what it read.
7. Inside the container there is no provider key, no GitHub sign-in, no pool token: the live check looks at the
   container's environment and files. The model calls went through the gateway, which recorded the thread's usage.

**What to build**

- **Templates:** an agent whose placement is `pool` instead of a pinned runner, with an image. Its owner is the person
  who made it; in a project, "asks" goes to whoever started the thread, and a lead if they don't answer (the approved
  decision 7). Events on the `agent-{id}` stream; the web's agent dialog gets "Runs on: a runner I connect / a pool".
- **Pools:** enrol like runners (`PoolEnrolled`, a token stored hashed), a `pool-{id}` stream, a hub group of their own.
  The pool token can only receive sandbox commands and report state. A **Where agents run** view lists runners and
  pools with slots and state.
- **The contract** (in `CoWork.Contracts`, Apache-2.0, kept small): `CreateSandbox`, `ResumeSandbox`,
  `SuspendSandbox`, `DeleteSandbox` and a state report (`suspended`, `resuming`, `running`, `suspending`, `failed`).
  Each create or resume carries the template's image, the thread's workspace, limits, the network rules and credential
  references as data (the shape of Substrate's `EgressPolicy`: hostnames, then `replaceHeaders` with `header`, `prefix`
  and a reference), and a fresh thread token. Round 1 may ignore the network rules; round 2 enforces them.
- **Thread tokens:** minted per create and per resume, stored hashed, bound to agent, project, thread and sandbox,
  revoked on suspend, pause, removal or delete. The runner's hub accepts one and scopes the connection to that thread:
  it receives only that thread's deliveries and may post only that thread's events. Tests for every "can't" in the
  design's table.
- **The pool** (`cowork-runner pool`, a mode of the same binary): keeps one mirror per repository and fetches it; makes
  a volume per thread with a copy of the repository on `cowork/<slug>` and a home for the harness; runs rootless podman
  with the memory limit; labels everything it makes (`cowork.pool`, `cowork.thread`) and only ever removes what carries
  its labels. The container's runner connects out with the thread token. Find how a rootless container reaches a
  server on the host's loopback here (`host.containers.internal`, pasta options), and log it.
- **The image:** `deploy/sandbox/Containerfile` from `docker.io/library/debian:trixie-slim`, the runner published
  self-contained for linux-x64, OpenCode 2.4.1 at a pinned version, git and ripgrep, a non-root user. A script builds it
  locally with podman. Decide how the build gets OpenCode 2.4.1 (a pinned download, or a read-only copy of the binary
  at `~/.weave/harnesses/opencode2/.opencode/bin/opencode`; never touch `~/.weave`'s data) and log it.
- **Routing:** a message for a template with no running sandbox in that thread asks a pool to create or resume one and
  queues the delivery until the sandbox connects; a full pool queues in order; the idle timer suspends. Events:
  `SandboxRequested`, `SandboxStarted`, `SandboxSuspended`, `SandboxResumed`, `SandboxFailed`, `SandboxDeleted`, with
  the person who asked as `on_behalf_of`. The thread panel shows the starting rows and "sandbox on pool-01".
- **The gateway:** in the server, an OpenAI-compatible endpoint (`/gateway/v1/chat/completions` and `/models`) that
  takes the thread token as the API key, checks it, forwards to the configured upstream with the server's credential,
  streams the answer back, and records usage per turn (`ModelUsageRecorded`: tokens, and cost when the upstream says).
  The sandbox's OpenCode config names the gateway as an OpenAI-compatible provider. Upstreams:
  - **OpenAI-compatible** (base URL and key: OpenRouter, OpenAI, a local model). Tested with a fake upstream.
  - **GitHub Copilot, signed in on the server's machine**, for personal and development servers. The user agreed on
    25 November 2026 that this machine's Copilot subscription is used for checks: the server reads the GitHub sign-in
    from OpenCode's `~/.local/share/opencode/auth.json` (read only, never copied into a sandbox or an event), exchanges
    it for a Copilot token as OpenCode does, and forwards to Copilot's chat endpoint. The live check uses this with
    `claude-haiku-4.5`. Label it in the UI as for a server only its owner uses.
  - Never use the OpenRouter key in `~/.bashrc`: it is the user's and costs money.

## Round 2: locked down

**Demo:** researcher tries a host that isn't on Checkout app's list; Rosa gets the ask, allows it once, and the agent
is told to try again. A project's setup command runs once per thread. A thread at its cap stops cleanly. Alex pauses
the project's agents.

- The egress proxy: sandboxes on an internal podman network whose only way out is the pool's proxy, which applies the
  project's network list (the server and the gateway are always allowed), reports refusals to the server, and opens a
  host once or for the thread when someone allows it. The ask card goes to whoever started the thread; a short system
  message in the session says it may try again.
- The project's setup command (project settings), run in the sandbox once per thread before the first prompt, its
  output kept as a tool row, its result kept in the volume.
- Budgets: per project a month and per thread, enforced by the gateway; the thread says why it stopped; a lead can
  raise it. Project settings show the Budget card from the mockup with real usage.
- **Pause all agents** in project settings: suspends every sandbox in the project and holds new deliveries.

## Round 3: threads as work

**Demo:** researcher reads the channel with a tool, saves nothing outside its boundary, and resolves its thread with
an outcome that shows on the post, in the thread and in search.

- CoWork's tools as an MCP server passed in `session/new` to every agent (pinned and pool): `cowork_read_channel`,
  `cowork_search` (search inside the agent's boundary), `cowork_resolve_thread` (a summary). Authenticated with the
  thread token in a sandbox and the runner's token on a pinned runner; every call checked against the agent's boundary.
- Thread status on the post and in the thread (starting, working, waiting for someone, done) and the outcome card
  from the mockup, with time and usage. Outcomes become search documents of kind `outcome`, grouped first.

## The rules every round keeps

- Behaviour that exists keeps working: `tests/live/phase1.mjs`, `phase2.mjs`, `phase4.mjs` to `phase8.mjs`, plus
  `dotnet test` and `bun run test`. The importer and rebuild tests cover the new events.
- Each round adds a live check (`phase9.mjs`, `phase10.mjs`, `phase11.mjs`) with two browser contexts (Alex and Rosa,
  Sam where needed), a real pool on this machine, real containers and real OpenCode 2.4.1 talking to the gateway. The
  live checks' model defaults are in `AGENTS.md` (GitHub Copilot when signed in locally).
- Screenshots of the real app in `docs/screens/brief7/`, dark and light for new screens; look at them.
- Scratch servers on ports 5883 to 5889. Containers, volumes and networks only with your labels; remove them by
  label. Never touch the user's compose project `cowork` or its volume, other containers, or `~/.weave`.
- Cap heavy commands with `systemd-run --user --scope -q -p MemoryMax=2500M -p MemorySwapMax=0 -- …`; sandboxes get
  their own podman memory limit. This machine has 14 GB and runs other agents.
- Commit on your branch in small commits with tests. Don't push and don't touch `main`: the orchestrating session
  merges. Append each round to `docs/build/log.md` (demo, what changed, decisions, checked).
- Don't ask questions: decide, log the decision and carry on. Report to the session that sent you with `fleet_message`.
