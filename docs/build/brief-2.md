# Build brief 2: polish, then voice channels

Read `AGENTS.md`, `docs/build/brief.md` (the user journey is still the acceptance test) and `docs/build/log.md` first.
Phases 1–3 work: they passed `tests/live/phase1.mjs` and `tests/live/phase2.mjs` against a container built from a
clean clone. **Nothing in this brief may break that journey.** Re-run both live checks before every commit.

Use ports 5883–5889 for your scratch servers and the compose project name `cwp` (`podman compose -p cwp …`), so you
never collide with another check on 5880. Remove your containers, networks and volumes by that label when done.

## Part A: polish (do first, commit on its own)

Found by an independent check of the prototype:

1. "Invite an agent", step with the command: the sentence reads "On the machine where should work, run this." It's
   missing the agent's name ("where reviewer should work").
2. The same dialog's waiting line wraps badly: the `cowork-runner run` chip splits the sentence across columns. Make it
   one readable paragraph under the command.
3. The web app falls back to the system font. Bundle Inter with the app (`@fontsource-variable/inter`; no CDN, it's
   self-hosted) and JetBrains Mono for code, as in the mockup.
4. The thread's session line shows the harness command's full path, which wraps. Show a short form (the executable's
   name plus its arguments, e.g. `opencode acp`) with the full command in a tooltip.
5. A restarted runner starts every thread's session afresh. When the harness says `loadSession: true` (OpenCode 2
   does), keep each thread's harness session id in the runner's saved state and `session/load` it after a restart,
   falling back to a new session if loading fails. Test it with the fake agent, and once live.
6. An agent can't be changed after it's created. Let its owner or an admin edit its about, instructions, harness
   command and workspace (People & agents, or the agent's row in the member list). Changes apply to new threads.

## Part B: voice channels (the user asked for them)

**Demo:** Alex creates 🔊 Standup (voice channels are created, renamed, made private and deleted like text channels).
Alex and Rosa join it from two browsers; each one's tile shows the other, the speaking ring lights when one talks, and
the channel list shows who's in it, live. Mute and leave work. With voice not configured on the server, a voice channel
says "Voice isn't set up on this server" with one line on how, and the rest of the app is unaffected.

- LiveKit server (`docker.io/livekit/livekit-server`, pin a version) as a second service in
  `deploy/docker-compose.yml`, with its config: API key and secret from env (`LIVEKIT_API_KEY`, `LIVEKIT_API_SECRET`),
  the signalling port, and one UDP port for media (`rtc.udp_port`) so self-hosters open few ports. Document the ports
  and the "behind a reverse proxy" case in the README.
- The CoWork server mints a room token per join (an HS256 JWT with LiveKit's video grant: room = the channel's id,
  identity = the member's id, name = display name), only for members of that channel. It also tells the web app the
  LiveKit URL. No LiveKit SDK is needed on the server; write the JWT yourself and test it.
- Web: `livekit-client`. Tiles for people in the room, a speaking ring, mute, leave. Who's in which voice channel comes
  from the CoWork server (join and leave reported by the web app, pushed over SignalR), so the channel list shows it
  without joining.
- Check it live with Playwright's fake media devices (`--use-fake-ui-for-media-stream`,
  `--use-fake-device-for-media-stream`): both browsers connected, each subscribed to the other's audio track, the
  channel list showing both. Add it as `tests/live/phase4.mjs`.
- Agents in voice (transcript, raise a hand, text-to-speech) stay on the Later list.
- **Time box:** if voice isn't working live within about 3 hours, take it out cleanly (no half-built voice in the UI),
  log what you learnt, and stop.

## When you're done

Run `tests/live/phase1.mjs`, `phase2.mjs` and (if voice is in) `phase4.mjs` against one container built with compose
from a clean clone of `main`. Then update `README.md` and `docs/build/log.md`, commit, and report with `fleet_message`
to the session that sent you this: what works, how you checked it, what's left. Keep it short. Don't wait for anyone;
decide, log the decision and carry on.
