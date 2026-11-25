// Round 3 of brief 4: the first new views. Two browsers (Alex, the admin, and Rosa) and a REAL runner with a REAL harness (OpenCode 2 under a
// scratch HOME). Checks, in the app: unread badges and the Mentions inbox for Rosa; the Audit for Alex telling "Rosa asked reviewer (Alex's agent)
// to run `git log -3`. Alex allowed it once. It ran on build-01." from the causation chain, with its filters; agent activity on reviewer's
// profile; a rebuild of each view from zero that changes nothing. Starts its own server (default port 5887).
//   node tests/live/phase6.mjs          (screenshots go to docs/screens/round3/)
import { launch, person, expect, ok, freshHarnessHome } from './lib.mjs'
import { spawn, execFileSync } from 'node:child_process'
import { homedir } from 'node:os'
import { rmSync, mkdirSync } from 'node:fs'

const HOME = homedir()
const PORT = process.env.PORT ?? '5887'
const BASE = `http://127.0.0.1:${PORT}`
const REPO = new URL('../../', import.meta.url).pathname.replace(/\/$/, '')
const DIR = `${HOME}/.cache/cowork-p6`
const DB = `${DIR}/data/cowork.db`
const SCRATCH = freshHarnessHome('p6')        // this run's own runner and harness state, deleted at the end
const SHOTS = `${REPO}/docs/screens/round3/`
const HOST = 'build-01'                      // the name the runner reports: a made-up one, so no screenshot or doc shows this machine's own
const SERVER = `${REPO}/src/CoWork.Server/bin/Debug/net10.0/CoWork.Server.dll`
const RUNNER = `${REPO}/src/CoWork.Runner/bin/Debug/net10.0/cowork-runner.dll`
const HARNESS = process.env.HARNESS ?? `${HOME}/.weave/harnesses/opencode2/.opencode/bin/opencode acp`
const ENV = SCRATCH.env
const LONG = 150000
rmSync(DIR, { recursive: true, force: true }); mkdirSync(`${DIR}/data`, { recursive: true }); mkdirSync(SHOTS, { recursive: true })
const sql = (q) => JSON.parse(execFileSync('python3', ['-I', '-c', 'import sqlite3,sys,json;c=sqlite3.connect(sys.argv[1]);print(json.dumps(c.execute(sys.argv[2]).fetchall()))', DB, q]).toString())
const until = async (what, probe, ms = 40000) => { const end = Date.now() + ms; for (;;) { const v = await probe(); if (v) return v; if (Date.now() > end) throw new Error('FAILED: timed out waiting for ' + what); await new Promise(r => setTimeout(r, 100)) } }
const clean = (t) => t.replace(/\s+/g, ' ').trim()

let server = null, runner = null
function startServer() {
  server = spawn('dotnet', [SERVER], { cwd: `${REPO}/src/CoWork.Server`, env: { ...process.env, COWORK_DB: DB, ASPNETCORE_URLS: BASE }, stdio: ['ignore', 'pipe', 'pipe'], detached: true })
  server.log = ''; server.stdout.on('data', d => server.log += d); server.stderr.on('data', d => server.log += d)
  return until('the server to listen', async () => { try { return (await fetch(BASE + '/api/setup/status')).ok } catch { return false } }, 30000)
}
const stopGroup = (p) => new Promise(res => { if (!p || p.exitCode !== null) return res(); p.once('exit', res); try { process.kill(-p.pid, 'SIGTERM') } catch { return res() } setTimeout(res, 8000) })
function startRunner(args) {
  runner = spawn('dotnet', [RUNNER, ...args], { env: ENV, stdio: ['ignore', 'pipe', 'pipe'], detached: true })
  runner.stdout.on('data', d => process.stdout.write('  [runner] ' + d)); runner.stderr.on('data', d => process.stdout.write('  [runner!] ' + d))
}

const browser = await launch()
const A = await person(browser), R = await person(browser)
const a = A.page, r = R.page
// a dialog fades and scales in: take the picture once it has settled (a look at the screen, not an assertion)
const shot = async (p, name) => { await p.waitForTimeout(500); await p.screenshot({ path: SHOTS + name + '.png' }) }
const json = (p, path) => p.evaluate(async (u) => (await fetch(u)).json(), path)
try {
  await startServer()

  // --- people, a channel and the agent ---
  await a.goto(BASE)
  await a.fill('[name=serverName]', 'Lumen Labs'); await a.fill('[name=username]', 'alex'); await a.fill('[name=displayName]', 'Alex'); await a.fill('[name=password]', 'correct horse')
  await a.click('button[type=submit]'); await a.waitForSelector('[data-channel=general]')
  await a.click('[data-testid=create-channel]'); await a.fill('[name=name]', 'checkout-app'); await a.fill('[name=topic]', 'Cart, payment and order confirmation'); await a.click('button:has-text("Create channel")')
  await a.waitForSelector('[data-testid=channel-title]:has-text("checkout-app")')
  const invite = await a.evaluate(async () => (await (await fetch('/api/invites', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json()).code)
  await r.goto(`${BASE}/invite/${invite}`)
  await r.fill('[name=username]', 'rosa'); await r.fill('[name=displayName]', 'Rosa'); await r.fill('[name=password]', 'rosa-password'); await r.click('button[type=submit]')
  await r.waitForSelector('[data-channel=checkout-app]')
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-invite-agent]')
  await a.fill('[name=handle]', 'reviewer'); await a.fill('[name=about]', 'Reviews the checkout app')
  await a.fill('[name=instructions]', 'You are reviewer, a code reviewer for the Lumen Labs checkout app. Answer in at most three short sentences. Never change any file. When a person asks you to run a shell command, run it with your shell tool: the server asks your owner to allow it first, so it is safe to try.')
  await a.fill('[name=harness]', HARNESS); await a.fill('[name=workspace]', REPO)
  await a.click('button:has-text("Create agent")')
  await a.waitForSelector('[data-testid=runner-command]')
  const parts = (await a.textContent('[data-testid=runner-command]')).trim().split(' ')
  startRunner(['connect', BASE, '--code', parts[parts.length - 1], '--host', HOST])
  await a.waitForSelector('[data-testid=agent-connected]', { timeout: 30000 })
  await a.check('[data-pick=checkout-app]'); await a.click('[data-testid=join-channels]'); await a.keyboard.press('Escape')
  await a.waitForSelector('[data-agent=reviewer]:has-text("online")')
  ok(`Alex (admin) and Rosa are in; reviewer's real runner is online, reporting its host as ${HOST}`)

  // --- unread badges and the Mentions inbox: Rosa is in #general while Alex writes in #checkout-app ---
  await r.click('[data-channel=general]'); await r.waitForSelector('[data-testid=channel-title]:has-text("general")')
  expect(await r.locator('[data-testid=unread-badge], [data-testid=mentions-badge]').count() === 0, 'Rosa starts with nothing unread (what was written before she joined is not unread)')
  await a.click('[data-channel=checkout-app]')
  await a.fill('[data-testid=composer]', 'Morning! The cart total is wrong when a coupon is applied.'); await a.keyboard.press('Enter')
  await a.fill('[data-testid=composer]', '@rosa could you take a look at the coupon code today?'); await a.keyboard.press('Enter')
  await r.waitForSelector('[data-channel=checkout-app] [data-testid=unread-badge]')
  await r.waitForSelector('[data-testid=mentions-badge]')
  const badge = clean(await r.textContent('[data-channel=checkout-app] [data-testid=unread-badge]'))
  expect(badge === '2' && clean(await r.textContent('[data-testid=mentions-badge]')) === '1', `Rosa's channel list shows 2 unread in #checkout-app and 1 mention (badge "${badge}")`)
  await shot(r, '1-unread-badges')
  expect(await a.locator('[data-channel=checkout-app] [data-testid=unread-badge]').count() === 0, 'Alex has read his own channel: no badge for him')

  await r.click('[data-testid=mentions]')
  await r.waitForSelector('[data-testid=mention]')
  const mention = clean(await r.textContent('[data-testid=mention]'))
  expect(mention.includes('Alex') && mention.includes('#checkout-app') && mention.includes('could you take a look at the coupon code') && await r.locator('[data-testid=mention][data-unread=true]').count() === 1, 'the Mentions inbox shows who, where and what, as unread: ' + mention.slice(0, 90))
  await shot(r, '2-mentions')
  await r.click('[data-testid=mention]')
  await r.waitForSelector('[data-testid=channel-title]:has-text("checkout-app")')
  await r.waitForSelector('[data-channel=checkout-app] [data-testid=unread-badge]', { state: 'detached' })
  await r.waitForSelector('[data-testid=mentions-badge]', { state: 'detached' })
  ok('Rosa opens the mention: she is taken to the channel and the badges clear (it is read)')
  const reads = sql("select stream_id, actor_id, json_extract(data,'$.channelId'), json_extract(data,'$.position') from events where type='ChannelRead'")
  const rosaId = sql("select id from members where handle='rosa'")[0][0]
  expect(reads.length >= 1 && reads.every(x => x[0] === `person-${rosaId}` && x[1] === rosaId), `reading wrote ChannelRead events on Rosa's own stream (${reads.length})`)

  // --- the demo: Rosa asks reviewer to run a shell command; only Alex can allow it ---
  await r.fill('[data-testid=composer]', '@reviewer Use your shell tool to run exactly this command and nothing else: git log -3   Then tell me in one sentence what the newest commit is about.'); await r.keyboard.press('Enter')
  await r.waitForSelector('[data-testid=thread-panel]')
  await a.waitForSelector('[data-testid=thread-card]', { timeout: 30000 }); await a.click('[data-testid=thread-card]')
  await a.waitForSelector('[data-testid=thread-panel] [data-testid=ask-card]', { timeout: LONG }).catch(() => { throw new Error('FAILED: the harness did not ask permission for the shell command (not a pass)') })
  expect((await r.textContent('[data-testid=ask-waiting]')).includes('waiting for Alex'), 'Rosa sees "reviewer is waiting for Alex"')
  await a.click('[data-testid=ask-card] button:has-text("Allow once")')
  await a.waitForFunction(() => /idle/.test(document.querySelector('[data-testid=agent-status]')?.textContent ?? ''), null, { timeout: LONG })
  await r.waitForFunction(() => /idle/.test(document.querySelector('[data-testid=agent-status]')?.textContent ?? ''), null, { timeout: LONG })
  ok('Alex allowed it once; reviewer ran it and answered')
  await until('the turn to end in the log', () => sql("select count(*) from events where type='TurnEnded'")[0][0] >= 1)

  // --- the Audit: a sentence built from the causation chain ---
  await a.keyboard.press('Escape')
  await a.click('[aria-label="Close thread"]')       // the thread shows what a real harness wrote about this repository: keep it out of the pictures
  expect(await r.locator('[data-testid=server-menu]').count() === 1, 'Rosa has the server menu') 
  await r.click('[data-testid=server-menu]')
  expect(await r.locator('[data-testid=menu-audit]').count() === 0, 'Rosa (not an admin) has no Audit entry in the server menu')
  await r.keyboard.press('Escape')
  expect(await r.evaluate(async () => (await fetch('/api/audit')).status) === 403, 'and the server refuses her the audit (403)')
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-audit]')
  const demo = /Rosa asked reviewer \(Alex's agent\) to run git log -3\. Alex allowed it once\. It ran on build-01\./
  const row = await until('the audit to tell the story', async () => (await a.locator('[data-testid=audit-entry]').allTextContents()).map(clean).find(t => demo.test(t)), 30000)
  expect(demo.test(row), 'Audit says: ' + row.replace(/ \S+$/, ''))
  expect(await a.locator('[data-testid=audit-entry] code:has-text("git log -3")').count() === 1, 'the command is set as code')
  const ask = sql("select position, correlation from events where type='PermissionRequested'")[0], grant = sql("select causation, correlation, actor_id from events where type='PermissionGranted'")[0]
  const alexId = sql("select id from members where handle='alex'")[0][0]
  expect(grant[0] === ask[0] && grant[1] === ask[1] && grant[2] === alexId, 'in the log the grant names the ask it answers, in the same turn, by Alex: the sentence is that chain')
  const all = (await a.locator('[data-testid=audit-entry]').allTextContents()).map(clean)
  expect(all.some(t => t.startsWith('Alex posted in #checkout-app, mentioning')) || all.some(t => t.startsWith('Rosa posted in #checkout-app, mentioning')), 'people\'s posts are there too, as "who posted where", without their words')
  expect(!all.some(t => t.includes('coupon')), 'what people wrote is not in the audit')
  await shot(a, '3-audit')

  // filters: person, agent, channel
  const pick = async (testid, label) => { await a.click(`[data-testid=${testid}]`); await a.click(`[role=option]:has-text("${label}")`); await a.waitForTimeout(400) }
  const texts = async () => (await a.locator('[data-testid=audit-entry]').allTextContents()).map(clean)
  await pick('audit-person', 'Rosa')
  let t = await texts()
  expect(t.some(x => demo.test(x)) && !t.some(x => x.startsWith('Alex created')) && t.length < all.length, `filter by person (Rosa): her story is there, Alex's setup is not (${t.length} of ${all.length})`)
  await shot(a, '4-audit-filtered-person')
  await pick('audit-person', 'Everyone'); await pick('audit-agent', 'reviewer')
  t = await texts()
  expect(t.some(x => demo.test(x)) && !t.some(x => x.startsWith('Rosa joined')), `filter by agent (reviewer): its work, not Rosa joining (${t.length})`)
  await pick('audit-agent', 'All agents'); await pick('audit-channel', '#general')
  t = await texts()
  expect(!t.some(x => demo.test(x)) && t.length >= 1, `filter by channel (#general): the story is in #checkout-app, not here (${t.length})`)
  await pick('audit-channel', 'All channels')

  // --- rebuild each view from zero: the same answers afterwards ---
  await a.keyboard.press('Escape')
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-audit]')
  await a.waitForSelector('[data-testid=projections] [data-projection]')
  const before = await texts()
  expect((await a.locator('[data-testid=projections] [data-testid=checkpoint]').allTextContents()).length === 3, 'the Audit lists the three views with their place in the log')
  await shot(a, '5-audit-rebuild')
  const headNow = sql('select max(position) from events')[0][0]
  for (const name of ['audit', 'unread', 'agent-activity']) {
    await a.click(`[data-rebuild=${name}]`)
    await a.waitForSelector('[data-testid=rebuilt]')
    await until(`${name} to be back at the head`, () => sql(`select position from projection_checkpoints where name='${name}'`)[0]?.[0] >= headNow)
  }
  ok('every view was rebuilt from zero with its Rebuild button, and sits at the head of the log again')
  const afterRebuild = await until('the audit to list again', async () => { const x = await texts(); return x.length ? x : null })
  expect(JSON.stringify(afterRebuild.map(x => x.replace(/ \S+ ?(ago)?$/, ''))) === JSON.stringify(before.map(x => x.replace(/ \S+ ?(ago)?$/, ''))), `the audit reads the same after the rebuild (${afterRebuild.length} entries)`)
  await a.keyboard.press('Escape')

  // --- agent activity on the agent's profile ---
  await a.click('[data-agent=reviewer] [data-testid=agent-activity-row]')
  await a.waitForSelector('[data-testid=turns-list] [data-testid=turn]')
  const summary = clean(await a.textContent('[data-testid=activity-summary]'))
  expect(summary.includes('online') && summary.includes('owned by Alex') && summary.includes(`runs on ${HOST}`), 'the profile says online, owned by Alex, running on ' + HOST)
  const turn = clean(await a.locator('[data-testid=turn]').first().textContent())
  expect(/answered/.test(turn) && turn.includes('#checkout-app') && turn.includes('Rosa') && /[1-9] tool call/.test(turn), 'its turn shows: answered, in #checkout-app, for Rosa, with tool calls: ' + turn)
  expect(Number(await a.textContent('[data-testid=total-turns]')) >= 1 && clean(await a.textContent('[data-testid=total-failed]')) === '0', 'totals: turns counted, no failures')
  await shot(a, '6-agent-activity')
  await a.keyboard.press('Escape')

  // --- unread survives its rebuild: Rosa is away in #general, Alex mentions her again ---
  await r.keyboard.press('Escape')
  await r.click('[data-channel=general]'); await r.waitForSelector('[data-testid=channel-title]:has-text("general")')
  await a.fill('[data-testid=composer]', '@rosa one more thing: please add a test for the coupon.'); await a.keyboard.press('Enter')
  await r.waitForSelector('[data-channel=checkout-app] [data-testid=unread-badge]'); await r.waitForSelector('[data-testid=mentions-badge]')
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-audit]')
  await a.click('[data-rebuild=unread]'); await a.waitForSelector('[data-testid=rebuilt]')
  await a.keyboard.press('Escape')
  await r.waitForTimeout(800)
  expect(clean(await r.textContent('[data-channel=checkout-app] [data-testid=unread-badge]')) === '1' && clean(await r.textContent('[data-testid=mentions-badge]')) === '1', 'after rebuilding the unread view Rosa still has 1 unread and 1 mention')
  await shot(r, '7-unread-after-rebuild')
  console.log('PHASE 6 LIVE CHECK PASSED')
} catch (e) {
  console.log('FAILED', e.message)
  await shot(a, 'FAIL-alex').catch(() => {}); await shot(r, 'FAIL-rosa').catch(() => {})
  process.exitCode = 1
} finally {
  await stopGroup(runner); await stopGroup(server)
  await browser.close()
  SCRATCH.cleanup()
}
