# Build brief 8: threads you can find

Read `AGENTS.md`, `docs/build/log.md` (briefs 5 to 7) and `docs/screens/tour.html`. The user tried CoWork on 25 November
2026 and asked for this: "We should be able to see the threads under the channel so that you can see the thread in a nice
full conversation view. Otherwise threads can get lost in the channel conversation history." A thread is a piece of work
(an agent's session, with a status and an outcome), so it deserves a place of its own, not only a "N replies" link under
a post.

## The demo (the acceptance test)

1. In the channel list, under `#checkout-app`, Rosa sees its threads, most recent activity first: each with a title, the
   agent in it (avatar) and its status as a dot and on hover (starting, working, waiting for you, waiting for someone,
   done). At most 5 open threads show per channel, then "All threads". Done threads drop out of the list after a day,
   unless unread. A channel's threads fold away with its chevron, remembered per channel.
2. A thread's title is its outcome title when resolved, else the first words of its opening post (without the
   `@mention`); anyone in the thread can rename it (`ThreadRenamed`).
3. Clicking a thread in the list opens it **as a full conversation in the main area**: header with the title, status,
   "in #checkout-app" (a link back to the post), the session line; the opening post, every reply, tool rows, asks and
   the outcome card; its own composer. The URL is the thread's, so it can be shared and reloaded. The side panel from a
   post's "N replies" stays as the quick look, with an "Open full view" button.
4. **All threads** (from the list, or a Threads button in the channel header) shows every thread in the channel with
   filters: open, waiting for me, done; and the title, agent, who started it, status, last activity.
5. Unread replies in a thread show on its row in the list (bold, a count), and opening the thread marks them read.
6. **A mention that can't reach the agent says so.** Mentioning an agent that isn't in the channel's project (or
   channel) gets a short reply in a new thread: "researcher isn't in Weave Fleet. A lead can add it in the project's
   settings." Today the mention is dropped without a word.
7. A sandbox started for a project with no workspace says so in its starting rows: "No repository: add one in the
   project's settings to give researcher something to work on."

## How

- Threads already exist in the event store and the thread projection (brief 4); add what the list needs (title,
  last activity, status, per-person unread) to the projections, inline where people read their own writes.
- Live: the list updates as threads start, change status and get replies, over the existing hub, and a reconnect
  catches up as today.
- Web: the shared `ui/` pieces, theme tokens, dark and light, and the phone layout (the list in the drawer; the full
  view fills the screen). Sentence case, "…".
- Tests for the title rules, ordering, the done-after-a-day rule, unread, rename permissions, the refused mention and
  who sees which threads (a private channel's threads only for its members).

## The rules every brief keeps

- Everything that exists keeps working: `tests/live/phase1.mjs`, `phase2.mjs`, `phase4.mjs` to `phase11.mjs`,
  `dotnet test`, `bun run test`. A new `tests/live/phase12.mjs` walks the demo with two browsers (Alex and Rosa) and a
  real pool and OpenCode 2.4.1 (model defaults in `AGENTS.md`).
- Screenshots of the real app in `docs/screens/brief8/`, dark and light; look at them.
- Scratch servers on ports 5883 to 5888 only. **Port 5889 is the user's trial server (`cowork-try` and
  `cowork-try-pool` user services, data in `~/.local/share/cowork-try`): never touch it, its data, its pool or its
  containers** (labelled `cowork.pool=2ae5525a-p1`). Your containers carry your own labels; remove only those.
- Cap heavy commands with `systemd-run --user --scope -q -p MemoryMax=2500M -p MemorySwapMax=0 -- …`.
- Commit on your branch in small commits with tests. Don't push and don't touch `main`. Log it in `docs/build/log.md`.
- Don't ask questions: decide, log the decision and carry on. Report to the session that sent you with `fleet_message`.
