# Build brief 6: search

Read `AGENTS.md`, `docs/build/log.md` (especially brief 4 round 3: async projections, checkpoints and rebuild), the
"Sources and search" tab of `docs/architecture/projects-agents-sandboxes.html`, and step 6 of
`mockups/cowork/projects.html` (open it in a browser).

Another session is building **projects** (brief 5) at the same time, from the same `main`. It restructures the channel
list into project groups and uses migrations `007` to `009`. Keep your change to the channel list small (one Search
entry under Mentions) so the merge is easy. Don't build projects.

## The demo (this is the acceptance test)

1. Rosa opens **Search** (the entry under Mentions, or `Ctrl+K` / `⌘K`) and types `refund rounding`.
2. Results arrive as she types, grouped: **Threads** first (a thread's opening post, with its reply count and the
   agents in it), then **Messages** (posts and thread replies, including agents' answers). Matching words are
   highlighted, and `rounds` finds `rounding` (stemming).
3. Filters typed into the box narrow it: `in:#checkout-app`, `from:@reviewer`, `from:@rosa`, `is:thread`. The chips
   under the box insert them.
4. Clicking a result opens the channel at that post, or the thread with that reply in view.
5. Results only include what Rosa may see: a post in a private channel she isn't in never appears, nor do deleted or
   redacted messages (a redacted message is found by nothing it used to say).
6. An answer reviewer gave (a real runner and real OpenCode) is found by a word in it.

## What to build

- **An async projection** (like `UnreadProjection`): `SearchProjection` keeps an SQLite **FTS5** table (tokenizer
  `porter unicode61`) of searchable documents with a `kind` column (`message` now; `outcome` and `library` will join
  when threads get outcomes and projects get a library, so design for them). Feed it from `MessagePosted` and
  `AgentReplied` (one document per answer, not per segment: append segments as they come), and remove or replace on
  `MessageDeleted`, `MessageRedacted` and `ChannelDeleted`. Checkpointed, part of the admin Rebuild list, rebuilt from
  zero to the same rows (add it to the rebuild tests). Migration `010`.
- **The query:** `GET /api/search?q=…` parses the filters out of `q`, runs the FTS match with `bm25` ranking and
  `snippet()` for highlights, and filters by the channels the caller may see **in SQL**, not after (so counts and
  paging are right). Threads are grouped from their messages. Brings the projection to the head first, like
  `/api/unread`, so you find what you just wrote. Tests: visibility, private channels, redaction, deletion, stemming,
  each filter, the rebuild.
- **Web:** the Search view in the main area as in the mockup (box, filter chips, grouped results, empty and no-results
  states), `Ctrl+K` / `⌘K`, keyboard up and down plus Enter through results, opening a result at the right post.
  Leave out "Ask an agent instead" (it needs CoWork's MCP tools) and `project:` (brief 5 adds projects: if it has
  landed on `main` before you finish, add `project:` too, else log it as next).

## The rules every brief keeps

- Behaviour that exists keeps working: `tests/live/phase1.mjs`, `phase2.mjs`, `phase4.mjs`, `phase5.mjs` and
  `phase6.mjs` pass, plus `dotnet test` and `bun run test`.
- A new `tests/live/phase8.mjs` walks the demo with two browser contexts (Alex and Rosa), a private channel Rosa isn't
  in, and a real runner with real OpenCode 2.4.1 under a fresh scratch home. Invented content only (Lumen Labs,
  #checkout-app, refunds and tax rounding).
- Screenshots of the real app in `docs/screens/brief6/`, dark and light; look at them.
- Scratch servers on ports **5886 to 5888** only (projects uses 5883 to 5885). Compose project `cwp6` if you need one;
  clean up by label. Never touch the user's compose project `cowork` or its volume, or `~/.weave`.
- Cap heavy commands with `systemd-run --user --scope -q -p MemoryMax=2500M -p MemorySwapMax=0 -- …`;
  `TMPDIR` under `~/.cache`. Another session builds and runs browsers at the same time on this 14 GB machine.
- Commit on your branch in small commits with tests. Don't push. When done, rebase on `main` (projects may have
  landed), run the checks again, and append an entry to `docs/build/log.md` like the earlier ones.
- Don't ask questions: decide, log the decision and carry on. Report to the session that sent you with `fleet_message`.
