# Build brief 13: pages that look chosen

Read `AGENTS.md` and `docs/build/log.md` (briefs 9 to 11). The user, looking at brief 10's report in the canvas on 26 November
2026: "The one thing that doesn't really look nice … we can borrow the fleet design skill that makes web pages look a little bit
nicer and provide that as a skill to whichever harness is running." They chose, from a proposal with a mockup (version B: a
credit note's layout, the answer as one large figure, real charts, three open-licence fonts): deliver it both ways from one file,
bring Weave Fleet's design skill into CoWork, make version B's level the default for reports, have CoWork tell a page which theme
it's in, and ship a small font kit.

## The demo (the acceptance test)

1. Rosa asks researcher (a team template, OpenCode in a sandbox) for a short report. Before writing it, researcher reads the
   design skill: OpenCode lists `cowork-design` among its skills (installed in the sandbox's home) and loads it, or calls
   `cowork_design_guide`. The report it shows has a dark and a light theme, uses fonts from CoWork's kit, and avoids the generic
   look (no saturated header row, no coloured bar down the side of cards).
2. **Every harness, every runner.** CoWork's MCP instructions tell any agent to read the guide before it writes a page, and
   `cowork_design_guide()` returns the same text. A pinned runner's agent (whose home CoWork doesn't own) gets it this way.
   In a sandbox the runner installs the skill as `SKILL.md` under the harness's home, in `.claude/skills/cowork-design/`
   (Claude Code; OpenCode reads it too) and `.agents/skills/cowork-design/` (Codex; OpenCode reads it too).
3. **The font kit.** CoWork serves a few open-licence families at `/a/fonts/`, with `/a/fonts/fonts.css` declaring them, so a
   page links one stylesheet and names the family. Fonts load in the canvas's sandboxed frame (CORS allowed for font files,
   including a page's own fonts in its folder).
4. **Light and dark follow CoWork.** Rosa's CoWork is dark while her computer is light: the report shows dark. She switches
   CoWork to light and the report follows without reloading. A page opened on its own, or downloaded, still follows the
   computer. Pages keep writing standard `@media (prefers-color-scheme: dark)`; CoWork does the rest.

## How

- The skill's text lives once, in `src/CoWork.Contracts` (embedded): the runner installs it, the server's MCP tool returns it.
- The canvas adds `#scheme=dark|light` to the page's address; the server adds one small script to each page it serves that
  turns the page's `prefers-color-scheme` rules on or off to match, and again on `hashchange`.
- Fonts under `src/CoWork.Server/PageKit/`, with their licences (SIL OFL 1.1) beside them and in `THIRD-PARTY-NOTICES.md`.

## The rules every brief keeps

- Everything that exists keeps working: `dotnet test`, `bun run test`, and the live checks that cover what changes (phase13 and
  phase14 for the canvas). Add `phase16.mjs` for this demo (phase15 is taken on the sign-in branch).
- Screenshots in `docs/screens/brief12/`, dark and light; look at them.
- Ports 5883 to 5888; sandbox image `cowork-sandbox-opencode:2.4.1-b12` for the new runner. Never rebuild or retag
  `cowork-sandbox-opencode:2.4.1` or `:try`, and never touch port 5889.
- Cap heavy commands with `systemd-run --user --scope -q -p MemoryMax=2500M -p MemorySwapMax=0 -- …`.
- Small commits with tests, on this branch. Log it in `docs/build/log.md`.
