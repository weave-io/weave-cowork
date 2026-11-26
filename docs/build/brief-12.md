# Build brief 12: a showcase and a README

(Written as brief 11 alongside the sign-in brief; renumbered 12 when both reached main. Sign-in kept 11.)

Read `AGENTS.md`, `README.md`, `docs/build/log.md` (briefs 5 to 10) and `docs/screens/tour.html`. The user, 26 November
2026: spruce up the repository with screenshots, and make a page for the website (`cowork.tryweave.io`, the way Weave
Fleet has `fleet.tryweave.io`). Show what this is and what it's built on, and **mark it clearly as experimental: something
we're curious about exploring, a collaborative environment for people and agents.** The screenshots come from a scratch
CoWork with agents and people participating and sample projects. This brief makes the screenshots and the README; a later
brief in the website's own repository uses the screenshots for the page.

The repository is public (`weave-io/weave-cowork`). Everything here is seen by anyone: invented data only (Lumen Labs,
Alex, Rosa, Sam, Priya, #checkout-app), no machine names, no paths from this machine, no real people or companies.

## 1. A showcase you can run again

`tests/live/showcase.mjs` builds a scratch CoWork from nothing and photographs it. It's a script anyone can run again
when the app changes (document how in the README's contributing notes and at the top of the script).

- **The server and its people:** Lumen Labs, set up by Alex (admin). Rosa, Sam and Priya join with invite links. Their
  avatars and display names are real-looking and invented.
- **Projects:**
  - **Checkout app**, from a small invented repository the script makes: a cart, VAT per line with the one-cent bug,
    refunds. Channels `#checkout-app`, `#checkout-alerts` and 🔊 `checkout-standup`.
  - **Research**, with no repository: `#research`.
  - `#general` outside any project.
- **Agents:**
  - **researcher**, a team template on a real pool, in real sandboxes.
  - **reviewer**, Alex's own agent on a real runner started with `--host build-01`.
  - Both run real OpenCode 2.4.1 (model defaults in `AGENTS.md`).
- **Real conversations, so the screens are honest.** People post in channels and talk to each other, and agents do real
  work in threads:
  - researcher compares three invented tax APIs and shows an HTML report with `cowork_show`, then makes v2 after Rosa
    asks.
  - reviewer explains `roundTax` with tool rows.
  - A sandbox asks to reach a host and Rosa allows it once.
  - A thread is resolved with an outcome.
  - A few threads are left working, waiting and done, so the channel list shows all three.
- **The screens** (desktop 1600×1000 at device scale 2, PNG, **dark and light** of each; crop nothing important):
  1. **Hero:** a thread in the full view with the canvas showing researcher's report, threads under their channels,
     people and agents in the member list.
  2. **A channel** where people and agents talk, with its threads underneath and their status dots.
  3. **An agent at work:** the sandbox starting rows, then streaming text and tool rows.
  4. **An ask:** the network ask card for Rosa, and "waiting for Rosa" as Alex sees it (one shot or a pair).
  5. **Adding an agent:** the boundary sentences.
  6. **Project settings:** the workspace, agents, network list and budget.
  7. **Search:** outcomes first.
  8. **Voice:** a room with three people (LiveKit with fake media devices, as `phase4.mjs` does).
  9. **The audit**, in sentences.
  10. **A phone:** a thread on a phone.
- Write them to `docs/screens/showcase/` with clear names (`01-hero-dark.png`, …). Keep each PNG reasonable (under about
  700 KB; optimise if needed with a tool you can install without root, and log which). Also write a 1200×630 social
  card (`docs/screens/showcase/social.png`) from the hero, with the name and "Experimental" on it.
- Look at every screenshot. Fix what looks wrong in the app if it's small and clearly a bug, and log it; otherwise
  retake with better content.

## 2. The README

Rewrite the top of `README.md` for someone who lands on the repository:

- **The name and one line:** "A self-hosted place where people and AI agents work together: channels, threads, projects,
  and agents that run in sandboxes." Wording is yours, plain and short.
- **A clear experimental notice right under it.** For example: "Experimental. Weave CoWork is something we're curious
  about exploring, not a product: expect rough edges and breaking changes, and don't run it with anything you can't
  lose." Use the same words on the website later.
- **The hero screenshot,** with dark and light following the reader's GitHub theme (`<picture>` with
  `prefers-color-scheme`).
- **What it is,** in a few short paragraphs:
  - Members are people and agents.
  - A thread is an agent's session.
  - Projects hold the work.
  - Agents are either brought by their owners or started on demand in sandboxes, with an owner who approves.
  - Any harness that speaks ACP.
- **What you can do today:** a short list, each with a screenshot (a grid of two per row is fine).
- **How it fits together:** the server, runners, pools and sandboxes, the gateway. A small diagram as a mermaid block or
  an image you make.
- **Quick start,** run locally and with Docker or Podman, then the existing detailed sections (keep them, tidy them).
- **What's not built yet:** bring this up to date (it still lists search and unread as missing).
- **Built with:** .NET 10, Vue 3, SQLite (events), ACP harnesses (OpenCode, Claude Code, Copilot), LiveKit, podman.
  Licence, contributing.
- Keep the voice of the rest of the repo: sentence case, short sentences, "…".

## The rules every brief keeps

- Everything that exists keeps working (no app changes expected beyond small fixes). If you change app code, run
  `dotnet test`, `bun run test` and the live checks that cover it.
- Ports 5883 to 5888; sandbox image from `COWORK_SANDBOX_IMAGE` (default `cowork-sandbox-opencode:2.4.1-b9`). **Port
  5889, everything `cowork-try`, and the image tags `cowork-sandbox-opencode:2.4.1` and `:try` are the user's trial:
  never touch them.**
- Cap heavy commands with `systemd-run --user --scope -q -p MemoryMax=2500M -p MemorySwapMax=0 -- …`. Clean up your
  containers by your own label.
- Commit on your branch in small commits. Don't push and don't touch `main`. Log it in `docs/build/log.md`.
- Don't ask questions: decide, log the decision and carry on. Report to the session that sent you with
  `fleet_message`, listing the screenshots.
