// Voice channels, live: two browsers with fake microphones, a real LiveKit server.
//   BASE=http://127.0.0.1:5884 BASE_NOVOICE=http://127.0.0.1:5888 node tests/live/phase4.mjs
// BASE is a fresh CoWork server with LIVEKIT_API_KEY / LIVEKIT_API_SECRET / LIVEKIT_URL set and LiveKit running.
// BASE_NOVOICE (optional) is a fresh server without them: its voice channel must say voice isn't set up.
import { launch, person, BASE, SHOTS, expect, ok } from './lib.mjs'
const NOVOICE = process.env.BASE_NOVOICE
const browser = await launch(['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream'])
const perm = { permissions: ['microphone'] }
const A = await person(browser, perm), R = await person(browser, perm)
const a = A.page, r = R.page
const shot = (p, name) => p.screenshot({ path: SHOTS + `phase4-${name}.png` })
const tiles = (p) => p.locator('[data-testid=voice-tile]')
const roster = (p, ch = 'standup') => p.locator(`[data-voice-members=${ch}] [data-voice-member]`)
const rosterNames = async (p) => (await roster(p).evaluateAll(els => els.map(e => e.dataset.voiceMember))).sort().join(',')
async function setup(p, base) {
  await p.goto(base)
  await p.fill('[name=serverName]', 'Lumen Labs'); await p.fill('[name=username]', 'alex'); await p.fill('[name=displayName]', 'Alex'); await p.fill('[name=password]', 'correct horse')
  await p.click('button[type=submit]'); await p.waitForSelector('[data-channel=general]')
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
  expect((await a.textContent('[data-channel=standup]')).includes('🔊'), 'Alex creates 🔊 standup; the channel list shows the speaker icon')
  expect(await a.locator('[data-testid=voice-join]').count() === 1 && await a.locator('[data-testid=composer]').count() === 0, 'a voice channel shows a Join button, not a composer')
  const code = await a.evaluate(async () => (await (await fetch('/api/invites', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json()).code)
  await r.goto(`${BASE}/invite/${code}`)
  await r.fill('[name=username]', 'rosa'); await r.fill('[name=displayName]', 'Rosa'); await r.fill('[name=password]', 'rosa-password'); await r.click('button[type=submit]')
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
  // the fake microphone beeps on and off, so record the moment a tile lights instead of polling for it
  for (const p of [a, r]) await p.evaluate(() => {
    window.__spoke = {}
    const mark = () => document.querySelectorAll('[data-testid=voice-tile][data-speaking=true]').forEach(e => { window.__spoke[e.dataset.member] = true })
    new MutationObserver(mark).observe(document.body, { subtree: true, attributes: true, attributeFilter: ['data-speaking'] }); mark()
  })
  await a.waitForFunction(() => window.__spoke.rosa, null, { timeout: 45000 })
  await r.waitForFunction(() => window.__spoke.alex, null, { timeout: 45000 })
  ok("the speaking ring lights on the other person's tile (fake microphone)")
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
  await a2.goto(BASE); await a2.fill('[name=username]', 'alex'); await a2.fill('[name=password]', 'correct horse'); await a2.click('button[type=submit]')
  await a2.waitForSelector('[data-channel=standup]')

  // --- manage like a text channel: rename, private, delete ---
  await a2.click('[data-channel=standup]')
  await a2.click('[data-testid=channel-settings]')
  await a2.fill('[name=name]', 'daily'); await a2.click('button:has-text("Save")')
  await a2.waitForSelector('[data-channel=daily][data-kind=voice]')
  await r.waitForSelector('[data-channel=daily][data-kind=voice]')
  ok('Alex renames the voice channel; Rosa sees it')
  await a2.check('text=Private channel >> input'); await a2.waitForTimeout(0)
  await r.waitForSelector('[data-channel=daily]', { state: 'detached' })
  ok('Alex makes it private: it disappears for Rosa')
  await a2.click('[data-testid=delete-channel]'); await a2.click('[data-testid=confirm-delete]')
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
} finally { await browser.close() }
