# Build brief 9: artifacts and markdown

Read `AGENTS.md`, `docs/build/log.md` (briefs 7 and 8) and the mockup `mockups/cowork/artifacts.html`: option **A**
(canvas beside the thread), **M** (markdown) and **?** (how the agent does it). The user chose A plus full markdown on
25 November 2026; the project library (option C) comes later. Brief 8's full thread view is where the canvas sits.

## The demo (the acceptance test)

1. Rosa asks researcher (a team template in a sandbox) for "a report I can share with finance". Its answer renders
   as markdown: a heading, a list, a table, inline code, a link that opens in a new tab.
2. researcher writes `reports/tax-apis/index.html` (with a CSS file and an image beside it) in its workspace and calls
   `cowork_show`. A card appears in its message: thumbnail, title, kind, version, size.
3. Rosa, who started the thread and is looking at it, sees the **canvas** open beside the thread with the report in a
   tab. Alex, also looking, sees only the card; clicking it opens the canvas for him.
4. The canvas has tabs (one per open artifact), a version picker, full screen, copy link, download and close.
5. Rosa types "add the cost for 50,000 refunds a month" under the canvas: it's posted in the thread as her reply,
   quoting the artifact and its version. researcher edits the file and calls `cowork_show` again: **v2**, the card
   and the open tab update, v1 is still in the picker.
6. A pinned agent (Alex's reviewer on a runner) can do the same from its worktree.
7. The report can't reach CoWork: from inside it, `document.cookie`, a `fetch` to `/api/…` and a `fetch` to any
   other host all fail (the live check proves each). Sam, not in the channel, gets 404 for the artifact's address.

## How

- **The tool:** `cowork_show({ path, title, open })` on CoWork's MCP server (brief 7 round 3), described well enough that
  a harness knows when to use it. `path` is in the agent's workspace; the runner reads the file and the files beside
  it (one folder, at most 25 MB) and uploads them with its token (thread token in a sandbox, runner token pinned).
  `open: "canvas"` opens it for the thread's starter if they're looking; `"card"` only adds the card.
- **Storage:** `ArtifactPublished` (artifact id, version, thread, project, title, kind, size, SHA-256 of each file,
  who published it, on behalf of whom) on the thread's stream; the bytes in a content-addressed folder under the
  data directory, never in events. Same path in the same thread = a new version of the same artifact. Deleted with
  the channel; redaction removes the bytes and keeps the fact. In the audit.
- **Serving safely:** artifacts are served under their own origin, configurable (`COWORK_ARTIFACT_ORIGIN`, e.g.
  `https://artifacts.cowork.example`), and, when that isn't set, from `/a/…` with a strict CSP (`default-src 'self'`,
  `connect-src 'none'`, `frame-ancestors` the app) inside an iframe with `sandbox="allow-scripts"` and no
  `allow-same-origin`, so the page has an opaque origin and no cookies. Access needs a short-lived signed URL the app
  gets for people who can see the channel. Kinds: HTML, markdown (rendered), images and SVG (SVG served as an image),
  CSV and JSON as tables, PDF; anything else downloads.
- **Markdown:** GitHub-flavoured markdown in every post (people and agents): headings, lists, task lists, tables,
  links, quotes, fenced code with highlighting. Sanitised; no raw HTML in posts. Borrow Weave Fleet's renderer choices
  where they fit (`~/source/weave-fleet/client`), read only. Search keeps indexing the plain text.
- **Web:** the card and the canvas as in mockup A, with the shared `ui/` pieces, dark and light, the phone layout (the
  canvas full screen with a back button), "Open full view" for an artifact on its own page.

## The rules every brief keeps

- Everything that exists keeps working: `tests/live/phase1.mjs` to `phase12.mjs` (phase3 excepted, as before),
  `dotnet test`, `bun run test`. A new `tests/live/phase13.mjs` walks the demo with Alex, Rosa and Sam, a real pool and
  a real pinned runner, real OpenCode 2.4.1 (model defaults in `AGENTS.md`).
- Screenshots of the real app in `docs/screens/brief9/`, dark and light; look at them.
- Ports 5883 to 5888. **Port 5889 and everything `cowork-try` (services, data in `~/.local/share/cowork-try`,
  containers labelled `cowork.pool=2ae5525a-p1`) is the user's trial: never touch it.**
- Cap heavy commands with `systemd-run --user --scope -q -p MemoryMax=2500M -p MemorySwapMax=0 -- …`.
- Commit on your branch in small commits with tests. Don't push and don't touch `main`. Log it in `docs/build/log.md`.
- Don't ask questions: decide, log the decision and carry on. Report to the session that sent you with `fleet_message`.
