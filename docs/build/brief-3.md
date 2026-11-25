# Build brief 3: a Weave Fleet makeover

Read `AGENTS.md`, `docs/build/log.md` and this brief. The prototype works and is verified (journey checks
`tests/live/phase1.mjs`, `phase2.mjs`, `phase4.mjs`). Now it should **look and feel like part of the Weave Fleet family**:
someone who uses Weave Fleet should open CoWork and feel at home. Behaviour doesn't change; only the look and feel.

## Where Weave Fleet's look lives (read these, copy from them)

Weave Fleet's current client is at
`/home/pgermishuys/source/weave-fleet-worktrees/fleet-interesting-concept-something-likely/client/` (read only; never
edit or run anything there). In it:

- `src/assets/main.css`: the theme tokens (`--rail-bg`, `--panel-bg`, `--card-bg`, `--main-bg`, `--border`, `--text`,
  `--muted`, `--accent`, `--accent-text`, `--accent-dim`, status colours, `--radius-btn` 8px, `--radius-card` 10px,
  `--radius-panel` 12px, `--transition` 150ms, `--focus-ring`) for **every theme** (dark default, light, weave-classic,
  black, nord, dracula, solarized-dark, solarized-light, monokai, github-dark) and the `@theme inline` mapping to Tailwind.
- `src/assets/weave_logo.png`: the Weave logo.
- `src/components/ui/`: button (its variants and sizes in `button/index.ts`), dialog, alert-dialog, dropdown-menu,
  tooltip, input, textarea, select, switch, tabs, avatar, badge, empty-state, error-note, spinner, page-header,
  relative-time. Built on `reka-ui`, icons from `lucide-vue-next`.
- `src/components/layout/IconRail.vue`, `RailButton.vue`, `AppShell.vue`, `ContextPanel.vue`: the rail and the shell.
- `src/components/sessions/SessionsPanel.vue`, `SessionItem.vue`, `StatusGlyph.vue`: list rows (height, padding,
  hover and picked states, muted meta, status dots).
- The conversation and composer: find them under `src/components/session/` (message rows, tool call rows, the composer).
- Fleet's `AGENTS.md` (repo root of that worktree) "Design Rules": the rules CoWork should follow too.

## What to do

1. **Tokens and themes.** Bring Fleet's tokens and all its themes into `web/` with the same variable names, the dark
   theme as default, "System" resolving to dark or light, and a theme picker in the user menu (bottom of the sidebar),
   remembered per browser. Fonts: Inter Variable and JetBrains Mono Variable, bundled (no CDN).
2. **Shared pieces.** Port the Fleet `ui/` pieces CoWork needs (button with Fleet's variants and sizes, dialog,
   alert-dialog for confirmations, dropdown-menu, tooltip, input, textarea, select, switch, avatar, badge,
   empty-state, error-note, spinner, relative-time) onto `reka-ui` and `lucide-vue-next`, and use them everywhere.
   No emoji as icons: `#` → lucide `Hash`, 🔊 → `Volume2`, the lock → `Lock`, and so on. Replace `window.confirm` with the
   alert-dialog confirm.
3. **The shell, Fleet-style.** A left **rail** like Fleet's (the Weave logo at the top, icon buttons with tooltips:
   the server, People & agents, Settings at the bottom), then the **channel list** styled like Fleet's sessions panel
   (category headers, rows with Fleet's height and states, unread weight, voice channel rows showing who's in them),
   then the **channel** and the **thread** panel styled like Fleet's conversation (message rows, author line, agent
   badge, tool call rows like Fleet's, the composer like Fleet's), then the member list. Resize gutters aren't needed.
   Status (agent online, working, offline, waiting on you) uses Fleet's status colours and glyphs.
4. **Dialogs and pages** (setup, sign in, invite sign-up, create channel, channel settings, invite people, invite an
   agent, edit agent, people, voice call) restyled with the shared pieces. Setup and sign in get a centred card with the
   Weave logo, like Fleet's welcome screen.
5. **Words:** sentence case, "…", "Try again", "machine" not "computer" (Fleet's words rules).
6. **Phone width:** at 390 px the channel list and member list collapse behind a menu button; nothing scrolls sideways.
7. **Attribution:** a line in `README.md` saying the look and the `ui/` pieces are adapted from Weave Fleet.

## Don't break anything

- Keep every `data-testid` the live checks use (or update the checks in the same commit if a change is unavoidable).
- Re-run `tests/live/phase1.mjs`, `phase2.mjs` (real runner, OpenCode 2, scratch HOME) and `phase4.mjs` (LiveKit) against
  scratch servers before committing, plus `dotnet test` and `bun run test`. Use ports 5883–5889 and the compose project
  name `cwp`; clean up containers, networks and volumes by label afterwards. Don't use the `cowork` project or its volume:
  it holds the user's own test data.
- Text keeps 4.5:1 contrast in every theme (Fleet's tokens already do; don't invent new colours).

## Screenshots (the user will look at these)

Save to `docs/screens/makeover/`, looked at by you before you finish, from real servers driven by the live scripts
(invented data only: Lumen Labs, Alex, Rosa, #checkout-app, @reviewer):

- `channel-dark.png` and `channel-light.png` (1440 px): #checkout-app with posts from Alex and Rosa, a thread open on
  the right in which reviewer (a real OpenCode 2 answer) replied with tool rows, and the member list showing reviewer
  online.
- `invite-agent-dark.png`: the invite-an-agent dialog showing the runner command.
- `voice-dark.png`: Alex and Rosa in the voice call.
- `channel-settings-dark.png`, `setup-dark.png`, `people-dark.png`.
- `theme-nord.png` (or another non-default theme) and `phone-dark.png` (390 px).

## When you're done

Commit (one commit for tokens/themes/pieces, one for screens is fine), append to `docs/build/log.md`, and report with
`fleet_message` to the session that sent you this: what changed, how you checked it, the screenshot paths. Work on your
own and don't ask questions: decide, log the decision and carry on.
