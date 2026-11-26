# Build brief 10: panels you can size

Read `AGENTS.md` and `docs/build/log.md` (briefs 8 and 9). The user, using brief 9 on 26 November 2026: "the ability to
drag resize the panel on the right hand side where the HTML page is being shown." In their screenshot the canvas took a
fixed share of a wide screen, and the report they wanted to read was squeezed while the thread had space to spare.

## The demo (the acceptance test)

1. Rosa drags the left edge of the **canvas** to make it wider or narrower. The thread and the canvas resize live while
   she drags; the report inside reflows. The handle shows on hover (a thin line and a resize cursor), and the iframe
   doesn't swallow the drag when the pointer crosses it.
2. Limits: the canvas never goes below about 320 px, and the thread never below about 360 px. Double-clicking the
   handle puts the canvas back to its default width.
3. The width is remembered per browser for the canvas (not per artifact) and survives a reload. Opening the canvas on
   a narrower window clamps it.
4. The same handle on the **thread's side panel** (the quick look from "N replies") and on the **member list**, each
   with its own remembered width.
5. Keyboard: the handle is focusable (a `separator` with `aria-valuenow`/`min`/`max`); arrow keys move it by 16 px,
   Shift+arrow by 64, Home and End to the limits.
6. On a phone nothing changes: the canvas is full screen, no handle.
7. **Showing a file from outside the workspace.** When an agent calls `cowork_show` on a path outside its workspace
   (in the user's run it was `/tmp/opencode/…`), the tool's answer says exactly what to do: "Write the file inside your
   workspace (`<cwd>`), for example `<cwd>/reports/<name>`, then call cowork_show with that path." And the tool's
   description says up front that the file must be in the workspace. Check with a runner test, and in the live check
   that an agent asked for a report shows it on the first try.

## How

- One shared resize handle component in `web/src/components/ui/`, using pointer capture; while dragging, an overlay
  covers iframes so they don't steal pointer events. Widths as CSS variables on the layout grid. Persist with
  `localStorage` (the app runs as a normal page, so storage is available), keyed per panel.
- Theme tokens, dark and light, sentence case. Unit tests for clamping and keyboard steps.

## The rules every brief keeps

- Everything that exists keeps working: `tests/live/phase1.mjs` to `phase13.mjs` (phase3 excepted), `dotnet test`,
  `bun run test`. Extend `phase13.mjs` (or add `phase14.mjs`) to drag the canvas wider, reload, find the width kept,
  double-click to reset, and use the keyboard.
- Screenshots in `docs/screens/brief10/`, dark and light; look at them.
- Ports 5883 to 5888; sandbox image tag from `COWORK_SANDBOX_IMAGE` (default `cowork-sandbox-opencode:2.4.1-b9`).
  **Port 5889 and everything `cowork-try` is the user's trial: never touch it, and never rebuild or retag
  `cowork-sandbox-opencode:2.4.1` or `:try`.**
- Cap heavy commands with `systemd-run --user --scope -q -p MemoryMax=2500M -p MemorySwapMax=0 -- …`.
- Commit on your branch in small commits with tests. Don't push and don't touch `main`. Log it in `docs/build/log.md`.
- Don't ask questions: decide, log the decision and carry on. Report to the session that sent you with `fleet_message`.
