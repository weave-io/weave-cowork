# Build brief 5: projects, workspaces and an agent's boundary

Read `AGENTS.md`, `docs/build/log.md`, and the approved product model `docs/architecture/projects-agents-sandboxes.html`
(every tab; the user chose option A in all eight decisions on 25 November 2026). The screens to build are steps 1 to 3
of `mockups/cowork/projects.html`: **New project**, **Project settings** and **An agent's boundary**. Open it in a
browser. `docs/architecture/provisioning.html` is for context only: pools, sandboxes and the gateway are a later brief.

Another session is building **search** (brief 6) at the same time, from the same `main`. Expect to merge with it:
it adds a Search entry to the channel list and migration `010`. Don't build search.

## The demo (this is the acceptance test)

1. Alex opens **New project**: "Checkout app", about "Cart, payment and order confirmation", started from a
   **repository** (a git URL and a branch), with Rosa and Sam, and the channels `#checkout-app` and 🔊 `checkout-standup`.
   He lands in the project's settings.
2. The channel list shows **Checkout app** as a group with its channels, a gear for its settings, and `#general` under
   **Channels** outside any project. Rosa sees the project; a person who isn't a member doesn't.
3. Alex adds **reviewer** (his agent, on his runner started with `--host build-01`) to Checkout app. The dialog shows
   reviewer's **boundary** in sentences, computed from real data (sees, takes work from, works on, reaches, asks, spends,
   remembers, stops), and warns **"Runs unsandboxed on build-01"**.
4. Rosa and Sam each mention reviewer in `#checkout-app` at the same time. Two threads run side by side, **each in its
   own git worktree on its own branch** (`cowork/<thread-slug>`). A file reviewer writes in one thread's worktree is
   not in the other's. The thread's session line says which branch and "unsandboxed on build-01".
5. Project settings show the workspace (repository, branch, where it's cloned), people with roles (lead, member),
   agents with where they run, who they take work from and who they ask, and the rules in force.
6. If Alex sets reviewer to take work from **its owner only**, Rosa's mention gets a polite "reviewer only takes work
   from Alex" instead of a turn.

## What to build

- **Projects as events** (the event store from brief 4; read how `channel-{id}` and `agent-{id}` streams work first):
  a `project-{id}` stream with `ProjectCreated`, `ProjectUpdated`, `ProjectMemberAdded` / `ProjectMemberRemoved` (role
  `lead` or `member`), `WorkspaceAdded` / `WorkspaceRemoved`, `AgentAddedToProject` / `AgentRemovedFromProject` (with
  its grants: `takesWorkFrom` = `owner` | `members`), and a channel's `projectId` (on `ChannelCreated`, plus an event
  for moving a channel in or out of a project). Inline projections, as for channels. Migrations `007` to `009`.
- **Who sees what:** a project's members see its public channels; a channel inside a project defaults its members to
  the project's. Leads and admins edit the project. Every API and hub call checks membership (tests, as for channels).
- **Workspaces belong to the project.** Kinds: `repository` (URL + branch), `folder` (path + runner host), `none`. The
  agent's own workspace field stays as the fallback for channels outside any project (today's behaviour).
- **The runner:** `DeliverMessage` carries the thread's workspace. For a repository: one clone per (agent, repository)
  under the runner's data folder, fetched before a new thread, and `git worktree add -b cowork/<slug>` per thread. For
  a git folder: a worktree too. For a non-git folder: the folder itself. The worktree is the session's `cwd`, and
  `session/load` after a restart finds the same one. A `pre-push` hook in each worktree refuses pushing to the project
  branch (`main`). Removing a thread's worktree is a later job: keep them, and log where they live.
- **Remembers:** the harness's data for an agent in a project is kept apart from its data in other projects (a data
  folder per agent and project), without breaking its sign-in. Check what OpenCode 2.4.1 keeps where, choose, and log
  the decision.
- **Web:** New project dialog, project settings page, project groups in the channel list, the add-agent dialog with
  the boundary sentences (a component the agent profile can reuse), "unsandboxed on <host>" in the thread's session
  line. Leave out Sources, Budget, Board, Library and guests: they come with later briefs.
- **Takes work from:** routing refuses a mention from someone the grant doesn't allow, with a short reply in the
  thread, and records why (the audit should read sensibly).

## The rules every brief keeps

- Behaviour that exists keeps working: `tests/live/phase1.mjs`, `phase2.mjs`, `phase4.mjs`, `phase5.mjs` and
  `phase6.mjs` pass, plus `dotnet test` and `bun run test`. The importer and rebuild tests keep passing with the new
  events (add the project events to them).
- A new `tests/live/phase7.mjs` walks the demo with two browser contexts (Alex and Rosa, and Sam where needed), a real
  runner with `--host build-01` and real OpenCode 2.4.1 under a fresh scratch home. The script makes its own small git
  repository (a bare repo with an invented `checkout-app`: a `README.md` and `src/tax.ts`) and uses its path as the URL.
- Screenshots of the real app in `docs/screens/brief5/`, dark and light for the new screens; look at them.
- Scratch servers on ports **5883 to 5885** only (search uses 5886 to 5888). Compose project `cwp5` if you need one;
  clean up by label. Never touch the user's compose project `cowork` or its volume, or `~/.weave`.
- Cap heavy commands with `systemd-run --user --scope -q -p MemoryMax=2500M -p MemorySwapMax=0 -- …`;
  `TMPDIR` under `~/.cache`. Another session builds and runs browsers at the same time on this 14 GB machine.
- Commit on your branch in small commits with tests. Don't push. When done, rebase on `main` (search may have landed),
  run the checks again, and append an entry to `docs/build/log.md` like the earlier ones (demo, what changed,
  decisions, checked).
- Don't ask questions: decide, log the decision and carry on. Report to the session that sent you with `fleet_message`.
