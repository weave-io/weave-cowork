// The screenshots for brief 3 (the Weave Fleet makeover), from a real server, a real runner with real OpenCode 2 and a
// real LiveKit, driven the way phase2 and phase4 drive them. Invented data only.
//   BASE=http://127.0.0.1:5884 DB=~/.cache/cowork-scratch/mk/cowork.db node tests/live/makeover.mjs
// (the server needs LIVEKIT_API_KEY / LIVEKIT_API_SECRET / LIVEKIT_URL and a LiveKit running; see ~/.cache/cowork-scratch/mk.sh)
import { launch, person, BASE, expect, ok } from './lib.mjs'
import { spawn } from 'node:child_process'
import { homedir } from 'node:os'
import { mkdirSync } from 'node:fs'

const HOME = homedir()
const OUT = process.env.SHOTS_DIR ?? new URL('../../docs/screens/makeover/', import.meta.url).pathname
mkdirSync(OUT, { recursive: true })
const OC = `${HOME}/.cache/cowork-oc2`
const REPO = new URL('../../', import.meta.url).pathname.replace(/\/$/, '')
const RUNNER = `${REPO}/src/CoWork.Runner/bin/Debug/net10.0/cowork-runner.dll`
const HARNESS = process.env.HARNESS ?? `${HOME}/.weave/harnesses/opencode2/.opencode/bin/opencode acp`
const ENV = { ...process.env, HOME: `${OC}/home`, XDG_CONFIG_HOME: `${OC}/config`, XDG_DATA_HOME: `${OC}/data`, XDG_STATE_HOME: `${OC}/state`, XDG_CACHE_HOME: `${OC}/cache` }
mkdirSync(`${OC}/home`, { recursive: true })
const LONG = 150000

let runner = null
const startRunner = (args) => { runner = spawn('dotnet', [RUNNER, ...args], { env: ENV, stdio: ['ignore', 'pipe', 'pipe'], detached: true }); runner.stdout.on('data', () => {}); runner.stderr.on('data', () => {}) }
const stopRunner = () => new Promise(res => { if (!runner || runner.exitCode !== null) return res(); runner.once('exit', res); try { process.kill(-runner.pid, 'SIGTERM') } catch { return res() } setTimeout(res, 8000) })

const browser = await launch(['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream'])
const perm = { permissions: ['microphone'] }
const A = await person(browser, { ...perm, colorScheme: 'dark' }), R = await person(browser, { ...perm, colorScheme: 'dark' })
const a = A.page, r = R.page
// menus and dialogs fade; a screenshot waits for every animation to end (a signal, not a timer)
const settle = (p) => p.evaluate(() => Promise.all(document.getAnimations().filter(x => x.effect?.getComputedTiming().iterations !== Infinity).map(x => x.finished.catch(() => {}))))
const shot = async (p, name) => { await p.mouse.move(700, 700); await p.evaluate(() => { document.activeElement?.blur?.(); getSelection()?.removeAllRanges() }); await settle(p); await p.screenshot({ path: `${OUT}/${name}.png` }) }
const idle = (p) => p.waitForFunction(() => /idle/.test(document.querySelector('[data-testid=agent-status]')?.textContent ?? ''), null, { timeout: LONG })
async function theme(p, id) { await p.click('[data-testid=user-menu]'); await p.click('[data-testid=theme-menu]'); await p.click(`[data-theme-option=${id}]`); await p.keyboard.press('Escape'); await p.keyboard.press('Escape'); await p.waitForSelector('[role=menu]', { state: 'detached' }) }
let finished = false
try {
  ;(async () => { while (!finished) { const b = a.locator('[data-testid=ask-card] button:has-text("Allow once")'); if (await b.count().catch(() => 0)) await b.first().click({ timeout: 2000 }).catch(() => {}); await new Promise(res => setTimeout(res, 1500)) } })()
  await a.goto(BASE)
  await a.fill('[name=serverName]', 'Lumen Labs'); await a.fill('[name=username]', 'alex'); await a.fill('[name=displayName]', 'Alex'); await a.fill('[name=password]', 'correct horse')
  await shot(a, 'setup-dark')
  await a.click('button[type=submit]'); await a.waitForSelector('[data-channel=general]')
  await a.click('[data-testid=create-channel]'); await a.fill('[name=name]', 'checkout-app'); await a.fill('[name=topic]', 'Cart, payment and order confirmation'); await a.click('button:has-text("Create channel")')
  await a.waitForSelector('[data-testid=channel-title]:has-text("checkout-app")')
  await a.click('[data-testid=create-channel]'); await a.click('[data-testid=kind-voice]'); await a.fill('[name=name]', 'standup'); await a.click('button:has-text("Create channel")')
  await a.waitForSelector('[data-channel=standup][data-kind=voice]')
  const code = await a.evaluate(async () => (await (await fetch('/api/invites', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json()).code)
  await r.goto(`${BASE}/invite/${code}`)
  await r.fill('[name=username]', 'rosa'); await r.fill('[name=displayName]', 'Rosa'); await r.fill('[name=password]', 'rosa-password'); await r.click('button[type=submit]')
  await r.waitForSelector('[data-channel=checkout-app]'); await r.click('[data-channel=checkout-app]')
  await a.click('[data-channel=checkout-app]')
  ok('Alex and Rosa are in #checkout-app')

  // posts
  await a.fill('[data-testid=composer]', 'Morning! The cart total is off by a cent on orders with three items.'); await a.keyboard.press('Enter')
  await r.waitForSelector('text=off by a cent')
  await r.fill('[data-testid=composer]', 'I can reproduce it. Rounding in the tax step, I think.'); await r.keyboard.press('Enter')
  await a.waitForSelector('text=I can reproduce it')

  // the agent
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-invite-agent]')
  await a.fill('[name=handle]', 'reviewer'); await a.fill('[name=about]', 'Reviews the checkout app')
  await a.fill('[name=instructions]', 'You are reviewer, a code reviewer for the Lumen Labs checkout app. Answer in at most four short sentences. Only use your read, grep and glob tools, with paths inside the workspace; never run shell commands and never change anything.')
  await a.fill('[name=harness]', HARNESS); await a.fill('[name=workspace]', REPO)
  await a.click('button:has-text("Create agent")'); await a.waitForSelector('[data-testid=runner-command]')
  await shot(a, 'invite-agent-dark')
  const parts = (await a.textContent('[data-testid=runner-command]')).trim().split(' ')
  startRunner(['connect', BASE, '--code', parts[parts.length - 1]])
  await a.waitForSelector('[data-testid=agent-connected]', { timeout: 30000 })
  await a.check('[data-pick=checkout-app]'); await a.click('[data-testid=join-channels]'); await a.keyboard.press('Escape')
  await a.waitForSelector('[data-agent=reviewer]:has-text("online")')
  ok('reviewer is online in #checkout-app')
  // reply in a thread on Alex's first post, mentioning the agent
  await a.hover('[data-post]:has-text("cart total")')
  await a.click('[data-post]:has-text("cart total") >> text=Reply in thread')
  await a.fill('[data-testid=thread-panel] [data-testid=composer]', '@reviewer can you find where the tax is rounded in this repository? Use grep and read, then name the file.'); await a.keyboard.press('Enter')
  await a.waitForSelector('[data-testid=thread-panel] [data-post]:has-text("reviewer")', { timeout: LONG })
  await idle(a)
  const tools = await a.locator('[data-testid=thread-panel] [data-testid=tool-row]').count()
  expect(tools > 0, `reviewer answered in the thread with ${tools} tool rows`)
  await r.click('[data-testid=thread-card]'); await r.waitForSelector('[data-testid=thread-panel] [data-post]:has-text("reviewer")')
  await r.fill('[data-testid=thread-panel] [data-testid=composer]', 'Thanks! That matches what I saw.'); await r.keyboard.press('Enter')
  await a.waitForSelector('[data-testid=thread-panel] [data-post]:has-text("Thanks! That matches")')
  await idle(a)
  await shot(a, 'channel-dark')
  await theme(a, 'light'); await shot(a, 'channel-light')
  await theme(a, 'nord'); await shot(a, 'theme-nord')
  await theme(a, 'dark')

  // dialogs
  await a.click('[data-testid=channel-settings]'); await a.waitForSelector('[data-testid=channel-members]'); await shot(a, 'channel-settings-dark'); await a.keyboard.press('Escape'); await a.waitForSelector('[role=dialog]', { state: 'detached' })
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-people]'); await a.waitForSelector('[data-testid=agents-list]'); await shot(a, 'people-dark'); await a.keyboard.press('Escape'); await a.waitForSelector('[role=dialog]', { state: 'detached' })

  // voice
  await r.keyboard.press('Escape')
  await a.click('[data-channel=standup]'); await a.click('[data-testid=voice-join]'); await a.waitForSelector('[data-testid=voice-tile]')
  await r.click('[data-channel=standup]'); await r.click('[data-testid=voice-join]')
  for (const p of [a, r]) await p.waitForFunction(() => document.querySelectorAll('[data-testid=voice-tile]').length === 2, null, { timeout: 30000 })
  await a.waitForSelector('[data-testid=voice-tile][data-member=rosa][data-hearing=true]', { timeout: 30000 })
  await a.waitForSelector('[data-testid=voice-tile][data-speaking=true]', { timeout: 45000 }).catch(() => {})
  await shot(a, 'voice-dark')
  ok('voice call screenshot')
  await a.click('[data-testid=voice-leave]'); await r.click('[data-testid=voice-leave]')

  // phone
  await a.click('[data-channel=checkout-app]'); await a.waitForSelector('[data-testid=feed]')
  await a.setViewportSize({ width: 390, height: 844 })
  await a.waitForSelector('[data-testid=phone-menu]')
  const sideways = await a.evaluate(() => document.documentElement.scrollWidth > innerWidth || document.body.scrollWidth > innerWidth)
  expect(!sideways, 'nothing scrolls sideways at 390 px')
  await shot(a, 'phone-dark')
  await a.click('[data-testid=phone-menu]'); await a.waitForSelector('[data-testid=phone-nav]'); await shot(a, 'phone-channels-dark'); await a.mouse.click(380, 400)
  await a.click('[data-testid=phone-members]'); await a.waitForSelector('[data-testid=member-list]'); await shot(a, 'phone-members-dark')
  console.log('MAKEOVER SCREENSHOTS DONE')
} catch (e) {
  console.log('FAILED', e.message)
  await shot(a, 'FAIL-alex').catch(() => {}); await shot(r, 'FAIL-rosa').catch(() => {})
  process.exitCode = 1
} finally {
  finished = true
  await stopRunner()
  await browser.close()
}
