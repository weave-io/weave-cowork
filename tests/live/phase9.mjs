// Brief 7, round 1: a sandbox per thread. Browsers for Alex (admin, Checkout app's lead), Rosa and Sam; a REAL pool on this machine
// (`cowork-runner pool connect`, rootless podman), REAL containers from cowork-sandbox-opencode:2.4.1 (deploy/sandbox/build.sh) and REAL
// OpenCode 2.4.1 inside them, whose only model provider is the server's gateway, which forwards to this machine's GitHub Copilot sign-in
// (the user agreed, 25 Nov 2026). The script makes its own small git repository (an invented checkout-app with src/tax.ts).
// The demo: Alex adds a pool (pool-01, 2 slots) and sets the models and a short idle time → makes the researcher template and enables it in
// Checkout app → Rosa and Sam ask researcher at the same moment: two containers; Alex's third thread waits for room on pool-01 and starts
// when a slot frees → the starting rows, then the answer with a tool row reading src/tax.ts → idle: the container goes, the volume stays →
// Rosa replies: the same volume, session/load, researcher remembers → inside the container no provider key, no GitHub sign-in, no pool token;
// the gateway recorded each thread's usage. Starts its own server (default port 5884). Everything it makes in podman carries the pool's
// label and is removed by that label at the end.
//   node tests/live/phase9.mjs          (screenshots go to docs/screens/brief7/)
import { launch, person, expect, ok, SANDBOX_IMAGE } from './lib.mjs'
import { spawn, execFileSync } from 'node:child_process'
import { homedir } from 'node:os'
import { rmSync, mkdirSync, writeFileSync, readFileSync, existsSync } from 'node:fs'

const HOME = homedir()
const PORT = process.env.PORT ?? '5884'
const BASE = `http://127.0.0.1:${PORT}`
const REPO = new URL('../../', import.meta.url).pathname.replace(/\/$/, '')
const DIR = `${HOME}/.cache/cowork-p9`
const DB = `${DIR}/data/cowork.db`
const SHOTS = process.env.SHOTS_DIR ?? `${REPO}/docs/screens/brief7/`
const SERVER = `${REPO}/src/CoWork.Server/bin/Debug/net10.0/CoWork.Server.dll`
const RUNNER = `${REPO}/src/CoWork.Runner/bin/Debug/net10.0/cowork-runner.dll`
const IMAGE = SANDBOX_IMAGE              // never the trial's own tag (see lib.mjs)
const IDLE = Number(process.env.IDLE ?? 25)
const LONG = 240000
const SIGN_IN = `${HOME}/.local/share/opencode/auth.json`
rmSync(DIR, { recursive: true, force: true }); mkdirSync(`${DIR}/data`, { recursive: true }); mkdirSync(SHOTS, { recursive: true })
const sql = (q) => JSON.parse(execFileSync('python3', ['-I', '-c', 'import sqlite3,sys,json;c=sqlite3.connect(sys.argv[1]);print(json.dumps(c.execute(sys.argv[2]).fetchall()))', DB, q]).toString())
const until = async (what, probe, ms = 60000) => { const end = Date.now() + ms; for (;;) { const v = await probe(); if (v) return v; if (Date.now() > end) throw new Error('FAILED: timed out waiting for ' + what); await new Promise(r => setTimeout(r, 200)) } }
const clean = (t) => (t ?? '').replace(/\s+/g, ' ').trim()
const podman = (...a) => execFileSync('podman', a, { stdio: ['ignore', 'pipe', 'pipe'] }).toString().trim()
const git = (cwd, ...args) => execFileSync('git', args, { cwd, env: { ...process.env, GIT_AUTHOR_NAME: 'Alex', GIT_AUTHOR_EMAIL: 'alex@lumen.example', GIT_COMMITTER_NAME: 'Alex', GIT_COMMITTER_EMAIL: 'alex@lumen.example' } }).toString().trim()

if (!podman('images', '-q', IMAGE)) { console.log(`FAILED: the image ${IMAGE} isn't built here. Run TAG=${IMAGE} deploy/sandbox/build.sh first.`); process.exit(1) }
const copilot = (() => { try { return JSON.parse(readFileSync(SIGN_IN, 'utf8'))['github-copilot'] } catch { return null } })()
if (!copilot) { console.log('FAILED: this machine has no GitHub Copilot sign-in in OpenCode; the gateway needs a provider.'); process.exit(1) }
const GITHUB_TOKEN = copilot.refresh || copilot.access          // only to prove it never reaches a sandbox; never printed

// --- the invented repository: a bare checkout-app with README.md and src/tax.ts on main ---
const seed = `${DIR}/seed`; mkdirSync(`${seed}/src`, { recursive: true })
git(seed, 'init', '-q', '-b', 'main')
writeFileSync(`${seed}/README.md`, '# checkout-app\n\nCart, payment and order confirmation for the Lumen Labs web shop.\n')
writeFileSync(`${seed}/src/tax.ts`, '// Tax for one order line, in euros.\nexport function roundTax(amount: number): number {\n  return Math.round(amount * 100) / 100\n}\n')
git(seed, 'add', '.'); git(seed, 'commit', '-q', '-m', 'Cart and tax')
const BARE = `${DIR}/git/checkout-app.git`
git(DIR, 'clone', '-q', '--bare', seed, BARE)

let server = null, pool = null, poolKey = null
function startServer() {
  // the server reads this machine's Copilot sign-in (read only) for the gateway; HOME is the real one for that
  server = spawn('dotnet', [SERVER], { cwd: `${REPO}/src/CoWork.Server`, env: { ...process.env, COWORK_DB: DB, ASPNETCORE_URLS: BASE }, stdio: ['ignore', 'pipe', 'pipe'], detached: true })
  server.log = ''; server.stdout.on('data', d => server.log += d); server.stderr.on('data', d => server.log += d)
  return until('the server to listen', async () => { try { return (await fetch(BASE + '/api/setup/status')).ok } catch { return false } }, 30000)
}
const stopGroup = (p) => new Promise(res => { if (!p || p.exitCode !== null) return res(); p.once('exit', res); try { process.kill(-p.pid, 'SIGTERM') } catch { return res() } setTimeout(res, 8000) })
// the pool keeps its config and mirrors in this run's folder, and uses this user's podman (its images and settings stay where they are)
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
const json = (p, path, init) => p.evaluate(async ([u, i]) => { const r = await fetch(u, i); return { status: r.status, body: r.ok ? await r.json() : null } }, [path, init])
async function join(p, handle, name) {
  const code = await a.evaluate(async () => (await (await fetch('/api/invites', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json()).code)
  await p.goto(`${BASE}/invite/${code}`)
  await p.fill('[name=username]', handle); await p.fill('[name=displayName]', name); await p.fill('[name=password]', `${handle}-password`); await p.click('button[type=submit]')
  await p.waitForSelector('[data-channel=general]')
}
const sandboxOf = (thread) => sql(`select id, state from sandboxes where thread_id=${thread}`)[0]
const container = (id) => `cowork-${poolKey}-sb-${id}`
const running = () => podman('ps', '--filter', `label=cowork.pool=${poolKey}`, '--format', '{{.Names}}').split('\n').filter(Boolean)
const events = (type, thread) => sql(`select count(*) from events where type='${type}'${thread ? ` and stream_id='thread-${thread}'` : ''}`)[0][0]

try {
  await startServer()
  await a.goto(BASE)
  await a.fill('[name=serverName]', 'Lumen Labs'); await a.fill('[name=username]', 'alex'); await a.fill('[name=displayName]', 'Alex'); await a.fill('[name=password]', 'correct horse')
  await a.click('button[type=submit]'); await a.waitForSelector('[data-channel=general]')
  await join(r, 'rosa', 'Rosa'); await join(s, 'sam', 'Sam')
  ok('Alex (admin) set up Lumen Labs; Rosa and Sam joined')

  // --- 1. Where agents run → Add a pool: a one-time code and the command; the real pool enrols and pool-01 shows online with 2 slots ---
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-where]')
  await a.click('[data-testid=add-pool]')
  const command = clean(await a.textContent('[data-testid=pool-command]'))
  expect(/^cowork-runner pool connect http:\/\/127\.0\.0\.1:\d+ --code [A-Z0-9]{4}-[A-Z0-9]{4} --slots 2 --memory 1500M$/.test(command), 'the dialog gives the command: ' + command.replace(/--code \S+/, '--code …'))
  startPool([...command.split(' ').slice(1), '--host', 'build-02'])
  await a.waitForSelector('[data-testid=pool-connected]', { timeout: 30000 })
  expect(clean(await a.textContent('[data-pool=pool-01] [data-testid=pool-state]')) === 'online' && clean(await a.textContent('[data-pool=pool-01] [data-testid=pool-slots]')).startsWith('0 of 2 slots busy · 1500 MB each'),
    'pool-01 shows online with 2 slots (0 of 2 busy, 1500 MB each), on build-02')
  poolKey = /labels cowork\.pool=(\S+);/.exec(pool.log)?.[1]
  const serverId = sql("select value from settings where key='server_id'")[0][0]
  expect(poolKey === `${serverId.slice(0, 8)}-p1`, `the pool labels what it makes cowork.pool=${poolKey} (the server's id and the pool's)`)
  const poolToken = JSON.parse(readFileSync(`${DIR}/pool/config/pools.json`, 'utf8'))[0].token
  expect(sql('select count(*) from pool_secrets where token_hash is not null')[0][0] === 1 && !sql("select group_concat(data) from events")[0][0].includes(poolToken), 'the pool token is stored as a hash only; no event holds it')
  await shots(a, '1-where-agents-run')
  await a.keyboard.press('Escape')

  // --- 2. Server settings → Models: GitHub Copilot signed in on this machine; a short idle time for the check ---
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-server-settings]')
  await a.click('[data-testid=provider-copilot]'); await a.click('[data-testid=save-models]'); await a.waitForSelector('[data-testid=models-saved]')
  await a.fill('[name=idleSeconds]', String(IDLE)); await a.click('[data-testid=save-idle]'); await a.waitForSelector('[data-testid=idle-saved]')
  expect(sql("select value from settings where key='model_provider'")[0][0] === 'github-copilot' && sql("select value from settings where key='sandbox_idle_seconds'")[0][0] === String(IDLE),
    `Server settings: models from GitHub Copilot on this machine, sandboxes stop after ${IDLE} s idle`)
  await shots(a, '2-server-settings')
  await a.keyboard.press('Escape')

  // --- 3. a team template, researcher, on a pool; Checkout app from the repository with Rosa and Sam; researcher enabled there ---
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-invite-agent]')
  await a.click('[data-testid=runs-on-pool]')
  await a.fill('[name=handle]', 'researcher'); await a.fill('[name=about]', 'Reads the code and the web, and compares options')
  await a.fill('[name=instructions]', 'You are researcher, on the Lumen Labs checkout app. Answer in at most two short sentences. Read files with your read tool; never run shell commands.')
  expect(await a.inputValue('[name=image]') === 'cowork-sandbox-opencode:2.4.1' && await a.inputValue('[name=model]') === 'claude-haiku-4.5' && await a.inputValue('[name=harness]') === 'opencode acp',
    'Runs on: a pool, with the image cowork-sandbox-opencode:2.4.1, opencode acp and claude-haiku-4.5 through the gateway')
  await a.fill('[name=image]', IMAGE)            // the check's own build of the image, never the trial's tag
  await shots(a, '3-new-template')
  await a.click('[data-testid=create-agent]'); await a.waitForSelector('[data-testid=template-created]')
  expect(sql("select placement, image, model from agents a join members m on m.id=a.member_id where m.handle='researcher'")[0].join() === `pool,${IMAGE},claude-haiku-4.5`
    && sql("select count(*) from runner_secrets rs join members m on m.id=rs.agent_id where m.handle='researcher'")[0][0] === 0, 'researcher is a template: no runner, no enrolment code')
  await a.keyboard.press('Escape')
  await a.click('[data-testid=new-project]')
  await a.fill('[name=projectName]', 'Checkout app'); await a.fill('[name=about]', 'Cart, payment and order confirmation')
  await a.click('[data-testid=start-repository]'); await a.fill('[name=repoUrl]', BARE); await a.fill('[name=branch]', 'main')
  for (const who of ['Rosa', 'Sam']) { await a.click('[data-testid=add-person]'); await a.click(`[role=option]:has-text("${who}")`) }
  await a.click('[data-testid=create-project]')
  await a.waitForSelector('[data-testid=project-settings-page] [data-testid=project-title]:has-text("Checkout app")')
  await a.click('[data-testid=add-agent]')
  await a.waitForSelector('[data-testid=boundary] [data-line=stops]')
  const line = Object.fromEntries(await a.$$eval('[data-testid=boundary] [data-line]', els => els.map(e => [e.dataset.line, e.textContent.replace(/\s+/g, ' ').trim()])))
  expect(line.worksOn.includes('pool-01 keeps a mirror') && line.asks.startsWith('Whoever started the thread; Alex') && line.spends.includes('gateway') && line.stops.includes(`after ${IDLE} seconds idle`),
    "the boundary of a template: pool-01's mirror and a copy per thread, asks whoever started the thread then Alex, the gateway, stops when idle")
  expect(await a.locator('[data-testid=boundary-note]').count() === 1 && await a.locator('[data-testid=boundary-warning]').count() === 0, 'sandboxed: a note that model keys stay in the gateway, no "unsandboxed" warning')
  await a.evaluate((bare) => { for (const c of document.querySelectorAll('[data-testid=boundary] code')) if (c.textContent === bare) c.textContent = '/srv/git/checkout-app.git' }, BARE)
  await shots(a, '4-add-template')
  await a.click('[data-testid=add-agent-confirm]')
  await a.waitForSelector('[data-agent-row=researcher]')
  expect(clean(await a.textContent('[data-agent-row=researcher] [data-testid=agent-runs]')) === 'pool-01, a sandbox per thread' && clean(await a.textContent('[data-agent-row=researcher] [data-testid=sandbox-pill]')) === 'sandboxed',
    'Checkout app\'s agents: researcher runs on "pool-01, a sandbox per thread", sandboxed')
  await a.evaluate((bare) => { for (const c of document.querySelectorAll('code')) if (c.textContent === bare) c.textContent = '/srv/git/checkout-app.git' }, BARE)
  await shots(a, '9-project-agents')

  // --- 4 and 5. Rosa and Sam ask researcher at the same moment: two containers. Alex's third thread waits for room on pool-01 ---
  for (const p of [r, s, a]) { await p.click('[data-channel=checkout-app]'); await p.waitForSelector('[data-testid=channel-title]:has-text("checkout-app")') }
  await r.fill('[data-testid=composer]', '@researcher What is the function in src/tax.ts called, and how does it round? Read the file first.')
  await s.fill('[data-testid=composer]', '@researcher What does README.md say the checkout app is for? Read the file first.')
  await Promise.all([r.keyboard.press('Enter'), s.keyboard.press('Enter')])
  await Promise.all([r.waitForSelector('[data-testid=thread-panel] [data-testid=sandbox-card]'), s.waitForSelector('[data-testid=thread-panel] [data-testid=sandbox-card]')])
  await shot(r, '5-thread-starting-dark')
  await until('both sandboxes placed', () => events('SandboxPlaced') >= 2)
  await a.fill('[data-testid=composer]', '@researcher Which files are in src/? List them.')
  await a.keyboard.press('Enter')
  await a.waitForSelector('[data-testid=thread-panel] [data-testid=sandbox-card][data-state=waiting]')
  const waitingText = clean(await a.textContent('[data-testid=thread-panel] [data-step=pool]'))
  expect(waitingText.startsWith('Waiting for room on pool-01 (2 of 2 busy), 1st in line'), 'the third thread says: ' + waitingText)
  expect(clean(await a.textContent('[data-testid=thread-panel] [data-testid=agent-status]')) === 'waiting for room', 'its session line says "waiting for room"')
  await shots(a, '6-waiting-for-room')
  const threadOf = (handle) => sql(`select t.root_id from threads t join members m on m.id=t.started_by where m.handle='${handle}'`)[0][0]      // Rosa and Sam posted at the same moment: either may be first
  const [rosaThread, samThread, alexThread] = ['rosa', 'sam', 'alex'].map(threadOf)
  await until('two sandboxes running at once', () => running().length === 2, 90000)
  expect(running().sort().join() === [container(sandboxOf(rosaThread)[0]), container(sandboxOf(samThread)[0])].sort().join(), 'two containers run side by side, one per thread: ' + running().join(', '))
  const limits = podman('inspect', '--format', '{{.HostConfig.Memory}} {{index .Config.Labels "cowork.thread"}}', container(sandboxOf(rosaThread)[0]))
  expect(limits === `${1500 * 1024 * 1024} ${rosaThread}`, "Rosa's container has the 1500 MB limit and carries her thread's label")

  // the starting rows, then the answer streams with tool rows; it read src/tax.ts from the project's repository
  await until("Rosa's and Sam's turns to end", () => events('TurnEnded', rosaThread) >= 1 && events('TurnEnded', samThread) >= 1, LONG)
  const steps = await r.$$eval('[data-testid=thread-panel] [data-testid=sandbox-card] [data-step]', els => els.map(e => [e.dataset.step, e.dataset.stepState, e.textContent.replace(/\s+/g, ' ').trim()]))
  expect(steps.map(x => x[0]).join() === 'pool,container,workspace,harness' && steps.every(x => x[1] === 'done'), "Rosa's starting rows are all done: " + steps.map(x => x[2].replace(/ \d+\.\d s$/, '')).join(' · '))
  expect(steps[0][2].startsWith('pool-01 has room') && steps[1][2].startsWith(`Started a sandbox from ${IMAGE}, 1500 MB`) && /^Made a copy of app on cowork\//.test(steps[2][2]), 'pool picked, sandbox started from the image, a copy of the repository on the thread\'s branch')
  await r.waitForFunction(() => [...document.querySelectorAll('[data-testid=thread-panel] [data-testid=tool-row]')].some(t => /src\/tax\.ts/.test(t.textContent)), null, { timeout: 30000 })
  ok("a tool row reads src/tax.ts (relative to the sandbox's workspace): the repository is really there")
  const rosaAnswer = sql(`select group_concat(body, ' ') from posts where thread_id=${rosaThread} and kind='text' and author_id=(select id from members where handle='researcher')`)[0][0] ?? ''
  expect(/roundTax/.test(rosaAnswer), 'researcher answers from the file: ' + rosaAnswer.slice(0, 140))
  const where = clean(await r.textContent('[data-testid=thread-panel] [data-testid=session-line]'))
  expect(where.includes('team template') && where.includes('sandbox on pool-01') && where.includes('cowork/') && where.includes('started by Rosa'), 'the session line: ' + where.replace(/\s*(idle|working…)$/, ''))
  await r.waitForFunction(() => /idle/.test(document.querySelector('[data-testid=thread-panel] [data-testid=agent-status]')?.textContent ?? ''), null, { timeout: 20000 })
  await shots(r, '7-thread-answer')
  expect(sql(`select count(*) from thread_agents where thread_id=${rosaThread} and place='sandbox' and host='pool-01' and sandboxed=1`)[0][0] === 1, 'the thread\'s workspace: in a sandbox on pool-01')

  // --- 6. idle: a container goes and a slot frees; the third thread starts in it. The volume stays ---
  await until('a sandbox to be suspended after the idle time', () => events('SandboxSuspended') >= 1, (IDLE + 60) * 1000)
  await until("Alex's thread to start in the freed slot", () => events('SandboxStarted', alexThread) >= 1, 90000)
  ok(`after ${IDLE} s idle a sandbox stopped (SandboxSuspended) and the waiting thread started in its slot`)
  await until("both of Rosa's and Sam's sandboxes to be suspended", () => sandboxOf(rosaThread)[1] === 'suspended' && sandboxOf(samThread)[1] === 'suspended', (IDLE + 60) * 1000)
  const rosaBox = container(sandboxOf(rosaThread)[0])
  expect(!running().includes(rosaBox) && podman('volume', 'ls', '-q', '--filter', `label=cowork.pool=${poolKey}`).split('\n').includes(rosaBox),
    "Rosa's container is gone and its volume is kept")
  await until("Alex's turn to end", () => events('TurnEnded', alexThread) >= 1, LONG)
  await a.waitForSelector('[data-testid=thread-panel] [data-testid=sandbox-card] [data-step=pool][data-step-state=done]')
  ok("Alex's third thread got its answer once a slot freed")
  await r.waitForSelector('[data-testid=thread-panel] [data-testid=sandbox-card][data-state=suspended]')
  expect(clean(await r.textContent('[data-testid=thread-panel] [data-testid=sandbox-footer]')).startsWith(`Stopped after ${IDLE} seconds idle. Its volume is kept`), "Rosa's card: stopped after the idle time, volume kept")

  // Rosa replies: the sandbox resumes on the same volume, the runner calls session/load, and researcher remembers what it read
  const readsBefore = sql(`select count(*) from posts where thread_id=${rosaThread} and kind='tool'`)[0][0]
  await r.fill('[data-testid=thread-panel] [data-testid=composer]', '@researcher Without reading anything again: what was the name of the function you read before? One word.')
  await r.keyboard.press('Enter')
  await until("Rosa's sandbox to resume", () => events('SandboxResumed', rosaThread) >= 1, 90000)
  await until("the resumed sandbox's runner to connect", () => events('SandboxConnected', rosaThread) >= 2, 60000)
  expect(running().includes(rosaBox), 'resumed: a new container on the same volume, with the same name ' + rosaBox.replace(poolKey, '<pool>'))

  // --- 7. inside the container: no provider key, no GitHub sign-in, no pool token ---
  const env = podman('exec', rosaBox, 'cat', '/proc/1/environ').split('\0').filter(Boolean)
  const names = env.map(e => e.split('=')[0]).sort()
  expect(names.includes('COWORK_THREAD_TOKEN') && !names.some(n => /GITHUB|COPILOT|OPENROUTER|OPENAI|ANTHROPIC|API_KEY|POOL/i.test(n)), 'its environment names only CoWork\'s settings and the thread token: ' + names.filter(n => n.startsWith('COWORK')).join(', '))
  expect(!env.some(e => e.includes(GITHUB_TOKEN) || e.includes(poolToken)), 'no GitHub token and no pool token in its environment')
  const authFiles = podman('exec', rosaBox, 'sh', '-c', 'find / -xdev \\( -name auth.json -o -name pools.json -o -name agents.json \\) 2>/dev/null; find /work -name auth.json 2>/dev/null; true')
  expect(authFiles === '', 'no sign-in file (auth.json), no pool or runner config anywhere in the container or its volume')
  const volTar = execFileSync('podman', ['volume', 'export', rosaBox], { maxBuffer: 512 * 1024 * 1024 })
  const diff = podman('diff', rosaBox).split('\n').map(l => l.slice(2)).filter(p => p && !p.startsWith('/proc') && !p.startsWith('/sys') && !p.startsWith('/dev') && !p.startsWith('/run'))
  const changed = diff.length ? execFileSync('podman', ['exec', rosaBox, 'sh', '-c', 'for f in "$@"; do [ -f "$f" ] && cat "$f"; done; true', 'sh', ...diff], { maxBuffer: 64 * 1024 * 1024 }) : Buffer.alloc(0)
  const thread = sql(`select token_hash from thread_tokens where thread_id=${rosaThread} and revoked_at is null`)
  expect(!volTar.includes(GITHUB_TOKEN) && !volTar.includes(poolToken) && !changed.includes(GITHUB_TOKEN) && !changed.includes(poolToken),
    `neither the GitHub token nor the pool token is in its volume (${(volTar.length / 1048576).toFixed(1)} MB read) or in the ${diff.length} files it changed outside it`)
  const tok = env.find(e => e.startsWith('COWORK_THREAD_TOKEN=')).split('=')[1]
  const cfg = podman('exec', rosaBox, 'cat', '/work/home/.config/opencode/opencode.json')
  expect(!cfg.includes(tok) && cfg.includes('{env:COWORK_THREAD_TOKEN}') && cfg.includes('/gateway/v1') && thread.length === 1, "OpenCode's config names the gateway and takes the token from the environment by name; the file doesn't hold it")
  const pt = await fetch(`${BASE}/gateway/v1/models`, { headers: { Authorization: `Bearer ${poolToken}` } })
  expect(pt.status === 401, "the pool's token doesn't open the gateway (401)")

  await until("Rosa's second turn to end", () => events('TurnEnded', rosaThread) >= 2, LONG)
  const logs = podman('logs', rosaBox)
  expect(/loaded session/.test(logs), 'the runner in the resumed sandbox called session/load: "' + (/thread \d+: loaded session \S+/.exec(logs)?.[0] ?? '') + '"')
  const second = sql(`select body from posts where thread_id=${rosaThread} and kind='text' and author_id=(select id from members where handle='researcher') order by id desc limit 1`)[0][0]
  const readsAfter = sql(`select count(*) from posts where thread_id=${rosaThread} and kind='tool'`)[0][0]
  expect(/roundTax/.test(second), `researcher remembers: "${second.slice(0, 80)}"${readsAfter === readsBefore ? ' (without reading the file again)' : ` (it used ${readsAfter - readsBefore} tool call(s) too)`}`)
  await r.waitForSelector('[data-testid=thread-panel] [data-testid=sandbox-card][data-resume=true]')
  await r.waitForFunction(() => /idle/.test(document.querySelector('[data-testid=thread-panel] [data-testid=agent-status]')?.textContent ?? ''), null, { timeout: 20000 })
  await shots(r, '8-resumed')

  // the gateway counted every thread's model calls, per turn, for whoever asked
  const usage = sql(`select thread_id, sum(calls), sum(prompt_tokens), sum(completion_tokens) from model_usage group by thread_id order by thread_id`)
  expect(usage.length === 3 && usage.every(u => u[1] > 0 && u[2] > 0 && u[3] > 0), 'the gateway recorded usage for all three threads: ' + usage.map(u => `${u[1]} calls, ${u[2] + u[3]} tokens`).join('; '))
  expect(sql(`select count(distinct correlation) from events where type='ModelUsageRecorded' and stream_id='thread-${rosaThread}'`)[0][0] === 2
    && sql(`select count(*) from events where type='ModelUsageRecorded' and stream_id='thread-${rosaThread}' and on_behalf_of=(select id from members where handle='rosa')`)[0][0] === events('ModelUsageRecorded', rosaThread),
    "Rosa's thread: usage in each of its two turns, on Rosa's behalf")
  expect(!sql("select group_concat(data) from events")[0][0].includes(GITHUB_TOKEN), 'the GitHub token is in no event')
  console.log('PHASE 9 LIVE CHECK PASSED')
} catch (e) {
  console.log('FAILED', e.message)
  for (const [p, n] of [[a, 'alex'], [r, 'rosa'], [s, 'sam']]) await shot(p, 'FAIL-' + n).catch(() => {})
  if (server?.log) console.log(server.log.split('\n').filter(l => /fail|error|exception|warn/i.test(l)).slice(-20).join('\n'))
  process.exitCode = 1
} finally {
  await stopGroup(pool); await stopGroup(server)
  await browser.close()
  // twice: a container start already on its way when the pool stopped can land after the first sweep
  for (const pass of [1, 2]) { try { cleanupPodman() } catch (e) { console.log('cleanup:', e.message) } if (pass === 1) await new Promise(r => setTimeout(r, 5000)) }
  if (poolKey) console.log(`removed every container and volume labelled cowork.pool=${poolKey}`)
}
