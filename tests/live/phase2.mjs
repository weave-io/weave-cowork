// Journey steps 7-10 with a REAL runner and a REAL harness (OpenCode 2 under a scratch HOME).
//   BASE=http://127.0.0.1:5882 DB=~/.cache/cowork-scratch/p2/cowork.db node tests/live/phase2.mjs
import { launch, person, BASE, SHOTS, expect, ok } from './lib.mjs'
import { spawn, execFileSync } from 'node:child_process'
import { homedir } from 'node:os'
import { readFileSync, existsSync, mkdirSync, readdirSync } from 'node:fs'

const HOME = homedir()
const DB = process.env.DB ?? `${HOME}/.cache/cowork-scratch/p2/cowork.db`
const OC = `${HOME}/.cache/cowork-oc2`
const REPO = new URL('../../', import.meta.url).pathname.replace(/\/$/, '')
const RUNNER = `${REPO}/src/CoWork.Runner/bin/Debug/net10.0/cowork-runner.dll`
const WORKSPACE = process.env.WORKSPACE ?? REPO
const HARNESS = process.env.HARNESS ?? `${HOME}/.weave/harnesses/opencode2/.opencode/bin/opencode acp`
const ENV = { ...process.env, HOME: `${OC}/home`, XDG_CONFIG_HOME: `${OC}/config`, XDG_DATA_HOME: `${OC}/data`, XDG_STATE_HOME: `${OC}/state`, XDG_CACHE_HOME: `${OC}/cache` }
mkdirSync(`${OC}/home`, { recursive: true })
const LONG = 150000
const sql = (q) => execFileSync('python3', ['-I', '-c', 'import sqlite3,sys,json;c=sqlite3.connect(sys.argv[1]);print(json.dumps(c.execute(sys.argv[2]).fetchall()))', DB, q]).toString()

let runner = null
function startRunner(args) {
  runner = spawn('dotnet', [RUNNER, ...args], { env: ENV, stdio: ['ignore', 'pipe', 'pipe'], detached: true })
  runner.log = ''
  runner.stdout.on('data', d => { runner.log += d; process.stdout.write('  [runner] ' + d) })
  runner.stderr.on('data', d => { runner.log += d; process.stdout.write('  [runner!] ' + d) })
  return runner
}
const stopRunner = () => new Promise(res => { if (!runner || runner.exitCode !== null) return res(); runner.once('exit', res); try { process.kill(-runner.pid, 'SIGTERM') } catch { return res() } setTimeout(res, 8000) })

let auto = true, finished = false      // the owner's tab allows the harness's permission asks, except while a step inspects one
const browser = await launch()
const A = await person(browser), R = await person(browser)
const a = A.page, r = R.page
const shot = (p, name) => p.screenshot({ path: SHOTS + `phase2-${name}.png` })
try {
  ;(async () => { while (!finished) { if (auto) { const b = a.locator('[data-testid=ask-card] button:has-text("Allow once")'); if (await b.count().catch(() => 0)) await b.first().click({ timeout: 2000 }).catch(() => {}) } await new Promise(res => setTimeout(res, 1500)) } })()
  // --- people, as in phase 1 (done fast) ---
  await a.goto(BASE)
  await a.fill('[name=serverName]', 'Lumen Labs'); await a.fill('[name=username]', 'alex'); await a.fill('[name=displayName]', 'Alex'); await a.fill('[name=password]', 'correct horse')
  await a.click('button[type=submit]'); await a.waitForSelector('[data-channel=general]')
  await a.click('[data-testid=create-channel]'); await a.fill('[name=name]', 'checkout-app'); await a.fill('[name=topic]', 'Cart, payment and order confirmation'); await a.click('button:has-text("Create channel")')
  await a.waitForSelector('[data-testid=channel-title]:has-text("checkout-app")')
  const code = await a.evaluate(async () => (await (await fetch('/api/invites', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json()).code)
  await r.goto(`${BASE}/invite/${code}`)
  await r.fill('[name=username]', 'rosa'); await r.fill('[name=displayName]', 'Rosa'); await r.fill('[name=password]', 'rosa-password'); await r.click('button[type=submit]')
  await r.waitForSelector('[data-channel=checkout-app]'); await r.click('[data-channel=checkout-app]')
  ok('Alex and Rosa are in #checkout-app')

  // --- 7. an agent joins ---
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-invite-agent]')
  await a.fill('[name=handle]', 'reviewer'); await a.fill('[name=about]', 'Reviews the checkout app')
  await a.fill('[name=instructions]', 'You are reviewer, a code reviewer for the Lumen Labs checkout app. Answer in at most four short sentences. Only read files; never change anything.')
  await a.fill('[name=harness]', HARNESS); await a.fill('[name=workspace]', WORKSPACE)
  await shot(a, 'invite-agent-form')
  await a.click('button:has-text("Create agent")')
  await a.waitForSelector('[data-testid=runner-command]')
  const cmd = (await a.textContent('[data-testid=runner-command]')).trim()
  expect(/^cowork-runner connect http:\/\/\S+ --code [A-Z0-9]{4}-[A-Z0-9]{4}$/.test(cmd), 'the dialog gives the exact command: ' + cmd)
  await shot(a, 'invite-agent-command')
  const parts = cmd.split(' ')
  startRunner(['connect', BASE, '--code', parts[parts.length - 1]])
  await a.waitForSelector('[data-testid=agent-connected]', { timeout: 30000 })
  ok('dialog shows "reviewer is connected"')
  await a.check('[data-pick=checkout-app]'); await shot(a, 'invite-agent-connected')
  await a.click('[data-testid=join-channels]')
  await a.keyboard.press('Escape')
  await a.waitForSelector('[data-agent=reviewer]:has-text("online")')
  const sub = await a.textContent('[data-agent=reviewer] [data-testid=agent-sub]')
  expect(/online · owned by Alex/.test(sub) && await a.locator('[data-agent=reviewer] >> text=AGENT').count() > 0, 'reviewer is in the member list: agent, online, owned by Alex')
  await r.waitForSelector('[data-agent=reviewer]:has-text("online")')
  ok('Rosa sees reviewer online too')
  const cfg = readFileSync(`${OC}/config/cowork-runner/agents.json`, 'utf8')
  expect(cfg.includes('"token"') && !sql('select token_hash from agents').includes(JSON.parse(cfg)[0].token), 'runner saved its token; the server stores only a hash')

  // --- 8. the agent takes part ---
  await a.click('[data-channel=checkout-app]')
  await a.fill('[data-testid=composer]', '@reviewer what does this repository do? Read README.md with your read tool before you answer.'); await a.keyboard.press('Enter')
  await a.waitForSelector('[data-testid=thread-panel]')
  ok('posting a mention opens a thread on that post')
  const sess = await a.textContent('[data-testid=session-line]')
  expect(sess.includes('@reviewer') && sess.includes('opencode acp') && !sess.includes('/.opencode/bin/') && sess.includes('started by Alex'), 'session line: agent, short harness command, who started it: ' + sess.replace(/\s+/g, ' ').trim())
  expect((await a.getAttribute('[data-testid=session-command]', 'title')).includes('/opencode'), 'the full harness command is in the tooltip')
  await a.waitForSelector('[data-testid=thread-panel] [data-post]:has-text("reviewer")', { timeout: LONG })
  await a.waitForFunction(() => /idle/.test(document.querySelector('[data-testid=agent-status]')?.textContent ?? ''), null, { timeout: LONG })
  const first = await a.evaluate(() => [...document.querySelectorAll('[data-testid=thread-panel] [data-post]')].map(e => e.textContent).join('\n'))
  console.log('  first answer: ' + first.slice(0, 500).replace(/\n/g, ' | '))
  const tools = await a.locator('[data-testid=thread-panel] [data-testid=tool-row]').count()
  ok(`reviewer answered in the thread (${tools} tool rows)`)
  const bodies = await a.locator('[data-testid=thread-panel] [data-testid=tool-row] span.truncate').allTextContents()
  console.log('  tool rows: ' + bodies.join(' | '))
  expect(tools > 0 && bodies.every(b => !/^(read|grep|glob|search|edit|shell|list)$/i.test(b.trim())), 'every tool row says what it touched (a path, a pattern or a command), not just the tool')
  await shot(a, 'agent-answer')

  await r.click('[data-testid=thread-card]')
  await r.waitForSelector('[data-testid=thread-panel] [data-post]:has-text("reviewer")')
  ok('Rosa opens the same thread and sees the answer')
  await r.fill('[data-testid=thread-panel] [data-testid=composer]', '@reviewer and what tests does it have?'); await r.keyboard.press('Enter')
  for (let i = 0; i < 300 && !/^\[\["done"\],\["done"\]\]$/.test(sql("select status from deliveries order by id")); i++) await new Promise(res => setTimeout(res, 500))
  await r.waitForFunction(() => /idle/.test(document.querySelector('[data-testid=agent-status]')?.textContent ?? ''), null, { timeout: LONG })
  const second = await r.evaluate(() => [...document.querySelectorAll('[data-testid=thread-panel] [data-post]')].at(-1).textContent)
  console.log('  second answer: ' + second.slice(0, 400))
  const deliveries = JSON.parse(sql("select text, status from deliveries order by id"))
  expect(deliveries.length === 2 && deliveries[1][0].includes('from="@rosa"') && deliveries[1][0].includes('name="Rosa"') && deliveries[1][0].includes('owner="false"'), 'the second message reached the agent wrapped with its author: ' + deliveries[1][0].slice(0, 120))
  expect(deliveries.every(d => d[1] === 'done'), 'both deliveries are done')
  await shot(r, 'rosa-reply')

  // permission: only the owner gets buttons. The scratch OpenCode config asks for every shell command ("bash": "ask"), and the
  // command is new each run, in a new thread, so no earlier "allow" can apply: the ask card MUST appear, or this check fails.
  auto = false
  const nonce = Math.random().toString(36).slice(2, 8)
  await a.fill('[data-testid=composer]', `@reviewer Use your shell tool to run exactly this command and nothing else: echo ask-${nonce}   Then tell me what it printed.`); await a.keyboard.press('Enter')
  await a.waitForSelector('[data-testid=thread-panel] [data-testid=ask-card]', { timeout: LONG }).catch(() => { throw new Error('FAILED: the harness did not ask permission for a new shell command, so the owner-approval checks could not run (not a pass)') })
  await r.click(`[data-post]:has-text("ask-${nonce}") >> [data-testid=thread-card]`)
  await r.waitForSelector('[data-testid=ask-card]')
  expect((await r.textContent('[data-testid=ask-waiting]')).includes('waiting for Alex'), 'Rosa sees "reviewer is waiting for Alex"')
  expect(await r.locator('[data-testid=ask-card] button').count() === 0, 'Rosa has no Allow / Deny buttons')
  expect(await a.locator('[data-testid=ask-card] button:has-text("Deny")').count() === 1, 'Alex has Allow / Deny')
  await shot(a, 'permission-owner'); await shot(r, 'permission-rosa')
  const askId = JSON.parse(sql("select id from posts where kind='ask' order by id desc limit 1"))[0][0]
  const rosaTry = await r.evaluate(async (id) => (await fetch(`/api/posts/${id}/answer`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"optionId":null}' })).status, askId)
  expect(rosaTry === 403, 'the server refuses Rosa answering the owner\'s ask (403)')
  await a.click('[data-testid=ask-card] button:has-text("Allow once")')
  await a.waitForFunction((n) => document.querySelector('[data-testid=thread-panel]')?.textContent?.includes('ask-' + n) && /idle/.test(document.querySelector('[data-testid=agent-status]')?.textContent ?? ''), nonce, { timeout: LONG })
  expect(await a.locator('[data-testid=ask-card]:has-text("chose")').count() === 1, 'Alex allowed it; the harness carried on and answered')
  auto = true
  await a.waitForFunction(() => /idle/.test(document.querySelector('[data-testid=agent-status]')?.textContent ?? ''), null, { timeout: LONG })

  // --- 9. the agent goes offline ---
  await stopRunner()
  await a.waitForSelector('[data-testid=agent-status]:has-text("offline")')
  await r.waitForSelector('[data-testid=agent-status]:has-text("offline")')
  ok('runner stopped: reviewer shows offline')
  const before = await r.locator('[data-testid=thread-panel] [data-post]').count()
  await r.fill('[data-testid=thread-panel] [data-testid=composer]', '@reviewer last one: which license does it use?'); await r.keyboard.press('Enter')
  await r.waitForSelector('[data-testid=waiting-runner]')
  expect((await r.textContent('[data-testid=waiting-runner]')).includes('Waiting for reviewer'), 'the thread says "Waiting for reviewer\'s runner"')
  await shot(r, 'waiting-runner')
  startRunner(['run'])
  await a.waitForSelector('[data-testid=agent-status]:has-text("working"), [data-testid=agent-status]:has-text("idle")', { timeout: 30000 })
  await r.waitForSelector('[data-testid=waiting-runner]', { state: 'detached', timeout: 60000 })
  await r.waitForFunction((n) => document.querySelectorAll('[data-testid=thread-panel] [data-post]').length > n + 1, before, { timeout: LONG })
  ok('the runner came back and the waiting mention was answered')
  expect(/thread \d+: loaded session /.test(runner.log), 'the restarted runner loaded the thread\'s saved harness session (session/load)')
  expect(existsSync(`${OC}/config/cowork-runner`) && readdirSync(`${OC}/config/cowork-runner`).some(f => f.startsWith('sessions-')), 'the runner keeps session ids in its saved state')
  await r.waitForFunction(() => /idle/.test(document.querySelector('[data-testid=agent-status]')?.textContent ?? ''), null, { timeout: LONG })

  // --- 10. admin over agents ---
  await a.click('[aria-label="Close thread"]')
  await a.click('[data-testid=channel-settings]')
  await a.click('[data-remove=reviewer]')
  await a.waitForFunction(() => !document.querySelector('[data-testid=channel-members]').textContent.includes('reviewer'))
  await a.keyboard.press('Escape')
  await a.waitForSelector('[data-agent=reviewer]', { state: 'detached' })
  ok('Alex removes reviewer from the channel')
  await a.click('[data-testid=channel-settings]'); await a.click('[data-testid=add-member]'); await a.click('[role=option]:has-text("reviewer (@reviewer) · agent")'); await a.click('button:has-text("Add"):not(:has-text("Add to"))')
  await a.keyboard.press('Escape'); await a.waitForSelector('[data-agent=reviewer]')
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-people]')
  await shot(a, 'people-agents')
  await a.click('[data-testid=agents-list] [data-testid=edit-agent]')
  await a.waitForSelector('[name=instructions]')
  expect((await a.inputValue('[name=instructions]')).startsWith('You are reviewer'), 'the edit dialog shows the agent\'s instructions')
  await a.fill('[name=about]', 'Reviews the checkout app, read only'); await shot(a, 'edit-agent')
  await a.click('[data-testid=save-agent]'); await a.waitForSelector('[data-testid=agent-saved]')
  expect(sql("select about from agents").includes('read only'), 'Alex edited the agent; the change is saved')
  await a.keyboard.press('Escape')
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-people]')
  await a.click('[data-testid=agents-list] [data-testid=remove-agent]'); await a.click('[data-testid=confirm-action]')
  await a.waitForSelector('[data-testid=agents-list] [data-agent=reviewer]', { state: 'detached' })
  ok('Alex removes reviewer entirely')
  await new Promise(res => setTimeout(res, 3000))
  const tokenRows = sql("select token_hash from agents")
  expect(tokenRows.includes('null'), 'the runner token is cut off on the server (hash cleared)')
  await new Promise(res => setTimeout(res, 2000))
  console.log('  runner log tail: ' + runner.log.split('\n').slice(-4).join(' | '))
  expect(/forgetting|removed this agent/.test(runner.log) || JSON.parse(readFileSync(`${OC}/config/cowork-runner/agents.json`, 'utf8')).length === 0, 'the runner dropped the agent (told by the server)')
  console.log('PHASE 2 LIVE CHECK PASSED')
} catch (e) {
  console.log('FAILED', e.message)
  await shot(a, 'FAIL-alex').catch(() => {}); await shot(r, 'FAIL-rosa').catch(() => {})
  process.exitCode = 1
} finally {
  finished = true
  await stopRunner()
  await browser.close()
}
