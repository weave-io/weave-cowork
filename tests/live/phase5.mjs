// Round 1 of brief 4: conversations as events. A browser whose connection drops while an agent answers, and comes back,
// shows exactly what it missed WITHOUT reloading the page; the `events` table holds the thread's history; the per-agent
// "Record full tool output" switch; a server restart mid-turn leaves "cut off" in the thread.
// Starts its own server (default port 5886) and a REAL runner with a scripted ACP agent (tests/live/scripted_acp_agent.py: it
// waits for gate files so the test decides when it speaks). Real harnesses are covered by phase2.mjs.
//   node tests/live/phase5.mjs
import { launch, person, SHOTS, expect, ok, setUp, ssoJoin, SSO_ENV } from './lib.mjs'
import { spawn, execFileSync } from 'node:child_process'
import { homedir } from 'node:os'
import { rmSync, mkdirSync, writeFileSync, existsSync, readFileSync } from 'node:fs'

const HOME = homedir()
const PORT = process.env.PORT ?? '5886'
const BASE = `http://127.0.0.1:${PORT}`
const REPO = new URL('../../', import.meta.url).pathname.replace(/\/$/, '')
const DIR = `${HOME}/.cache/cowork-p5`
const DB = `${DIR}/data/cowork.db`
const GATE = `${DIR}/gate`
const SERVER = `${REPO}/src/CoWork.Server/bin/Debug/net10.0/CoWork.Server.dll`
const RUNNER = `${REPO}/src/CoWork.Runner/bin/Debug/net10.0/cowork-runner.dll`
const HARNESS = `python3 ${REPO}/tests/live/scripted_acp_agent.py`
const ENV = { ...process.env, HOME: `${DIR}/home`, XDG_CONFIG_HOME: `${DIR}/config`, XDG_DATA_HOME: `${DIR}/xdg-data`, XDG_STATE_HOME: `${DIR}/state`, XDG_CACHE_HOME: `${DIR}/cache`, GATE_DIR: GATE }
rmSync(DIR, { recursive: true, force: true })
for (const d of ['data', 'gate', 'home']) mkdirSync(`${DIR}/${d}`, { recursive: true })
const sql = (q) => JSON.parse(execFileSync('python3', ['-I', '-c', 'import sqlite3,sys,json;c=sqlite3.connect(sys.argv[1]);print(json.dumps(c.execute(sys.argv[2]).fetchall()))', DB, q]).toString())
const until = async (what, probe, ms = 40000) => { const end = Date.now() + ms; for (;;) { const v = await probe(); if (v) return v; if (Date.now() > end) throw new Error('FAILED: timed out waiting for ' + what); await new Promise(r => setTimeout(r, 100)) } }

let server = null, runner = null
function startServer() {
  server = spawn('dotnet', [SERVER], { cwd: `${REPO}/src/CoWork.Server`, env: { ...process.env, ...SSO_ENV, COWORK_DB: DB, ASPNETCORE_URLS: BASE }, stdio: ['ignore', 'pipe', 'pipe'], detached: true })
  server.log = ''; server.stdout.on('data', d => server.log += d); server.stderr.on('data', d => server.log += d)
  return until('the server to listen', async () => { try { return (await fetch(BASE + '/api/setup/status')).ok } catch { return false } }, 30000)
}
const stopGroup = (p, signal = 'SIGTERM') => new Promise(res => { if (!p || p.exitCode !== null) return res(); p.once('exit', res); try { process.kill(-p.pid, signal) } catch { return res() } setTimeout(res, 8000) })
function startRunner(args) {
  runner = spawn('dotnet', [RUNNER, ...args], { env: ENV, stdio: ['ignore', 'pipe', 'pipe'], detached: true })
  runner.stdout.on('data', d => process.stdout.write('  [runner] ' + d)); runner.stderr.on('data', d => process.stdout.write('  [runner!] ' + d))
}

// what a thread panel shows, in order: posts, tool rows and ask cards
const view = (p) => p.evaluate(() => [...document.querySelectorAll('[data-testid=thread-panel] [data-post], [data-testid=thread-panel] [data-testid=tool-row], [data-testid=thread-panel] [data-testid=ask-card]')]
  .map(e => (e.getAttribute('data-testid') ?? 'post') + ': ' + e.textContent.replace(/\s+/g, ' ').replace(/\d{1,2}:\d{2}\s?[AP]M/g, '').replace(/Allow once.*$|Deny.*$|[a-z]+ is waiting for [A-Za-z]+$/, '').trim()).slice(1))
const idle = (p) => p.waitForFunction(() => /idle/.test(document.querySelector('[data-testid=agent-status]')?.textContent ?? ''), null, { timeout: 40000 })

const browser = await launch()
const A = await person(browser), R = await person(browser)
const a = A.page, r = R.page
const shot = (p, name) => p.screenshot({ path: SHOTS + `phase5-${name}.png` })
let finished = false
try {
  await startServer()

  // --- Alex's page: its connection can be dropped, and every request it makes after the drop is recorded ---
  let blocked = false; const sockets = []; const requestsAfterDrop = []; let navigations = 0
  await a.route(/\/hubs\/app\/negotiate/, route => blocked ? route.abort('connectionfailed') : route.continue())
  await a.routeWebSocket(/\/hubs\/app/, ws => { if (blocked) { ws.close(); return } ws.connectToServer(); sockets.push(ws) })
  a.on('request', q => { if (blocked || requestsAfterDrop.dropped) requestsAfterDrop.push(q.method() + ' ' + new URL(q.url()).pathname) })
  a.on('framenavigated', () => navigations++)

  // --- people and the agent, as in phase 2 ---
  await a.goto(BASE)
  await setUp(a)
  await a.waitForSelector('[data-channel=general]')
  await a.click('[data-testid=create-channel]'); await a.fill('[name=name]', 'checkout-app'); await a.fill('[name=topic]', 'Cart, payment and order confirmation'); await a.click('button:has-text("Create channel")')
  await a.waitForSelector('[data-testid=channel-title]:has-text("checkout-app")')
  const code = await a.evaluate(async () => (await (await fetch('/api/invites', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json()).code)
  await r.goto(`${BASE}/invite/${code}`)
  await ssoJoin(r, 'rosa', 'Rosa')
  await r.waitForSelector('[data-channel=checkout-app]'); await r.click('[data-channel=checkout-app]')
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-invite-agent]')
  await a.fill('[name=handle]', 'reviewer'); await a.fill('[name=about]', 'Reviews the checkout app')
  await a.fill('[name=instructions]', 'You are reviewer, a code reviewer for the Lumen Labs checkout app.')
  await a.fill('[name=harness]', HARNESS); await a.fill('[name=workspace]', REPO)
  await a.click('button:has-text("Create agent")')
  await a.waitForSelector('[data-testid=runner-command]')
  const parts = (await a.textContent('[data-testid=runner-command]')).trim().split(' ')
  startRunner(['connect', BASE, '--code', parts[parts.length - 1]])
  await a.waitForSelector('[data-testid=agent-connected]', { timeout: 30000 })
  await a.check('[data-pick=checkout-app]'); await a.click('[data-testid=join-channels]'); await a.keyboard.press('Escape')
  await a.waitForSelector('[data-agent=reviewer]:has-text("online")')
  ok('Alex and Rosa are in #checkout-app, and reviewer (a real runner, a scripted harness) is online')

  // --- the answer starts; Alex's connection drops while it is being written ---
  await a.click('[data-channel=checkout-app]')
  await a.evaluate(() => { window.__sameWindow = 'yes' })
  await a.fill('[data-testid=composer]', '@reviewer SCENARIO-A what does this repository do?'); await a.keyboard.press('Enter')
  await a.waitForSelector('[data-testid=thread-panel]')
  await a.waitForSelector('[data-testid=thread-panel] [data-post]:has-text("Part one.")')
  await r.click('[data-testid=thread-card]')
  await r.waitForSelector('[data-testid=thread-panel] [data-post]:has-text("Part one.")')
  ok('both browsers see the first part of the answer as it is written')
  await a.evaluate(() => { performance.mark('before-drop') })
  const navBefore = navigations
  blocked = true; requestsAfterDrop.dropped = true
  for (const ws of sockets) ws.close({ code: 1006, reason: 'test: connection dropped' })
  await new Promise(res => setTimeout(res, 1500))
  writeFileSync(`${GATE}/go1`, '')                      // the agent goes on: more text, two tool calls and a permission ask, all while Alex is offline
  await until('the approval to be asked', () => sql("select count(*) from events where type='PermissionRequested'")[0][0] === 1)
  await r.waitForSelector('[data-testid=thread-panel] [data-testid=ask-card]')
  await until('Rosa to see part two', async () => (await view(r)).some(t => t.includes('Part two.')))
  const missed = await view(a)
  expect(!missed.some(t => t.includes('Part two.')) && missed.every(t => !t.startsWith('ask-card')), 'while it is offline Alex\'s page shows nothing of what came after: ' + JSON.stringify(missed.map(t => t.slice(0, 40))))
  await shot(a, '1-offline'); await shot(r, '1-rosa-live')

  // --- the connection comes back: no reload, and exactly what was missed appears ---
  blocked = false
  await a.waitForSelector('[data-testid=thread-panel] [data-testid=ask-card]', { timeout: 40000 })
  const got = await a.evaluate(() => window.__sameWindow)
  expect(got === 'yes' && navigations === navBefore, 'the page was not reloaded (same window, no navigation)')
  const reloaded = requestsAfterDrop.filter(x => /^GET \/api\/(channels\/\d+\/posts|threads\/\d+)/.test(x))
  expect(reloaded.length === 0, 'catching up fetched no channel or thread over the API: ' + JSON.stringify([...new Set(requestsAfterDrop)]))
  await until('Alex to see the same thread as Rosa', async () => JSON.stringify(await view(a)) === JSON.stringify(await view(r)))
  const seen = await view(a)
  expect(seen.some(t => t.includes('Part two.')) && seen.filter(t => t.startsWith('tool-row')).length === 2 && seen.some(t => t.startsWith('ask-card')), 'it shows the rest of the answer, both tool rows and the approval: ' + JSON.stringify(seen.map(t => t.slice(0, 50))))
  expect(seen.some(t => /read\s*README\.md/.test(t)) && seen.some(t => t.includes('git log -3')), 'the tool rows say what they touched (README.md, git log -3)')
  expect(await a.locator('[data-testid=ask-card] button:has-text("Allow once")').count() === 1 && await r.locator('[data-testid=ask-card] button').count() === 0, 'Alex (the owner) has the buttons, Rosa sees that it is waiting')
  await shot(a, '2-caught-up')

  // --- Alex allows it from the page that just caught up; the answer finishes live on both ---
  await a.click('[data-testid=ask-card] button:has-text("Allow once")')
  await a.waitForSelector('[data-testid=thread-panel] [data-post]:has-text("Part three.")'); await idle(a); await idle(r)
  await until('both pages to agree', async () => JSON.stringify(await view(a)) === JSON.stringify(await view(r)))
  ok('after Alex allows it the last part arrives live and both browsers show the same thread: ' + (await view(a)).length + ' items')
  await shot(a, '3-finished')

  // --- the events table holds the thread's history, as in the design's "One thread as events" ---
  const root = sql("select id from posts where thread_id is null and body like '%SCENARIO-A%'")[0][0]
  const rows = sql(`select position, stream_id, type, actor_id, on_behalf_of, correlation, causation, data from events where stream_id='thread-${root}' or (stream_id like 'channel-%' and type='MessagePosted') order by position`)
  console.log('  events: ' + rows.map(x => x[2]).join(' → '))
  const types = rows.map(x => x[2])
  const squashed = types.filter((t, i) => t !== types[i - 1])
  const wanted = ['MessagePosted', 'ThreadStarted', 'AgentJoinedThread', 'MessageDelivered', 'TurnStarted', 'AgentReplied', 'ToolCallStarted', 'ToolCallDetailed', 'ToolCallFinished', 'ToolCallStarted', 'ToolCallDetailed', 'PermissionRequested', 'PermissionGranted', 'ToolCallFinished', 'AgentReplied', 'TurnEnded']      // (the first AgentReplied is two segments)
  expect(JSON.stringify(squashed) === JSON.stringify(wanted), 'the thread stream reads like the design: ' + squashed.join(' → '))
  const by = Object.fromEntries(rows.map(x => [x[2], x]))
  const req = rows.find(x => x[2] === 'PermissionRequested'), granted = rows.find(x => x[2] === 'PermissionGranted'), delivered = by.MessageDelivered, started = by.TurnStarted
  expect(granted[6] === req[0] && granted[3] === 1 && granted[5] === req[5], 'PermissionGranted is caused by PermissionRequested, by Alex, in the same turn')
  expect(delivered[4] === 1 && started[5] === delivered[5] && started[6] === delivered[0] && started[4] === 1, 'the turn id ties delivery and start together, the start was caused by the delivery, both on behalf of Alex')
  expect(rows.filter(x => x[1] === `thread-${root}` && ['TurnStarted', 'AgentReplied', 'ToolCallStarted', 'ToolCallFinished', 'TurnEnded'].includes(x[2])).every(x => x[5] === started[5] && x[4] === 1), 'every event of the turn carries its correlation id and the person it was for')
  const toolEnds = rows.filter(x => x[2] === 'ToolCallFinished').map(x => JSON.parse(x[7]))
  expect(toolEnds.length === 2 && toolEnds.every(d => d.outputSha256?.length === 64 && d.outputBytes > 0 && d.output == null), 'with "Record full tool output" off, a tool call keeps its size and SHA-256 and no text')
  const replied = rows.filter(x => x[2] === 'AgentReplied').map(x => JSON.parse(x[7]))
  expect(replied.map(d => d.text).join('').replace(/\s+/g, ' ').trim() === 'Part one. Part two. Part three. The last commits are listed.', 'the answer is stored in segments that add up to what was said')
  expect(sql("select count(*) from events where type='AgentReplied' and data like '%offset%'")[0][0] === 0, 'streamed deltas are not stored')

  // --- the per-agent switch ---
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-people]')
  await a.click('[data-testid=agents-list] [data-testid=edit-agent]')
  await a.waitForSelector('[data-testid=record-tool-output]')
  expect(await a.getAttribute('[data-testid=record-tool-output]', 'aria-checked') === 'false', 'the switch "Record full tool output" is off by default')
  await a.click('[data-testid=record-tool-output]'); await shot(a, '4-switch'); await a.click('[data-testid=save-agent]'); await a.waitForSelector('[data-testid=agent-saved]')
  await a.keyboard.press('Escape'); await a.keyboard.press('Escape')
  await a.waitForSelector('[role=dialog]', { state: 'detached' })
  await a.fill('[data-testid=composer]', '@reviewer SCENARIO-A once more, with output'); await a.keyboard.press('Enter')
  await a.waitForSelector('[data-testid=ask-card] button:has-text("Allow once")', { timeout: 40000 }); await a.click('[data-testid=ask-card] button:has-text("Allow once")')
  await until('the second turn to end', () => sql("select count(*) from events where type='TurnEnded'")[0][0] === 2)
  const outs = sql("select data from events where type='ToolCallFinished' order by position desc limit 2").map(x => JSON.parse(x[0]))
  expect(outs.every(d => typeof d.output === 'string' && d.output.length === d.outputBytes) && outs.some(d => d.output.includes('Round tax')), 'with the switch on, the output text is recorded too (up to 64 kB)')
  expect(!sql("select group_concat(meta) from posts where kind='tool'")[0][0].includes('Round tax'), 'the posts the app reads never carry tool output')

  // --- the server restarts in the middle of an answer ---
  await a.fill('[data-testid=composer]', '@reviewer HOLD this one'); await a.keyboard.press('Enter')
  await a.waitForSelector('[data-testid=thread-panel] [data-post]:has-text("Writing the answer")')
  await until('the opening words to be stored', () => sql("select count(*) from posts where streaming=1")[0][0] === 1)
  const posBefore = await a.evaluate(() => window.__sameWindow), navHold = navigations
  await stopGroup(server, 'SIGKILL')                     // a crash: nothing is closed, nothing is tidied
  await startServer()                                    // recovery runs at startup
  expect(sql("select count(*) from events where type='TurnInterrupted'")[0][0] === 1, 'at startup the turn that never ended got a TurnInterrupted')
  await a.waitForSelector('[data-testid=thread-panel] :text("cut off")', { timeout: 40000 })
  expect(await a.evaluate(() => window.__sameWindow) === posBefore && navigations === navHold, 'Alex\'s page found the server again and shows "cut off" in the thread without a reload')
  expect(sql("select count(*) from posts where streaming=1")[0][0] === 0, 'the half-written answer is final, not "streaming"')
  await shot(a, '5-cut-off')
  finished = true
  console.log('PHASE 5 LIVE CHECK PASSED')
} catch (e) {
  console.log(String(e.stack ?? e).split('\n').slice(0, 6).join('\n'))
  if (server?.log) console.log('--- server log (tail) ---\n' + server.log.split('\n').slice(-15).join('\n'))
  try { console.log('Alex sees: ' + JSON.stringify(await view(a), null, 1)); console.log('Rosa sees: ' + JSON.stringify(await view(r), null, 1)) } catch { }
  try { await shot(a, 'failed-alex'); await shot(r, 'failed-rosa') } catch { }
  process.exitCode = 1
} finally {
  await browser.close().catch(() => {})
  await stopGroup(runner); await stopGroup(server)
}
