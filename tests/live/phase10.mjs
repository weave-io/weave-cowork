// Brief 7, round 2: locked down. Browsers for Alex (admin, Checkout app's lead), Rosa and Sam; a REAL pool on this machine, REAL containers
// from cowork-sandbox-opencode:2.4.1 (deploy/sandbox/build.sh) with no network of their own, and REAL OpenCode 2.4.1 whose model calls go
// through the gateway to this machine's GitHub Copilot sign-in (the user agreed, 25 Nov 2026).
// The demo: Alex sets Checkout app's network list (github.com, registry.npmjs.org), its setup command and its budget → Rosa asks researcher to
// fetch example.com: the setup runs first (and reaches the registry through the proxy), the proxy refuses example.com and Rosa gets the ask;
// she allows it once and researcher is told to try again, and reads the page → a thread at its token cap stops cleanly and says why; Alex
// raises it → a container killed mid-turn is started again once and the turn finishes → Alex pauses the project's agents: the sandbox stops
// and Rosa's message waits; resume, and it's answered → inside a container: no network but the two sockets; a listed host passes, any other
// is refused. Starts its own server (default port 5884). Everything in podman carries the pool's label and is removed by it at the end.
//   node tests/live/phase10.mjs          (screenshots go to docs/screens/brief7/)
import { launch, person, expect, ok } from './lib.mjs'
import { spawn, execFileSync } from 'node:child_process'
import { homedir } from 'node:os'
import { rmSync, mkdirSync, writeFileSync, readFileSync } from 'node:fs'

const HOME = homedir()
const PORT = process.env.PORT ?? '5884'
const BASE = `http://127.0.0.1:${PORT}`
const REPO = new URL('../../', import.meta.url).pathname.replace(/\/$/, '')
const DIR = `${HOME}/.cache/cowork-p10`
const DB = `${DIR}/data/cowork.db`
const SHOTS = process.env.SHOTS_DIR ?? `${REPO}/docs/screens/brief7/`
const SERVER = `${REPO}/src/CoWork.Server/bin/Debug/net10.0/CoWork.Server.dll`
const RUNNER = `${REPO}/src/CoWork.Runner/bin/Debug/net10.0/cowork-runner.dll`
const IMAGE = 'cowork-sandbox-opencode:2.4.1'
const LONG = 240000
rmSync(DIR, { recursive: true, force: true }); mkdirSync(`${DIR}/data`, { recursive: true }); mkdirSync(SHOTS, { recursive: true })
const sql = (q) => JSON.parse(execFileSync('python3', ['-I', '-c', 'import sqlite3,sys,json;c=sqlite3.connect(sys.argv[1]);print(json.dumps(c.execute(sys.argv[2]).fetchall()))', DB, q]).toString())
const until = async (what, probe, ms = 60000) => { const end = Date.now() + ms; for (;;) { const v = await probe(); if (v) return v; if (Date.now() > end) throw new Error('FAILED: timed out waiting for ' + what); await new Promise(r => setTimeout(r, 250)) } }
const clean = (t) => (t ?? '').replace(/\s+/g, ' ').trim()
const podman = (...a) => execFileSync('podman', a, { stdio: ['ignore', 'pipe', 'pipe'] }).toString().trim()
const podmanTry = (...a) => { try { return { code: 0, out: podman(...a) } } catch (e) { return { code: e.status, out: (e.stdout?.toString() ?? '') + (e.stderr?.toString() ?? '') } } }
const git = (cwd, ...args) => execFileSync('git', args, { cwd, env: { ...process.env, GIT_AUTHOR_NAME: 'Alex', GIT_AUTHOR_EMAIL: 'alex@lumen.example', GIT_COMMITTER_NAME: 'Alex', GIT_COMMITTER_EMAIL: 'alex@lumen.example' } }).toString().trim()

if (!podman('images', '-q', IMAGE)) { console.log(`FAILED: the image ${IMAGE} isn't built here. Run deploy/sandbox/build.sh first.`); process.exit(1) }
const copilot = (() => { try { return JSON.parse(readFileSync(`${HOME}/.local/share/opencode/auth.json`, 'utf8'))['github-copilot'] } catch { return null } })()
if (!copilot) { console.log('FAILED: this machine has no GitHub Copilot sign-in in OpenCode; the gateway needs a provider.'); process.exit(1) }

// --- the invented repository, with a setup script that reaches the package registry (on the list) through the proxy ---
const seed = `${DIR}/seed`; mkdirSync(`${seed}/src`, { recursive: true }); mkdirSync(`${seed}/scripts`, { recursive: true })
git(seed, 'init', '-q', '-b', 'main')
writeFileSync(`${seed}/README.md`, '# checkout-app\n\nCart, payment and order confirmation for the Lumen Labs web shop.\n')
writeFileSync(`${seed}/src/tax.ts`, '// Tax for one order line, in euros.\nexport function roundTax(amount: number): number {\n  return Math.round(amount * 100) / 100\n}\n')
writeFileSync(`${seed}/scripts/agent-setup.sh`, '#!/bin/sh\n# What an agent needs before it works on the checkout app.\necho "checking the package registry…"\necho "registry.npmjs.org answered $(curl -s -o /dev/null -w "%{http_code}" -m 20 https://registry.npmjs.org/)"\ngit rev-parse --short HEAD > .agent-setup-done\necho "installed 3 packages"\n')
git(seed, 'add', '.'); git(seed, 'commit', '-q', '-m', 'Cart, tax and the agent setup')
const BARE = `${DIR}/git/checkout-app.git`
git(DIR, 'clone', '-q', '--bare', seed, BARE)

let server = null, pool = null, poolKey = null
function startServer() {
  server = spawn('dotnet', [SERVER], { cwd: `${REPO}/src/CoWork.Server`, env: { ...process.env, COWORK_DB: DB, ASPNETCORE_URLS: BASE }, stdio: ['ignore', 'pipe', 'pipe'], detached: true })
  server.log = ''; server.stdout.on('data', d => server.log += d); server.stderr.on('data', d => server.log += d)
  return until('the server to listen', async () => { try { return (await fetch(BASE + '/api/setup/status')).ok } catch { return false } }, 30000)
}
const stopGroup = (p) => new Promise(res => { if (!p || p.exitCode !== null) return res(); p.once('exit', res); try { process.kill(-p.pid, 'SIGTERM') } catch { return res() } setTimeout(res, 8000) })
const POOL_ENV = { ...process.env, COWORK_RUNNER_CONFIG: `${DIR}/pool/config`, COWORK_RUNNER_DATA: `${DIR}/pool/data` }
function startPool(args) {
  pool = spawn('dotnet', [RUNNER, ...args], { env: POOL_ENV, stdio: ['ignore', 'pipe', 'pipe'], detached: true })
  pool.log = ''
  pool.stdout.on('data', d => { pool.log += d; process.stdout.write('  [pool] ' + d) }); pool.stderr.on('data', d => { pool.log += d; process.stdout.write('  [pool!] ' + d) })
}
function cleanupPodman() {
  if (!poolKey) return
  for (const id of podman('ps', '-aq', '--filter', `label=cowork.pool=${poolKey}`).split('\n').filter(Boolean)) podman('rm', '-f', id)
  for (const v of podman('volume', 'ls', '-q', '--filter', `label=cowork.pool=${poolKey}`).split('\n').filter(Boolean)) podman('volume', 'rm', v)
}

const browser = await launch()
const A = await person(browser), R = await person(browser), S = await person(browser)
const a = A.page, r = R.page, s = S.page
const shot = async (p, name) => { await p.waitForTimeout(400); await p.screenshot({ path: SHOTS + name + '.png' }) }
const shots = async (p, name) => {
  for (const theme of ['dark', 'light']) { await p.evaluate(t => { localStorage.setItem('weave:theme', t); document.documentElement.dataset.theme = t; document.documentElement.style.colorScheme = t }, theme); await shot(p, `${name}-${theme}`) }
  await p.evaluate(() => { localStorage.setItem('weave:theme', 'dark'); document.documentElement.dataset.theme = 'dark'; document.documentElement.style.colorScheme = 'dark' })
}
async function join(p, handle, name) {
  const code = await a.evaluate(async () => (await (await fetch('/api/invites', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json()).code)
  await p.goto(`${BASE}/invite/${code}`)
  await p.fill('[name=username]', handle); await p.fill('[name=displayName]', name); await p.fill('[name=password]', `${handle}-password`); await p.click('button[type=submit]')
  await p.waitForSelector('[data-channel=general]')
}
const events = (type, thread) => sql(`select count(*) from events where type='${type}'${thread ? ` and stream_id='thread-${thread}'` : ''}`)[0][0]
const threadOf = (text) => until('the thread', () => sql(`select root_id from threads t join posts p on p.id=t.root_id where p.body like '%${text}%'`)[0]?.[0])
const sandboxOf = (thread) => sql(`select id, state from sandboxes where thread_id=${thread}`)[0]
const container = (thread) => `cowork-${poolKey}-sb-${sandboxOf(thread)[0]}`
const running = () => podman('ps', '--filter', `label=cowork.pool=${poolKey}`, '--format', '{{.Names}}').split('\n').filter(Boolean)
const idleIn = (p) => p.waitForFunction(() => /idle/.test(document.querySelector('[data-testid=thread-panel] [data-testid=agent-status]')?.textContent ?? ''), null, { timeout: LONG })
const toBottom = (p) => p.evaluate(() => { const el = document.querySelector('[data-testid=thread-panel] .thin-scrollbar'); if (el) el.scrollTop = el.scrollHeight })
// the pictures show an invented repository path, not this machine's
const mask = (p) => p.evaluate((bare) => { for (const c of document.querySelectorAll('code')) if (c.textContent === bare) c.textContent = '/srv/git/checkout-app.git' }, BARE)
const answer = (thread) => sql(`select group_concat(body, ' ') from posts where thread_id=${thread} and kind='text' and author_id=(select id from members where handle='researcher')`)[0][0] ?? ''

try {
  await startServer()
  await a.goto(BASE)
  await a.fill('[name=serverName]', 'Lumen Labs'); await a.fill('[name=username]', 'alex'); await a.fill('[name=displayName]', 'Alex'); await a.fill('[name=password]', 'correct horse')
  await a.click('button[type=submit]'); await a.waitForSelector('[data-channel=general]')
  await join(r, 'rosa', 'Rosa'); await join(s, 'sam', 'Sam')
  // round 1's setup, quickly: a pool, Copilot, a minute's idle time, the researcher template in Checkout app
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-where]'); await a.click('[data-testid=add-pool]')
  const command = clean(await a.textContent('[data-testid=pool-command]'))
  startPool([...command.split(' ').slice(1), '--host', 'build-02'])
  await a.waitForSelector('[data-testid=pool-connected]', { timeout: 30000 }); await a.keyboard.press('Escape')
  poolKey = /labels cowork\.pool=(\S+);/.exec(pool.log)?.[1]
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-server-settings]')
  await a.click('[data-testid=provider-copilot]'); await a.click('[data-testid=save-models]'); await a.waitForSelector('[data-testid=models-saved]')
  await a.fill('[name=idleSeconds]', '90'); await a.click('[data-testid=save-idle]'); await a.waitForSelector('[data-testid=idle-saved]'); await a.keyboard.press('Escape')
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-invite-agent]'); await a.click('[data-testid=runs-on-pool]')
  await a.fill('[name=handle]', 'researcher'); await a.fill('[name=about]', 'Reads the code and the web, and compares options')
  await a.fill('[name=instructions]', 'You are researcher, on the Lumen Labs checkout app. Answer in at most two short sentences. Read files with your read tool. To read a web page, use your webfetch tool. If a page is refused, say so in one sentence and stop; when you are told a host is allowed, fetch it again.')
  await a.click('[data-testid=create-agent]'); await a.waitForSelector('[data-testid=template-created]'); await a.keyboard.press('Escape')
  await a.click('[data-testid=new-project]')
  await a.fill('[name=projectName]', 'Checkout app'); await a.fill('[name=about]', 'Cart, payment and order confirmation')
  await a.click('[data-testid=start-repository]'); await a.fill('[name=repoUrl]', BARE); await a.fill('[name=branch]', 'main')
  for (const who of ['Rosa', 'Sam']) { await a.click('[data-testid=add-person]'); await a.click(`[role=option]:has-text("${who}")`) }
  await a.click('[data-testid=create-project]')
  await a.waitForSelector('[data-testid=project-settings-page] [data-testid=project-title]:has-text("Checkout app")')
  await a.click('[data-testid=add-agent]'); await a.waitForSelector('[data-testid=boundary] [data-line=stops]'); await a.click('[data-testid=add-agent-confirm]')
  await a.waitForSelector('[data-agent-row=researcher]')
  const projectId = Number(a.url().split('/').pop())
  ok('round 1 as before: pool-01 online, Copilot through the gateway, the researcher template enabled in Checkout app')

  // --- 1. the project's network list, setup command and budget, in its settings ---
  for (const h of ['github.com', 'registry.npmjs.org']) { await a.fill('[name=networkHost]', h); await a.click('[data-testid=add-host]'); await a.waitForSelector(`[data-testid=network-list] [data-host="${h}"]`) }
  await a.fill('[name=setupCommand]', 'sh scripts/agent-setup.sh'); await a.click('[data-testid=save-setup]'); await a.waitForSelector('[data-testid=setup-saved]')
  await a.fill('[name=monthTokens]', '2000000'); await a.fill('[name=threadTokens]', '200000'); await a.click('[data-testid=save-budget]'); await a.waitForSelector('[data-testid=budget-saved]')
  expect(sql(`select group_concat(host) from (select host from project_network where project_id=${projectId} order by host)`)[0][0] === 'github.com,registry.npmjs.org'
    && sql(`select setup_command from projects where id=${projectId}`)[0][0] === 'sh scripts/agent-setup.sh'
    && sql(`select month_tokens, thread_tokens from budgets where project_id=${projectId}`)[0].join() === '2000000,200000',
    "Checkout app's settings: network github.com and registry.npmjs.org, setup sh scripts/agent-setup.sh, 2,000,000 tokens a month and 200,000 a thread")
  expect(clean(await a.textContent('[data-testid=budget-used]')) === '0 tokens of 2,000,000 tokens' && clean(await a.textContent('[data-testid=project-budget]')).includes('reports no cost'),
    'the Budget card counts tokens (GitHub Copilot reports no cost): 0 of 2,000,000 so far')

  // --- 2. Rosa asks for a page off the list: the setup runs first, the proxy refuses example.com, and Rosa is asked ---
  for (const p of [r, s, a]) { await p.click('[data-channel=checkout-app]'); await p.waitForSelector('[data-testid=channel-title]:has-text("checkout-app")') }
  await r.fill('[data-testid=composer]', '@researcher Fetch https://example.com with your webfetch tool and tell me the page title.')
  await r.keyboard.press('Enter')
  const rosa = await threadOf('Fetch https://example.com')
  await r.waitForSelector('[data-testid=thread-panel] [data-testid=tool-row][data-setup=true]', { timeout: LONG })
  await r.click('[data-testid=thread-panel] [data-testid=tool-row][data-setup=true] summary')
  const setupOut = clean(await r.textContent('[data-testid=thread-panel] [data-testid=setup-output]'))
  expect(setupOut.includes('registry.npmjs.org answered 200') && setupOut.includes('installed 3 packages'), 'the setup command ran first, as a tool row with its output; it reached registry.npmjs.org (on the list) through the proxy: ' + setupOut)
  await r.waitForSelector('[data-testid=thread-panel] [data-testid=ask-card] [data-testid=ask-egress]', { timeout: LONG })
  const why = clean(await r.textContent('[data-testid=thread-panel] [data-testid=ask-why]'))
  expect(clean(await r.textContent('[data-testid=thread-panel] [data-testid=ask-egress]')).startsWith('researcher wants to reach example.com') && why === "It isn't on Checkout app's network list. Asked by researcher, for Rosa's thread.",
    'the proxy refused example.com; Rosa gets the ask: "researcher wants to reach example.com. ' + why + '"')
  await a.goto(`${BASE}/c/${sql("select id from channels where name='checkout-app'")[0][0]}/t/${rosa}`)
  await a.waitForSelector('[data-testid=thread-panel] [data-testid=ask-waiting]')
  expect(clean(await a.textContent('[data-testid=thread-panel] [data-testid=ask-waiting]')).startsWith('researcher is waiting for Rosa') && await a.locator('[data-testid=thread-panel] [data-answer=once]').count() === 0,
    'Alex (owner and lead) sees "researcher is waiting for Rosa", with no buttons')
  await shot(a, '11-ask-waiting-dark')
  await toBottom(r); await shots(r, '10-egress-ask')
  await idleIn(r)                                    // the refused turn ended; Rosa answers the card
  await r.click('[data-testid=thread-panel] [data-answer=once]')
  await until("researcher's retry to end", () => events('TurnEnded', rosa) >= 2, LONG)
  const tried = sql(`select json_extract(data,'$.text') from events where type='MessageDelivered' and stream_id='thread-${rosa}' and json_extract(data,'$.system')=1`)[0]?.[0]
  expect(tried === `<cowork-system thread="${rosa}">Rosa allowed example.com once. Try again.</cowork-system>`, 'the agent is told in its session: ' + tried)
  expect(/example domain/i.test(answer(rosa)), 'researcher fetched it the second time: "' + answer(rosa).slice(-120) + '"')
  await r.waitForSelector('[data-testid=thread-panel] [data-testid=ask-card]:has-text("Rosa chose “Allow once”")')
  await idleIn(r); await toBottom(r); await shots(r, '12-allowed-once')
  expect(sql(`select count(*) from events where type='SandboxStarted' and stream_id='thread-${rosa}'`)[0][0] === 1 && events('ToolCallStarted', rosa) >= 2
    && sql(`select count(*) from events where type='ToolCallStarted' and stream_id='thread-${rosa}' and json_extract(data,'$.toolId')='cowork-setup'`)[0][0] === 1,
    'the setup ran once in the thread, though it had two turns')

  // --- 3. a thread at its cap stops cleanly and says why; Alex raises it ---
  await a.goto(`${BASE}/p/${projectId}`); await a.waitForSelector('[data-testid=project-budget]')
  await a.fill('[name=threadTokens]', '10000'); await a.click('[data-testid=save-budget]'); await a.waitForSelector('[data-testid=budget-saved]')
  await s.fill('[data-testid=composer]', '@researcher Read README.md, then src/tax.ts, then scripts/agent-setup.sh, one at a time, and summarise all three.')
  await s.keyboard.press('Enter')
  const sam = await threadOf('Read README.md, then src/tax.ts')
  await s.waitForSelector('[data-testid=thread-panel] [data-testid=notice][data-budget=thread]', { timeout: LONG })
  const stop = clean(await s.textContent('[data-testid=thread-panel] [data-testid=notice][data-budget=thread]'))
  expect(stop === "researcher: Reached this thread's cap of 10,000 tokens. A lead can raise it.", 'the thread says why it stopped: ' + stop)
  await until("Sam's turn to end", () => sql(`select count(*) from open_turns where thread_id=${sam}`)[0][0] === 0 && events('TurnEnded', sam) + events('TurnFailed', sam) >= 1, LONG)
  const used = sql(`select sum(prompt_tokens+completion_tokens) from model_usage where thread_id=${sam}`)[0][0]
  expect(used >= 10000 && events('BudgetReached', sam) === 1 && sql(`select count(*) from posts where thread_id=${sam} and kind='notice'`)[0][0] === 1, `the turn ended there: no turn left open, ${used.toLocaleString()} tokens used, one notice (the harness's own error about it isn't repeated)`)
  expect(await s.locator('[data-testid=thread-panel] [data-testid=raise-cap]').count() === 0, 'Sam (a member) has no Raise button')
  await idleIn(s); await toBottom(s); await shots(s, '13-budget-stop')
  await a.goto(`${BASE}/c/${sql("select id from channels where name='checkout-app'")[0][0]}/t/${sam}`)
  await a.waitForSelector('[data-testid=thread-panel] [data-testid=raise-cap]')
  await toBottom(a); await shot(a, '13-raise-cap-dark')
  await a.click('[data-testid=thread-panel] [data-testid=raise-cap]')
  await a.waitForSelector('[data-testid=thread-panel] [data-testid=notice]:has-text("raised this thread\'s cap to 20,000 tokens.")')
  ok('Alex, a lead, raised it: "Alex: raised this thread\'s cap to 20,000 tokens."')
  await a.goto(`${BASE}/p/${projectId}`); await a.waitForSelector('[data-testid=project-budget]')
  await a.fill('[name=threadTokens]', '200000'); await a.click('[data-testid=save-budget]'); await a.waitForSelector('[data-testid=budget-saved]')
  const monthUsed = clean(await a.textContent('[data-testid=budget-used]'))
  expect(/^[\d,]+ tokens of 2,000,000 tokens$/.test(monthUsed) && !monthUsed.startsWith('0 '), "the Budget card shows this month's real use: " + monthUsed)
  await mask(a); await shots(a, '14-sandboxes-and-budget')

  // --- 4. the container dies mid-turn: it's started again once on its volume and the turn finishes ---
  await r.fill('[data-testid=thread-panel] [data-testid=composer]', '@researcher Now read README.md, then src/tax.ts, then scripts/agent-setup.sh, one at a time, and tell me what each is for.')
  await r.keyboard.press('Enter')
  const toolsBefore = events('ToolCallStarted', rosa)
  await until('a tool call in the new turn', () => events('ToolCallStarted', rosa) > toolsBefore, LONG)
  const box = container(rosa)
  podman('kill', box)
  ok('killed ' + box.replace(poolKey, '<pool>') + ' while researcher was reading')
  await r.waitForSelector('[data-testid=thread-panel] [data-testid=notice]:has-text("researcher\'s sandbox stopped unexpectedly. Trying again.")', { timeout: 60000 })
  await until('the sandbox to resume', () => events('SandboxResumed', rosa) >= 1, 90000)
  await until('the turn to finish after the restart', () => events('TurnEnded', rosa) >= 3, LONG)
  const logs = podman('logs', box)
  expect(/loaded session/.test(logs) && events('TurnFailed', rosa) === 0, 'it resumed on the same volume, the runner loaded the session, and the turn finished: "' + answer(rosa).slice(-100) + '"')
  expect(sql(`select count(*) from posts where thread_id=${rosa} and kind='tool' and json_extract(meta,'$.status')='running'`)[0][0] === 0, 'no tool row is left running from the killed turn')
  await idleIn(r); await toBottom(r); await shots(r, '15-restarted')

  // --- 5. Pause all agents: the sandbox stops, Rosa's next message waits; resume and it's answered ---
  await a.goto(`${BASE}/p/${projectId}`); await a.waitForSelector('[data-testid=pause-agents]')
  expect(running().includes(box), "Rosa's sandbox is running before the pause")
  await a.click('[data-testid=pause-agents]'); await a.click('[data-testid=confirm-action]')
  await a.waitForSelector('[data-testid=paused-banner]')
  await until('the sandbox to stop', () => !running().includes(box) && sandboxOf(rosa)[1] === 'suspended', 60000)
  expect(clean(await a.textContent('[data-testid=paused-banner]')).startsWith('Agents paused by Alex.'), 'Alex pressed Pause all agents: the banner says so and the sandbox stopped (volume kept)')
  await mask(a); await shots(a, '16-paused')
  await r.fill('[data-testid=thread-panel] [data-testid=composer]', '@researcher One word: what is the function in src/tax.ts called?')
  await r.keyboard.press('Enter')
  await r.waitForFunction(() => /paused by Alex/.test(document.querySelector('[data-testid=thread-panel] [data-testid=agent-status]')?.textContent ?? ''))
  await r.waitForTimeout(4000)                     // nothing may start while paused
  expect(events('SandboxRequested', rosa) === 2 && events('TurnEnded', rosa) === 3, "Rosa's message waits: no sandbox is asked for and no turn starts; her thread says \"paused by Alex\"")
  const footers = await r.$$eval('[data-testid=thread-panel] [data-testid=sandbox-footer]', els => els.map(e => e.textContent.replace(/\s+/g, ' ').trim()))
  expect(footers.at(-1).startsWith("Stopped: Alex paused the project's agents."), 'its sandbox card says why it stopped: ' + footers.at(-1))
  await toBottom(r); await shots(r, '17-paused-thread')
  await a.click('[data-testid=pause-agents]')       // Resume agents
  await until('the waiting message to be answered', () => events('TurnEnded', rosa) >= 4, LONG)
  expect(/roundTax/.test(answer(rosa).split(' ').slice(-20).join(' ')) && events('SandboxResumed', rosa) >= 2, 'resumed: the sandbox came back and answered the message that waited')

  // --- 6. inside the container: no network but the two sockets; a listed host passes, anything else is refused ---
  await until('the sandbox to run', () => running().includes(box), 30000)
  const direct = podmanTry('exec', box, 'curl', '-sS', '-m', '8', '--noproxy', '*', '-o', '/dev/null', '-w', '%{http_code}', 'https://registry.npmjs.org/')
  expect(direct.code !== 0, 'without the proxy it reaches nothing, not even a listed host (no network): ' + clean(direct.out).slice(0, 80))
  const listed = podmanTry('exec', box, 'curl', '-sS', '-m', '20', '-o', '/dev/null', '-w', '%{http_code}', 'https://registry.npmjs.org/')
  expect(listed.out === '200', 'through the proxy a listed host answers: registry.npmjs.org 200')
  const other = podmanTry('exec', box, 'curl', '-sS', '-m', '20', 'https://example.net/')
  expect(other.code !== 0 && /403/.test(other.out), 'any other host is refused by the proxy (403): ' + clean(other.out).slice(0, 80))
  const mounts = podman('inspect', '--format', '{{.HostConfig.NetworkMode}} {{range .Mounts}}{{.Destination}} {{end}}', box)
  expect(mounts.startsWith('none ') && mounts.includes('/run/cowork') && mounts.includes('/work'), 'the container: network none, its volume at /work and the pool\'s sockets at /run/cowork')
  expect(podmanTry('exec', box, 'test', '-f', '/work/repo/.agent-setup-done').code === 0 && podmanTry('exec', box, 'test', '-f', '/work/home/.cowork/setup.json').code === 0,
    "the setup's result stays in the thread's volume, with its marker")
  console.log('PHASE 10 LIVE CHECK PASSED')
} catch (e) {
  console.log('FAILED', e.message)
  for (const [p, n] of [[a, 'alex'], [r, 'rosa'], [s, 'sam']]) await shot(p, 'FAIL-' + n).catch(() => {})
  if (server?.log) console.log(server.log.split('\n').filter(l => /fail|error|exception|warn/i.test(l)).slice(-20).join('\n'))
  process.exitCode = 1
} finally {
  await stopGroup(pool); await stopGroup(server)
  await browser.close()
  try { cleanupPodman() } catch (e) { console.log('cleanup:', e.message) }
  if (poolKey) console.log(`removed every container and volume labelled cowork.pool=${poolKey}`)
}
