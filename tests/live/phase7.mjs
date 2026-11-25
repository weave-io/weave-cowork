// Brief 5: projects, workspaces and an agent's boundary. Browsers for Alex (admin, the project's lead), Rosa and Sam (members) and Jo (not in
// the project), a REAL runner started with --host build-01 and REAL OpenCode 2.4.1 under a fresh scratch home. The script makes its own small
// git repository (a bare repo of an invented checkout-app: README.md and src/tax.ts) and uses its path as the project's repository URL.
// The demo: New project → its channels group in the channel list (Rosa sees it, Jo doesn't) → Alex adds reviewer and reads its boundary
// ("Runs unsandboxed on build-01") → Rosa and Sam mention reviewer at once: two threads side by side, each in its own git worktree on its own
// cowork/<slug> branch, a file written in one isn't in the other → the settings show the workspace, people, agents and rules → reviewer set to
// take work from Alex only: Rosa gets "reviewer only takes work from Alex." instead of a turn. Starts its own server (default port 5883).
//   node tests/live/phase7.mjs          (screenshots go to docs/screens/brief5/)
import { launch, person, expect, ok, freshHarnessHome, MODEL } from './lib.mjs'
import { spawn, execFileSync } from 'node:child_process'
import { homedir } from 'node:os'
import { rmSync, mkdirSync, writeFileSync, existsSync, readlinkSync } from 'node:fs'

const HOME = homedir()
const PORT = process.env.PORT ?? '5883'
const BASE = `http://127.0.0.1:${PORT}`
const REPO = new URL('../../', import.meta.url).pathname.replace(/\/$/, '')
const DIR = `${HOME}/.cache/cowork-p7`
const DB = `${DIR}/data/cowork.db`
const SCRATCH = freshHarnessHome('p7')        // this run's own runner and harness state, deleted at the end
const SHOTS = process.env.SHOTS_DIR ?? `${REPO}/docs/screens/brief5/`
const HOST = 'build-01'
const SERVER = `${REPO}/src/CoWork.Server/bin/Debug/net10.0/CoWork.Server.dll`
const RUNNER = `${REPO}/src/CoWork.Runner/bin/Debug/net10.0/cowork-runner.dll`
const HARNESS = process.env.HARNESS ?? `${HOME}/.weave/harnesses/opencode2/.opencode/bin/opencode acp`
const ENV = SCRATCH.env
const LONG = 180000
rmSync(DIR, { recursive: true, force: true }); mkdirSync(`${DIR}/data`, { recursive: true }); mkdirSync(SHOTS, { recursive: true })
// edits are allowed (the demo writes files in each thread's worktree), shell commands are refused: nothing needs approving in this run
writeFileSync(`${SCRATCH.dir}/config/opencode/opencode.json`, JSON.stringify({ $schema: 'https://opencode.ai/config.json', permission: { edit: 'allow', bash: 'deny' }, ...MODEL }))
const sql = (q) => JSON.parse(execFileSync('python3', ['-I', '-c', 'import sqlite3,sys,json;c=sqlite3.connect(sys.argv[1]);print(json.dumps(c.execute(sys.argv[2]).fetchall()))', DB, q]).toString())
const until = async (what, probe, ms = 40000) => { const end = Date.now() + ms; for (;;) { const v = await probe(); if (v) return v; if (Date.now() > end) throw new Error('FAILED: timed out waiting for ' + what); await new Promise(r => setTimeout(r, 150)) } }
const clean = (t) => (t ?? '').replace(/\s+/g, ' ').trim()
const git = (cwd, ...args) => execFileSync('git', args, { cwd, env: { ...process.env, GIT_AUTHOR_NAME: 'Alex', GIT_AUTHOR_EMAIL: 'alex@lumen.example', GIT_COMMITTER_NAME: 'Alex', GIT_COMMITTER_EMAIL: 'alex@lumen.example' } }).toString().trim()

// --- the invented repository: a bare checkout-app with README.md and src/tax.ts on main ---
const seed = `${SCRATCH.dir}/seed`; mkdirSync(`${seed}/src`, { recursive: true })
git(seed, 'init', '-q', '-b', 'main')
writeFileSync(`${seed}/README.md`, '# checkout-app\n\nCart, payment and order confirmation for the Lumen Labs web shop.\n')
writeFileSync(`${seed}/src/tax.ts`, 'export function roundTax(amount: number): number {\n  return Math.round(amount * 100) / 100\n}\n')
git(seed, 'add', '.'); git(seed, 'commit', '-q', '-m', 'Cart and tax')
const BARE = `${SCRATCH.dir}/git/checkout-app.git`
git(SCRATCH.dir, 'clone', '-q', '--bare', seed, BARE)

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
const A = await person(browser), R = await person(browser), S = await person(browser), J = await person(browser)
const a = A.page, r = R.page, s = S.page, j = J.page
const shot = async (p, name) => { await p.waitForTimeout(500); await p.screenshot({ path: SHOTS + name + '.png' }) }
/** the same screen in the default dark theme and in light */
const shots = async (p, name) => {
  for (const theme of ['dark', 'light']) { await p.evaluate(t => { localStorage.setItem('weave:theme', t); document.documentElement.dataset.theme = t; document.documentElement.style.colorScheme = t }, theme); await shot(p, `${name}-${theme}`) }
  await p.evaluate(() => { localStorage.setItem('weave:theme', 'dark'); document.documentElement.dataset.theme = 'dark'; document.documentElement.style.colorScheme = 'dark' })
}
const json = (p, path) => p.evaluate(async (u) => { const r = await fetch(u); return { status: r.status, body: r.ok ? await r.json() : null } }, path)
async function join(p, handle, name) {
  const code = await a.evaluate(async () => (await (await fetch('/api/invites', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json()).code)
  await p.goto(`${BASE}/invite/${code}`)
  await p.fill('[name=username]', handle); await p.fill('[name=displayName]', name); await p.fill('[name=password]', `${handle}-password`); await p.click('button[type=submit]')
  await p.waitForSelector('[data-channel=general]')
}
try {
  await startServer()

  // --- Alex sets up Lumen Labs; Rosa, Sam and Jo join; Alex brings his own agent, reviewer, on his runner on build-01 ---
  await a.goto(BASE)
  await a.fill('[name=serverName]', 'Lumen Labs'); await a.fill('[name=username]', 'alex'); await a.fill('[name=displayName]', 'Alex'); await a.fill('[name=password]', 'correct horse')
  await a.click('button[type=submit]'); await a.waitForSelector('[data-channel=general]')
  await join(r, 'rosa', 'Rosa'); await join(s, 'sam', 'Sam'); await join(j, 'jo', 'Jo')
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-invite-agent]')
  await a.fill('[name=handle]', 'reviewer'); await a.fill('[name=about]', 'Reviews and fixes the checkout app')
  await a.fill('[name=instructions]', 'You are reviewer, a careful engineer on the Lumen Labs checkout app. Answer in at most two short sentences. You cannot run shell commands; to create a file, use your write tool with a path relative to the current folder.')
  await a.fill('[name=harness]', HARNESS); await a.fill('[name=workspace]', `${SCRATCH.dir}/own-workspace`)
  await a.click('button:has-text("Create agent")')
  await a.waitForSelector('[data-testid=runner-command]')
  const parts = (await a.textContent('[data-testid=runner-command]')).trim().split(' ')
  startRunner(['connect', BASE, '--code', parts[parts.length - 1], '--host', HOST])
  await a.waitForSelector('[data-testid=agent-connected]', { timeout: 30000 })
  await a.click('button:has-text("Skip")')
  ok(`Alex (admin), Rosa, Sam and Jo are in; reviewer's real runner is online as ${HOST}`)

  // --- 1. New project: Checkout app, from a repository, with Rosa and Sam, #checkout-app and 🔊 checkout-standup ---
  await a.click('[data-testid=new-project]')
  await a.fill('[name=projectName]', 'Checkout app'); await a.fill('[name=about]', 'Cart, payment and order confirmation')
  await a.click('[data-testid=start-repository]')
  await a.fill('[name=repoUrl]', BARE); await a.fill('[name=branch]', 'main')
  for (const who of ['Rosa', 'Sam']) { await a.click('[data-testid=add-person]'); await a.click(`[role=option]:has-text("${who}")`) }
  expect(await a.isChecked('[data-pick-channel=checkout-app]') && await a.isChecked('[data-pick-channel=checkout-standup]') && !(await a.isChecked('[data-pick-channel=checkout-alerts]')),
    'the dialog suggests #checkout-app and 🔊 checkout-standup from the name (and #checkout-alerts, unticked)')
  await a.fill('[name=repoUrl]', '/srv/git/checkout-app.git')          // the picture shows an invented path, not this machine's
  await shots(a, '1-new-project')
  await a.fill('[name=repoUrl]', BARE)
  await a.click('[data-testid=create-project]')
  await a.waitForSelector('[data-testid=project-settings-page] [data-testid=project-title]:has-text("Checkout app")')
  expect(/\/p\/\d+$/.test(a.url()) && clean(await a.textContent('[data-testid=project-role]')).includes('you lead it'), 'Alex lands in the project\'s settings: "Project settings · you lead it"')
  const projectId = Number(a.url().split('/').pop())
  const types = sql(`select type from events where stream_id='project-${projectId}' order by version`).map(x => x[0])
  expect(types.join() === 'ProjectCreated,ProjectMemberAdded,ProjectMemberAdded,ProjectMemberAdded,WorkspaceAdded', 'the project is its events: ' + types.join(', '))

  // --- 2. the channel list: the project as a group with its channels and a gear; #general outside it. Rosa sees it, Jo doesn't ---
  const group = (p) => p.locator('[data-project="Checkout app"]')
  await a.waitForSelector('[data-project="Checkout app"] [data-channel=checkout-app]')
  expect(await group(a).locator('[data-channel=checkout-standup][data-kind=voice]').count() === 1 && await group(a).locator('[data-testid=project-settings]').count() === 1
    && await group(a).locator('[data-channel=general]').count() === 0 && await a.locator('[data-channel=general]').count() === 1,
    'Alex\'s channel list: Checkout app with #checkout-app, 🔊 checkout-standup and a gear; #general under Channels')
  await r.waitForSelector('[data-project="Checkout app"] [data-channel=checkout-app]')
  ok('Rosa sees Checkout app and its channels (live, no reload)')
  await j.waitForTimeout(1000)
  const chId = sql("select id from channels where name='checkout-app'")[0][0]
  expect(await j.locator('[data-project]').count() === 0 && await j.locator('[data-channel=checkout-app]').count() === 0 && (await json(j, `/api/channels/${chId}/posts`)).status === 404
    && (await json(j, `/api/projects/${projectId}`)).status === 404, 'Jo, not a member, sees no project and no channel of it; the API says 404 to her too')
  await a.click('[data-channel=checkout-app]'); await a.waitForSelector('[data-testid=channel-title]:has-text("checkout-app")')
  await shots(a, '2-channel-list')

  // --- 3. Alex adds reviewer: the dialog shows its boundary in sentences, from real data, and warns that it runs unsandboxed ---
  await a.click('[data-project="Checkout app"] [data-testid=project-settings]')
  await a.click('[data-testid=add-agent]')
  await a.waitForSelector('[data-testid=boundary] [data-line=stops]')
  const lines = await a.$$eval('[data-testid=boundary] [data-line]', els => els.map(e => [e.dataset.line, e.textContent.replace(/\s+/g, ' ').trim()]))
  expect(lines.map(l => l[0]).join() === 'sees,takesWorkFrom,worksOn,reaches,asks,spends,remembers,stops', 'eight sentences: sees, takes work from, works on, reaches, asks, spends, remembers, stops')
  const line = Object.fromEntries(lines)
  expect(line.sees.startsWith("Checkout app's channels (#checkout-app and 🔊 checkout-standup)"), 'Sees: ' + line.sees)
  expect(line.takesWorkFrom.startsWith('Members of Checkout app: Alex, Rosa and Sam.'), 'Takes work from: ' + line.takesWorkFrom.slice(0, 60))
  expect(line.worksOn.includes(BARE) && line.worksOn.includes(`cloned on ${HOST}`) && line.worksOn.includes('worktree per thread'), 'Works on: the repository, cloned on build-01, a worktree per thread')
  expect(line.asks.startsWith('Alex, its owner') && line.remembers.includes('for Checkout app on build-01'), 'Asks: Alex. Remembers: a data folder for Checkout app on build-01')
  const warning = clean(await a.textContent('[data-testid=boundary-warning]'))
  expect(warning.startsWith(`Runs unsandboxed on ${HOST}.`), 'the warning: ' + warning)
  await a.click('[data-testid=grant-owner]')
  await a.waitForFunction(() => /^Only Alex/.test(document.querySelector('[data-line=takesWorkFrom]')?.textContent?.trim() ?? ''))
  ok('switching "takes work from" to Only Alex rewrites that sentence (from the server)')
  await a.click('[data-testid=grant-members]')
  await a.waitForFunction(() => /^Members of/.test(document.querySelector('[data-line=takesWorkFrom]')?.textContent?.trim() ?? ''))
  // the picture: the repository path in the sentence is this run's scratch path, so it is shown as the invented one
  await a.evaluate((bare) => { for (const c of document.querySelectorAll('[data-testid=boundary] code')) if (c.textContent === bare) c.textContent = '/srv/git/checkout-app.git' }, BARE)
  await shots(a, '3-add-agent-boundary')
  await a.click('[data-testid=add-agent-confirm]')
  await a.waitForSelector('[data-agent-row=reviewer]')
  expect(clean(await a.textContent('[data-agent-row=reviewer]')).includes(`${HOST}, pinned`) && clean(await a.textContent('[data-agent-row=reviewer] [data-testid=sandbox-pill]')) === 'unsandboxed',
    'reviewer is in the project: runs on build-01, pinned, unsandboxed')

  // --- 4. Rosa and Sam each mention reviewer in #checkout-app at the same moment: two threads side by side, each in its own worktree ---
  for (const p of [r, s]) { await p.click('[data-channel=checkout-app]'); await p.waitForSelector('[data-testid=channel-title]:has-text("checkout-app")') }
  await r.fill('[data-testid=composer]', '@reviewer Create a file named rosa-notes.md in the current folder containing exactly one line: Rosa was here. Then say done.')
  await s.fill('[data-testid=composer]', '@reviewer Create a file named sam-notes.md in the current folder containing exactly one line: Sam was here. Then say done.')
  await Promise.all([r.keyboard.press('Enter'), s.keyboard.press('Enter')])
  await Promise.all([r.waitForSelector('[data-testid=thread-panel]'), s.waitForSelector('[data-testid=thread-panel]')])
  await until('both turns to start', () => sql("select count(*) from events where type='TurnStarted'")[0][0] >= 2, 60000)
  const started = sql("select max(position) from events where type='TurnStarted'")[0][0]
  const endedFirst = await until('both turns to end', () => { const e = sql("select position from events where type='TurnEnded' order by position"); return e.length >= 2 ? e[0][0] : null }, LONG)
  expect(started < endedFirst, 'side by side: both turns had started before either ended')
  for (const p of [r, s]) await p.waitForFunction(() => /idle/.test(document.querySelector('[data-testid=agent-status]')?.textContent ?? ''), null, { timeout: LONG })
  const places = sql("select ta.thread_id, ta.place, ta.branch, ta.host, ta.location, t.started_by from thread_agents ta join threads t on t.root_id=ta.thread_id order by ta.thread_id")
  expect(places.length === 2 && places.every(p => p[1] === 'worktree' && p[2].startsWith('cowork/') && p[3] === HOST) && places[0][2] !== places[1][2],
    'each thread has its own branch: ' + places.map(p => p[2]).join(' and '))
  const clone = places[0][4]
  expect(places[1][4] === clone && clone.includes('/cowork-runner/') && existsSync(`${clone}/.git`), 'one clone for reviewer and the repository, under the runner\'s data folder')
  const worktrees = git(clone, 'worktree', 'list', '--porcelain').split('\n\n').map(b => ({ path: /^worktree (.*)$/m.exec(b)?.[1], branch: /^branch refs\/heads\/(.*)$/m.exec(b)?.[1] })).filter(w => w.branch?.startsWith('cowork/'))
  const rosaId = sql("select id from members where handle='rosa'")[0][0]
  const wtOf = (who) => worktrees.find(w => w.branch === places.find(p => p[5] === who)[2]).path
  const rosaWt = wtOf(rosaId), samWt = wtOf(sql("select id from members where handle='sam'")[0][0])
  console.log(`  worktrees: ${rosaWt} | ${samWt}`)
  expect(existsSync(`${rosaWt}/rosa-notes.md`) && !existsSync(`${rosaWt}/sam-notes.md`) && existsSync(`${samWt}/sam-notes.md`) && !existsSync(`${samWt}/rosa-notes.md`),
    "the file reviewer wrote in Rosa's thread is in her worktree and not in Sam's, and the other way round")
  expect(existsSync(`${rosaWt}/src/tax.ts`) && git(rosaWt, 'rev-parse', '--abbrev-ref', 'HEAD') === places.find(p => p[5] === rosaId)[2], "Rosa's worktree has the project's files, on her thread's branch")
  git(rosaWt, 'add', 'rosa-notes.md'); git(rosaWt, '-c', 'user.name=reviewer', '-c', 'user.email=reviewer@lumen.example', 'commit', '-q', '-m', 'Rosa\'s notes')
  let refusedPush = ''
  try { execFileSync('git', ['push', 'origin', 'HEAD:main'], { cwd: rosaWt, stdio: 'pipe' }) } catch (e) { refusedPush = e.stderr.toString() }
  expect(refusedPush.includes("pushing to main isn't allowed") && git(seed, 'ls-remote', BARE, 'refs/heads/main').startsWith(git(seed, 'rev-parse', 'HEAD')), "the worktree's pre-push hook refuses a push to main; main is unchanged")
  const harnessData = `${clone.split('/repos/')[0]}/projects/${projectId}/harness/data/opencode`
  expect(existsSync(`${harnessData}/opencode.db`), 'OpenCode keeps its data for this project in the project\'s own folder (remembers): ' + harnessData.replace(SCRATCH.dir, '<scratch>'))
  if (existsSync(`${SCRATCH.dir}/data/opencode/auth.json`))
    expect(readlinkSync(`${harnessData}/auth.json`) === `${SCRATCH.dir}/data/opencode/auth.json` && !existsSync(`${SCRATCH.dir}/data/opencode/opencode.db`),
      "the runner's sign-in is linked into the project's data folder (the harness signed in and answered), and nothing else of the project's went to the runner's own folder")
  const sessionLine = clean(await r.textContent('[data-testid=session-line]'))
  expect(sessionLine.includes(places.find(p => p[5] === rosaId)[2]) && sessionLine.includes(`unsandboxed on ${HOST}`), 'the thread\'s session line names its branch and says "unsandboxed on build-01": ' + sessionLine)
  await shots(r, '4-thread-worktree')

  // --- 5. the project's settings: workspace (cloned where), people with roles, agents (where, takes work from, asks), the rules ---
  await a.reload(); await a.waitForSelector('[data-testid=workspace-cloned]')
  const ws = clean(await a.textContent('[data-testid=workspace]'))
  expect(ws.includes(BARE) && ws.includes('main') && clean(await a.textContent('[data-testid=workspace-cloned]')) === `cloned on ${HOST}`, 'Workspaces: the repository and branch, cloned on build-01')
  const chips = await a.$$eval('[data-testid=person-chip]', els => els.map(e => `${e.dataset.person}:${e.dataset.role}`))
  expect(chips.join() === 'alex:lead,rosa:member,sam:member', 'People: ' + chips.join(', '))
  const row = clean(await a.textContent('[data-agent-row=reviewer]'))
  expect(row.includes(`${HOST}, pinned`) && row.includes('Members') && row.includes('Alex') && row.includes('unsandboxed'), 'Agents: reviewer runs on build-01, takes work from members, asks Alex, unsandboxed')
  const rules = (await a.$$eval('[data-testid=rule]', els => els.map(e => e.textContent.replace(/\s+/g, ' ').trim())))
  expect(rules.some(x => x.includes('A hook refuses pushes to main')) && rules.some(x => x.includes('owner only')), `Rules in force (${rules.length}), e.g. "${rules[0]}"`)
  await a.evaluate((bare) => { for (const c of document.querySelectorAll('code')) if (c.textContent === bare) c.textContent = '/srv/git/checkout-app.git'; for (const c of document.querySelectorAll('[data-testid=workspace-place] code')) c.textContent = c.textContent.replace(/^.*\/cowork-runner\//, '~/.local/share/cowork-runner/') }, BARE)
  await shots(a, '5-project-settings')

  // --- 6. reviewer takes work from its owner only: Rosa's mention gets a polite reply instead of a turn ---
  await a.click('[data-testid=grant-reviewer]'); await a.click('[role=option][data-grant=owner]')
  await until('the grant to change', () => sql(`select takes_work_from from project_agents where agent_id=(select id from members where handle='reviewer')`)[0][0] === 'owner')
  const turnsBefore = sql("select count(*) from events where type='TurnStarted'")[0][0]
  await r.keyboard.press('Escape'); await r.click('[aria-label="Close thread"]').catch(() => {})
  await r.fill('[data-testid=composer]', '@reviewer what does src/tax.ts do?'); await r.keyboard.press('Enter')
  await r.waitForSelector('[data-testid=thread-panel] [data-testid=notice]:has-text("reviewer only takes work from Alex.")')
  ok('Rosa\'s mention gets "reviewer only takes work from Alex." in the thread')
  await r.waitForTimeout(1500)
  expect(sql("select count(*) from events where type='TurnStarted'")[0][0] === turnsBefore && sql("select count(*) from events where type='WorkRefused'")[0][0] === 1, 'no turn started for it; one WorkRefused event says why')
  const audit = await until('the audit to say why', async () => (await json(a, '/api/audit?limit=200')).body?.entries.map(e => e.sentence).find(x => x.includes("didn't take it")), 20000)
  expect(audit === "Rosa asked reviewer (Alex's agent) for work in #checkout-app. It didn't take it: it takes work from Alex only.", 'Audit: ' + audit)
  await shots(r, '6-owner-only')
  console.log('PHASE 7 LIVE CHECK PASSED')
} catch (e) {
  console.log('FAILED', e.message)
  for (const [p, n] of [[a, 'alex'], [r, 'rosa'], [s, 'sam'], [j, 'jo']]) await shot(p, 'FAIL-' + n).catch(() => {})
  if (server?.log) console.log(server.log.split('\n').filter(l => /fail|error|exception/i.test(l)).slice(-20).join('\n'))
  process.exitCode = 1
} finally {
  await stopGroup(runner); await stopGroup(server)
  await browser.close()
  SCRATCH.cleanup()
}
