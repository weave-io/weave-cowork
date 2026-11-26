// Voice channels, live: two browsers with fake microphones, a real LiveKit server.
//   BASE=http://127.0.0.1:5884 BASE_NOVOICE=http://127.0.0.1:5888 node tests/live/phase4.mjs
// BASE is a fresh CoWork server with LIVEKIT_API_KEY / LIVEKIT_API_SECRET / LIVEKIT_URL set and LiveKit running.
// BASE_NOVOICE (optional) is a fresh server without them: its voice channel must say voice isn't set up.
import { launch, person, BASE, SHOTS, expect, ok, setUp, ssoJoin, ssoSignIn } from './lib.mjs'
import { writeFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
const NOVOICE = process.env.BASE_NOVOICE
// The fake microphone: Chromium's default is a faint, intermittent beep that LiveKit's speaker detection may never pick up.
// Both browsers play a loud, continuous 440 Hz tone instead (a WAV written here), so "speaking" is deterministic.
function toneWav(path, seconds = 4, hz = 440, rate = 48000) {
  const n = rate * seconds, buf = Buffer.alloc(44 + n * 2)
  buf.write('RIFF', 0); buf.writeUInt32LE(36 + n * 2, 4); buf.write('WAVEfmt ', 8); buf.writeUInt32LE(16, 16); buf.writeUInt16LE(1, 20); buf.writeUInt16LE(1, 22)
  buf.writeUInt32LE(rate, 24); buf.writeUInt32LE(rate * 2, 28); buf.writeUInt16LE(2, 32); buf.writeUInt16LE(16, 34); buf.write('data', 36); buf.writeUInt32LE(n * 2, 40)
  for (let i = 0; i < n; i++) buf.writeInt16LE(Math.round(Math.sin(2 * Math.PI * hz * i / rate) * 0.8 * 32767), 44 + i * 2)
  writeFileSync(path, buf)
}
const TONE = `${process.env.TMPDIR ?? tmpdir()}/cowork-tone-${process.pid}.wav`
toneWav(TONE)
const browser = await launch(['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream', `--use-file-for-fake-audio-capture=${TONE}`])
const perm = { permissions: ['microphone'] }
const A = await person(browser, perm), R = await person(browser, perm)
const a = A.page, r = R.page
const shot = (p, name) => p.screenshot({ path: SHOTS + `phase4-${name}.png` })
const tiles = (p) => p.locator('[data-testid=voice-tile]')
const roster = (p, ch = 'standup') => p.locator(`[data-voice-members=${ch}] [data-voice-member]`)
const rosterNames = async (p) => (await roster(p).evaluateAll(els => els.map(e => e.dataset.voiceMember))).sort().join(',')
async function setup(p, base) {
  await p.goto(base)
  await setUp(p)
  await p.waitForSelector('[data-channel=general]')
}
async function createVoice(p, name) {
  await p.click('[data-testid=create-channel]')
  await p.click('[data-testid=kind-voice]'); await p.fill('[name=name]', name); await p.click('button:has-text("Create channel")')
  await p.waitForSelector(`[data-channel=${name}][data-kind=voice]`)
}
try {
  // --- Alex sets up and creates 🔊 standup ---
  await setup(a, BASE)
  await createVoice(a, 'standup')
  expect(await a.locator('[data-channel=standup][data-kind=voice] svg.lucide-volume-2').count() === 1, 'Alex creates a voice channel standup; the channel list shows the speaker icon')
  expect(await a.locator('[data-testid=voice-join]').count() === 1 && await a.locator('[data-testid=composer]').count() === 0, 'a voice channel shows a Join button, not a composer')
  const code = await a.evaluate(async () => (await (await fetch('/api/invites', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json()).code)
  await r.goto(`${BASE}/invite/${code}`)
  await ssoJoin(r, 'rosa', 'Rosa')
  await r.waitForSelector('[data-channel=standup]')
  ok('Rosa joins the server and sees standup')

  // --- Alex joins; Rosa (still in #general) sees him in the list ---
  await a.click('[data-testid=voice-join]')
  await a.waitForSelector('[data-testid=voice-tile]')
  await tiles(a).first().waitFor()
  expect(await tiles(a).count() === 1, 'Alex is in the call: one tile (his own)')
  await r.waitForSelector('[data-voice-members=standup] [data-voice-member=alex]')
  expect(await rosterNames(r) === 'alex', 'Rosa sees "Alex" under standup in the channel list without joining')

  // --- Rosa joins: tiles both ways, audio subscribed, ring lights ---
  await r.click('[data-channel=standup]'); await r.click('[data-testid=voice-join]')
  await a.waitForFunction(() => document.querySelectorAll('[data-testid=voice-tile]').length === 2, null, { timeout: 30000 })
  await r.waitForFunction(() => document.querySelectorAll('[data-testid=voice-tile]').length === 2, null, { timeout: 30000 })
  for (const p of [a, r]) await p.waitForFunction(() => document.querySelectorAll('[data-voice-members=standup] [data-voice-member]').length === 2)
  expect(await rosterNames(a) === 'alex,rosa' && await rosterNames(r) === 'alex,rosa', 'both channel lists show Alex and Rosa in standup, live')
  await a.waitForSelector('[data-testid=voice-tile][data-member=rosa][data-hearing=true]', { timeout: 30000 })
  await r.waitForSelector('[data-testid=voice-tile][data-member=alex][data-hearing=true]', { timeout: 30000 })
  ok("each browser is subscribed to the other's audio track")
  // 1. our side: the audio that reaches this tab is a real signal (RMS of the subscribed track, measured with Web Audio),
  //    which doesn't depend on LiveKit's speaker-detection threshold
  const level = (p) => p.evaluate(async () => {
    const el = document.querySelector('body > audio'); if (!el?.srcObject) return 0
    const ac = new AudioContext(); await ac.resume()
    const an = ac.createAnalyser(); an.fftSize = 2048; ac.createMediaStreamSource(el.srcObject).connect(an)
    const data = new Float32Array(an.fftSize); let best = 0
    for (let i = 0; i < 20; i++) { await new Promise(r => setTimeout(r, 100)); an.getFloatTimeDomainData(data); best = Math.max(best, Math.sqrt(data.reduce((a, v) => a + v * v, 0) / data.length)) }
    await ac.close(); return best
  })
  const [la, lr] = [await level(a), await level(r)]
  expect(la > 0.01 && lr > 0.01, `each tab receives the other's audio as a real signal (RMS ${la.toFixed(3)} and ${lr.toFixed(3)})`)
  // 2. the speaking ring on the tile (LiveKit's active-speaker detection, which the tone is loud and steady enough to trip)
  for (const p of [a, r]) await p.evaluate(() => {
    window.__spoke = {}
    const mark = () => document.querySelectorAll('[data-testid=voice-tile][data-speaking=true]').forEach(e => { window.__spoke[e.dataset.member] = true })
    new MutationObserver(mark).observe(document.body, { subtree: true, attributes: true, attributeFilter: ['data-speaking'] }); mark()
  })
  await a.waitForFunction(() => window.__spoke.rosa, null, { timeout: 30000 })
  await r.waitForFunction(() => window.__spoke.alex, null, { timeout: 30000 })
  ok("the speaking ring lights on the other person's tile (a steady tone as the microphone)")
  await shot(a, 'call-alex'); await shot(r, 'call-rosa')

  // --- mute ---
  await a.click('[data-testid=voice-mute]')
  await a.waitForSelector('[data-testid=voice-tile][data-member=alex][data-muted=true]')
  await r.waitForSelector('[data-testid=voice-tile][data-member=alex][data-muted=true]')
  expect((await a.textContent('[data-testid=voice-mute]')).trim() === 'Unmute', 'Alex mutes: his tile shows muted for both, the button says Unmute')
  await shot(r, 'muted')
  await a.click('[data-testid=voice-mute]')
  await r.waitForSelector('[data-testid=voice-tile][data-member=alex][data-muted=false]')
  ok('Alex unmutes')

  // --- still in the call while looking at another channel ---
  await a.click('[data-channel=general]')
  await a.waitForSelector('[data-testid=voice-bar]')
  expect(await tiles(r).count() === 2, 'Alex browses #general and stays in the call (bar at the bottom of the sidebar)')
  await a.click('[data-channel=standup]')

  // --- leave ---
  await a.click('[data-testid=voice-leave]')
  await a.waitForSelector('[data-testid=voice-join]')
  await r.waitForFunction(() => document.querySelectorAll('[data-testid=voice-tile]').length === 1, null, { timeout: 30000 })
  await r.waitForFunction(() => document.querySelectorAll('[data-voice-members=standup] [data-voice-member]').length === 1)
  await a.waitForFunction(() => document.querySelectorAll('[data-voice-members=standup] [data-voice-member]').length === 1)
  expect(await rosterNames(r) === 'rosa' && await rosterNames(a) === 'rosa', 'Alex leaves: Rosa has one tile, both lists show only Rosa')

  // --- closing a tab leaves too ---
  await a.click('[data-testid=voice-join]'); await r.waitForFunction(() => document.querySelectorAll('[data-testid=voice-tile]').length === 2, null, { timeout: 30000 })
  await A.ctx.close()
  await r.waitForFunction(() => document.querySelectorAll('[data-voice-members=standup] [data-voice-member]').length === 1, null, { timeout: 30000 })
  ok('Alex closes his tab: the server drops him from the list')
  const A2 = await person(browser, perm); const a2 = A2.page
  await a2.goto(BASE); await ssoSignIn(a2, 'alex')
  await a2.waitForSelector('[data-channel=standup]')

  // --- manage like a text channel: rename, private, delete ---
  await a2.click('[data-channel=standup]')
  await a2.click('[data-testid=channel-settings]')
  await a2.fill('[name=name]', 'daily'); await a2.click('button:has-text("Save")')
  await a2.waitForSelector('[data-channel=daily][data-kind=voice]')
  await r.waitForSelector('[data-channel=daily][data-kind=voice]')
  ok('Alex renames the voice channel; Rosa sees it')
  await a2.click('[data-testid=private-switch]')
  await r.waitForSelector('[data-channel=daily]', { state: 'detached' })
  ok('Alex makes it private: it disappears for Rosa')
  await a2.click('[data-testid=delete-channel]'); await a2.click('[data-testid=confirm-action]')
  await a2.waitForSelector('[data-channel=daily]', { state: 'detached' })
  ok('Alex deletes it')

  // --- voice not configured ---
  if (NOVOICE) {
    const N = await person(browser, perm); const n = N.page
    await setup(n, NOVOICE); await createVoice(n, 'standup')
    await n.waitForSelector('[data-testid=voice-not-set-up]')
    const msg = await n.textContent('[data-testid=voice-not-set-up]')
    expect(msg.includes("Voice isn't set up on this server") && msg.includes('LIVEKIT_API_KEY') && await n.locator('[data-testid=voice-join]').count() === 0, 'with voice not configured the channel says so, with one line on how')
    await shot(n, 'not-set-up')
    await n.click('[data-channel=general]'); await n.fill('[data-testid=composer]', 'the rest of the app still works'); await n.keyboard.press('Enter')
    await n.waitForSelector('[data-testid=feed] >> text=the rest of the app still works')
    ok('the rest of the app is unaffected')
  }
  console.log('PHASE 4 LIVE CHECK PASSED')
} catch (e) {
  console.log('FAILED', e.message)
  await shot(a, 'FAIL-alex').catch(() => {}); await shot(r, 'FAIL-rosa').catch(() => {})
  process.exitCode = 1
} finally { await browser.close(); rmSync(TONE, { force: true }) }
