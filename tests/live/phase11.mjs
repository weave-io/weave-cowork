// Brief 7, round 3: threads as work. Browsers for Alex (admin, Checkout app's lead), Rosa and Sam; a REAL pool with REAL containers and REAL
// OpenCode 2.4.1 (the researcher template), and a REAL runner with --host build-01 serving Alex's pinned reviewer with REAL OpenCode 2.4.1.
// Model calls go to this machine's GitHub Copilot sign-in (the user agreed, 25 Nov 2026): researcher's through the gateway, reviewer's
// through its runner's own (linked) sign-in. Both get CoWork's tools as an MCP server in session/new.
// The demo: researcher reads #checkout-app with cowork_read_channel and resolves its thread with cowork_resolve_thread: the post says done,
// the thread shows the outcome card with time and usage, and search finds the outcome first → asked to read #leads and search for what's
// only there, it can't: its boundary → reviewer, pinned, uses the same tools with its runner's token → after the retention time a done
// thread's volume is deleted (no changes), and one with a changed file is kept.
// Starts its own server (default port 5884). Everything in podman carries the pool's label and is removed by it at the end.
//   node tests/live/phase11.mjs          (screenshots go to docs/screens/brief7/)
import { launch, person, expect, ok, freshHarnessHome, MODEL } from './lib.mjs'
import { spawn, execFileSync } from 'node:child_process'
import { homedir } from 'node:os'
import { rmSync, mkdirSync, writeFileSync, readFileSync } from 'node:fs'

const HOME = homedir()
const PORT = process.env.PORT ?? '5884'
const BASE = `http://127.0.0.1:${PORT}`
const REPO = new URL('../../', import.meta.url).pathname.replace(/\/$/, '')
const DIR = `${HOME}/.cache/cowork-p11`
const DB = `${DIR}/data/cowork.db`
const SHOTS = process.env.SHOTS_DIR ?? `${REPO}/docs/screens/brief7/`
const SERVER = `${REPO}/src/CoWork.Server/bin/Debug/net10.0/CoWork.Server.dll`
const RUNNER = `${REPO}/src/CoWork.Runner/bin/Debug/net10.0/cowork-runner.dll`
const HARNESS = `${HOME}/.weave/harnesses/opencode2/.opencode/bin/opencode acp`
const IMAGE = 'cowork-sandbox-opencode:2.4.1'
const IDLE = 20, KEEP = 20
const LONG = 240000
rmSync(DIR, { recursive: true, force: true }); mkdirSync(`${DIR}/data`, { recursive: true }); mkdirSync(SHOTS, { recursive: true })
const SCRATCH = freshHarnessHome('p11')            // the pinned runner's own home and OpenCode data, deleted at the end
writeFileSync(`${SCRATCH.dir}/config/opencode/opencode.json`, JSON.stringify({ $schema: 'https://opencode.ai/config.json', permission: { edit: 'deny', bash: 'deny' }, ...MODEL }))
const sql = (q) => JSON.parse(execFileSync('python3', ['-I', '-c', 'import sqlite3,sys,json;c=sqlite3.connect(sys.argv[1]);print(json.dumps(c.execute(sys.argv[2]).fetchall()))', DB, q]).toString())
const until = async (what, probe, ms = 60000) => { const end = Date.now() + ms; for (;;) { const v = await probe(); if (v) return v; if (Date.now() > end) throw new Error('FAILED: timed out waiting for ' + what); await new Promise(r => setTimeout(r, 250)) } }
const clean = (t) => (t ?? '').replace(/\s+/g, ' ').trim()
const podman = (...a) => execFileSync('podman', a, { stdio: ['ignore', 'pipe', 'pipe'] }).toString().trim()
const git = (cwd, ...args) => execFileSync('git', args, { cwd, env: { ...process.env, GIT_AUTHOR_NAME: 'Alex', GIT_AUTHOR_EMAIL: 'alex@lumen.example', GIT_COMMITTER_NAME: 'Alex', GIT_COMMITTER_EMAIL: 'alex@lumen.example' } }).toString().trim()

if (!podman('images', '-q', IMAGE)) { console.log(`FAILED: the image ${IMAGE} isn't built here. Run deploy/sandbox/build.sh first.`); process.exit(1) }
const seed = `${DIR}/seed`; mkdirSync(`${seed}/src`, { recursive: true })
git(seed, 'init', '-q', '-b', 'main')
writeFileSync(`${seed}/README.md`, '# checkout-app\n\nCart, payment and order confirmation for the Lumen Labs web shop.\n')
writeFileSync(`${seed}/src/tax.ts`, 'export function roundTax(amount: number): number {\n  return Math.round(amount * 100) / 100\n}\n')
git(seed, 'add', '.'); git(seed, 'commit', '-q', '-m', 'Cart and tax')
const BARE = `${DIR}/git/checkout-app.git`
git(DIR, 'clone', '-q', '--bare', seed, BARE)

let server = null, pool = null, runner = null, poolKey = null
function startServer() {
  server = spawn('dotnet', [SERVER], { cwd: `${REPO}/src/CoWork.Server`, env: { ...process.env, COWORK_DB: DB, ASPNETCORE_URLS: BASE }, stdio: ['ignore', 'pipe', 'pipe'], detached: true })
  server.log = ''; server.stdout.on('data', d => server.log += d); server.stderr.on('data', d => server.log += d)
  return until('the server to listen', async () => { try { return (await fetch(BASE + '/api/setup/status')).ok } catch { return false } }, 30000)
}
const stopGroup = (p) => new Promise(res => { if (!p || p.exitCode !== null) return res(); p.once('exit', res); try { process.kill(-p.pid, 'SIGTERM') } catch { return res() } setTimeout(res, 8000) })
function spawnLogged(args, env, tag) {
  const p = spawn('dotnet', [RUNNER, ...args], { env, stdio: ['ignore', 'pipe', 'pipe'], detached: true })
  p.log = ''
  p.stdout.on('data', d => { p.log += d; process.stdout.write(`  [${tag}] ` + d) }); p.stderr.on('data', d => { p.log += d; process.stdout.write(`  [${tag}!] ` + d) })
  return p
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
const answer = (thread, handle = 'researcher') => sql(`select group_concat(body, ' ') from posts where thread_id=${thread} and kind='text' and author_id=(select id from members where handle='${handle}')`)[0][0] ?? ''
const toolCalls = (thread) => sql(`select json_extract(data,'$.tool'), json_extract(data,'$.refused'), actor_id from events where type='CoworkToolCalled' and stream_id='thread-${thread}' order by position`)
const toBottom = (p) => p.evaluate(() => { const el = document.querySelector('[data-testid=thread-panel] .thin-scrollbar'); if (el) el.scrollTop = el.scrollHeight })
const idleIn = (p) => p.waitForFunction(() => /idle/.test(document.querySelector('[data-testid=thread-panel] [data-testid=agent-status]')?.textContent ?? ''), null, { timeout: LONG })
const channel = () => sql("select id from channels where name='checkout-app'")[0][0]

try {
  await startServer()
  await a.goto(BASE)
  await a.fill('[name=serverName]', 'Lumen Labs'); await a.fill('[name=username]', 'alex'); await a.fill('[name=displayName]', 'Alex'); await a.fill('[name=password]', 'correct horse')
  await a.click('button[type=submit]'); await a.waitForSelector('[data-channel=general]')
  await join(r, 'rosa', 'Rosa'); await join(s, 'sam', 'Sam')
  // a pool, Copilot, short idle and retention times, the researcher template
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-where]'); await a.click('[data-testid=add-pool]')
  const command = clean(await a.textContent('[data-testid=pool-command]'))
  pool = spawnLogged([...command.split(' ').slice(1), '--host', 'build-02'], { ...process.env, COWORK_RUNNER_CONFIG: `${DIR}/pool/config`, COWORK_RUNNER_DATA: `${DIR}/pool/data` }, 'pool')
  await a.waitForSelector('[data-testid=pool-connected]', { timeout: 30000 }); await a.keyboard.press('Escape')
  poolKey = /labels cowork\.pool=(\S+);/.exec(pool.log)?.[1]
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-server-settings]')
  await a.click('[data-testid=provider-copilot]'); await a.click('[data-testid=save-models]'); await a.waitForSelector('[data-testid=models-saved]')
  await a.fill('[name=idleSeconds]', String(IDLE)); await a.fill('[name=retentionSeconds]', String(KEEP)); await a.click('[data-testid=save-idle]'); await a.waitForSelector('[data-testid=idle-saved]')
  expect(sql("select value from settings where key='sandbox_retention_seconds'")[0][0] === String(KEEP), `Server settings: a done thread's volume is kept ${KEEP} seconds (short for the check)`)
  await shots(a, '22-server-settings-retention')
  await a.keyboard.press('Escape')
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-invite-agent]'); await a.click('[data-testid=runs-on-pool]')
  await a.fill('[name=handle]', 'researcher'); await a.fill('[name=about]', 'Reads the code and the conversation, and finds out')
  await a.fill('[name=instructions]', 'You are researcher, on the Lumen Labs checkout app. Answer in at most two short sentences. Use CoWork\'s tools (cowork_read_channel, cowork_search, cowork_resolve_thread) when asked to.')
  await a.click('[data-testid=create-agent]'); await a.waitForSelector('[data-testid=template-created]'); await a.keyboard.press('Escape')
  // Alex's own reviewer on his runner on build-01 (pinned)
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-invite-agent]')
  await a.fill('[name=handle]', 'reviewer'); await a.fill('[name=about]', 'Reviews the checkout app')
  await a.fill('[name=instructions]', 'You are reviewer, on the Lumen Labs checkout app. Answer in one short sentence. Use CoWork\'s tools (cowork_read_channel, cowork_resolve_thread) when asked to.')
  await a.fill('[name=harness]', HARNESS); await a.fill('[name=workspace]', `${SCRATCH.dir}/own-workspace`)
  await a.click('[data-testid=create-agent]'); await a.waitForSelector('[data-testid=runner-command]')
  const parts = clean(await a.textContent('[data-testid=runner-command]')).split(' ')
  runner = spawnLogged(['connect', BASE, '--code', parts[parts.length - 1], '--host', 'build-01'], SCRATCH.env, 'runner')
  await a.waitForSelector('[data-testid=agent-connected]', { timeout: 30000 }); await a.click('button:has-text("Skip")')
  // Checkout app, with both agents; a private #leads that neither is in
  await a.click('[data-testid=new-project]')
  await a.fill('[name=projectName]', 'Checkout app'); await a.fill('[name=about]', 'Cart, payment and order confirmation')
  await a.click('[data-testid=start-repository]'); await a.fill('[name=repoUrl]', BARE); await a.fill('[name=branch]', 'main')
  for (const who of ['Rosa', 'Sam']) { await a.click('[data-testid=add-person]'); await a.click(`[role=option]:has-text("${who}")`) }
  await a.click('[data-testid=create-project]')
  await a.waitForSelector('[data-testid=project-settings-page] [data-testid=project-title]:has-text("Checkout app")')
  for (const handle of ['researcher', 'reviewer']) {
    await a.click('[data-testid=add-agent]'); await a.waitForSelector('[data-testid=boundary] [data-line=stops]')
    if (await a.locator(`[data-pick-agent=${handle}]`).count()) { await a.click(`[data-pick-agent=${handle}]`); await a.waitForTimeout(400) }
    await a.click('[data-testid=add-agent-confirm]'); await a.waitForSelector(`[data-agent-row=${handle}]`)
  }
  const leads = await a.evaluate(async () => (await (await fetch('/api/channels', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: 'leads', isPrivate: true }) })).json()).id)
  await a.evaluate(async (id) => fetch(`/api/channels/${id}/posts`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ body: 'The Q4 refund budget is 40k, not for sharing yet.' }) }), leads)
  for (const p of [r, s, a]) { await p.goto(`${BASE}/c/${channel()}`); await p.waitForSelector('[data-testid=channel-title]:has-text("checkout-app")') }
  await s.fill('[data-testid=composer]', "Finance wants partial refunds by January. Our tax step rounds per line, which is why we're a cent out."); await s.keyboard.press('Enter')
  ok('set up: pool-01, Copilot, idle 20 s and retention 20 s, researcher (a template) and reviewer (pinned on build-01) in Checkout app; Sam said what finance wants; #leads is private to Alex')

  // --- 1. researcher reads the channel with a tool and resolves the thread: done on the post, the outcome in the thread ---
  await r.evaluate(() => {           // every state the post's pill shows, as it changes
    window.__states = []
    new MutationObserver(() => { const el = document.querySelector('[data-testid=feed] [data-testid=thread-status]'); const st = el?.dataset.state; if (st && window.__states.at(-1) !== st) window.__states.push(st) })
      .observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['data-state'] })
  })
  await r.fill('[data-testid=composer]', '@researcher Use cowork_read_channel to read #checkout-app, tell me in one sentence what finance wants, then call cowork_resolve_thread with a short title and a two-line summary.')
  await r.keyboard.press('Enter')
  const rosa = await threadOf('tell me in one sentence what finance wants')
  await until('the thread to be resolved', () => events('ThreadResolved', rosa) >= 1, LONG)
  await until("Rosa's turn to end", () => events('TurnEnded', rosa) >= 1, LONG)
  const calls = toolCalls(rosa)
  expect(calls.some(c => c[0] === 'cowork_read_channel' && c[1] === 0) && calls.some(c => c[0] === 'cowork_resolve_thread' && c[1] === 0), 'researcher called cowork_read_channel and cowork_resolve_thread (CoworkToolCalled): ' + calls.map(c => c[0]).join(', '))
  expect(/refund/i.test(answer(rosa)), 'it answered from the channel: "' + answer(rosa).slice(0, 120) + '"')
  await r.waitForSelector('[data-testid=thread-panel] [data-testid=outcome-card]')
  const title = clean(await r.textContent('[data-testid=thread-panel] [data-testid=outcome-title]'))
  const meta = clean(await r.textContent('[data-testid=thread-panel] [data-testid=outcome-meta]'))
  expect(title.length > 0 && /\d+ (s|min)/.test(meta) && /[\d,]+ tokens/.test(meta) && /\d+ tool calls?/.test(meta) && meta.includes('sandbox on pool-01'), `the outcome card in the thread: "${title}" · ${meta}`)
  expect(clean(await r.textContent('[data-testid=thread-panel] [data-testid=thread-status]')) === 'done', 'the thread says done')
  const postPill = r.locator(`[data-post="${rosa}"] [data-testid=thread-status]`)
  await until('the post to say done', async () => (await postPill.getAttribute('data-state')) === 'done', 20000)
  const seen = await r.evaluate(() => window.__states)
  expect(seen.at(-1) === 'done' && seen.includes('working'), `the post's status changed live: ${seen.join(' → ')}`)
  expect(clean(await r.textContent(`[data-post="${rosa}"] [data-testid=thread-outcome]`)) === title, 'the post shows the outcome beside "done": ' + title)
  await r.waitForSelector('[data-testid=thread-panel] [data-testid=tool-row]:has-text("cowork_read_channel")')
  await idleIn(r); await toBottom(r); await shots(r, '18-outcome')

  // --- 2. its boundary: it can't read #leads, and search doesn't find what's only there ---
  await s.fill('[data-testid=composer]', '@researcher First call cowork_search for "Q4 refund budget" on its own. Then, in a separate step, use cowork_read_channel on #leads. Tell me exactly what each returned. Do not resolve this thread.')
  await s.keyboard.press('Enter')
  const sam = await threadOf('First call cowork_search for')
  await until("Sam's turn to end", () => events('TurnEnded', sam) >= 1, LONG)
  const samCalls = toolCalls(sam)
  const searched = sql(`select json_extract(data,'$.detail.channels') from events where type='CoworkToolCalled' and stream_id='thread-${sam}' and json_extract(data,'$.tool')='cowork_search'`)[0]?.[0]
  expect(samCalls.some(c => c[0] === 'cowork_read_channel' && c[1] === 1) && searched !== undefined && !JSON.parse(searched ?? '[]').includes('leads'),
    `reading #leads was refused (outside its boundary), and its search found nothing in #leads (results only from: ${JSON.parse(searched ?? '[]').join(', ') || 'nowhere'})`)
  await s.waitForSelector(`[data-testid=thread-panel] [data-testid=tool-row]:has-text("cowork_search")`)
  ok('its tool rows name the MCP calls: ' + (await s.$$eval('[data-testid=thread-panel] [data-testid=tool-row]', els => els.map(e => e.textContent.replace(/\s+/g, ' ').trim()))).join(' | '))
  expect(!/40k|40,000/.test(answer(sam)) && events('ThreadResolved', sam) === 0, 'nothing from #leads reached it: "' + answer(sam).slice(0, 140) + '"')
  await s.waitForSelector(`[data-post="${sam}"]`)

  // --- 3. search finds the outcome first ---
  await r.click('[data-testid=search-nav]')
  await r.fill('[data-testid=search-input]', 'refunds')
  await r.waitForSelector('[data-testid=search-outcomes] [data-kind=outcome]')
  const groups = await r.$$eval('#search-results section', els => els.map(e => e.dataset.testid))
  expect(groups[0] === 'search-outcomes' && clean(await r.textContent('[data-testid=search-outcomes]')).includes('done'), 'search: Outcomes first (' + groups.join(', ') + '), marked done')
  await shots(r, '20-search-outcomes')
  await r.click('[data-testid=search-outcomes] [data-kind=outcome]')
  await r.waitForSelector('[data-testid=thread-panel] [data-testid=outcome-card]')
  ok('opening the outcome opens its thread')

  // --- 4. reviewer, pinned on build-01, uses the same tools with its runner's token ---
  await a.goto(`${BASE}/c/${channel()}`); await a.waitForSelector('[data-testid=channel-title]:has-text("checkout-app")')
  await a.fill('[data-testid=composer]', '@reviewer Use cowork_read_channel to read #checkout-app, then call cowork_resolve_thread with a one-line summary of what finance wants.')
  await a.keyboard.press('Enter')
  const alex = await threadOf('@reviewer Use cowork_read_channel')
  await until("reviewer's thread to be resolved", () => events('ThreadResolved', alex) >= 1, LONG)
  const reviewerId = sql("select id from members where handle='reviewer'")[0][0]
  expect(toolCalls(alex).filter(c => c[2] === reviewerId && c[1] === 0).length >= 2, 'reviewer (pinned) called CoWork\'s tools through its runner\'s token: ' + toolCalls(alex).map(c => c[0]).join(', '))
  await a.waitForSelector('[data-testid=thread-panel] [data-testid=outcome-card]')
  expect(clean(await a.textContent('[data-testid=thread-panel] [data-testid=outcome-meta]')).includes("Alex's own access") && clean(await a.textContent('[data-testid=thread-panel] [data-testid=outcome-branch]')).includes('build-01'),
    "its outcome: on Alex's own access, its branch in a worktree on build-01")
  await idleIn(a); await a.click('[aria-label="Close thread"]')
  await shots(a, '19-posts-done')

  // --- 5. a change in the sandbox keeps its volume; the done thread without changes loses its volume after the retention time ---
  await r.goto(`${BASE}/c/${channel()}`); await r.waitForSelector('[data-testid=channel-title]:has-text("checkout-app")')
  await r.fill('[data-testid=composer]', '@researcher Append the line "Reviewed by researcher." to the end of README.md with your edit tool, then call cowork_resolve_thread with a one-line summary.')
  await r.keyboard.press('Enter')
  const edit = await threadOf('Append the line')
  await until('the edit thread to be resolved', async () => {
    const allow = r.locator('[data-testid=thread-panel] [data-testid=ask-card] button').first()     // if the harness asks before editing, Rosa allows it
    if (await allow.count()) await allow.click().catch(() => {})
    return events('ThreadResolved', edit) >= 1
  }, LONG)
  const rosaBox = sql(`select id from sandboxes where thread_id=${rosa}`)[0][0], editBox = sql(`select id from sandboxes where thread_id=${edit}`)[0][0]
  await until("Rosa's first sandbox to be deleted", () => events('SandboxDeleted', rosa) >= 1, (IDLE + KEEP + 90) * 1000)
  const vol = (id) => `cowork-${poolKey}-sb-${id}`
  const volumes = () => podman('volume', 'ls', '-q', '--filter', `label=cowork.pool=${poolKey}`).split('\n')
  expect(!volumes().includes(vol(rosaBox)), `after ${IDLE} s idle and ${KEEP} s retention the done thread's volume was deleted, its branch with it`)
  await until('the edit thread to be kept', () => events('SandboxKept', edit) >= 1, (IDLE + KEEP + 90) * 1000)
  const reason = sql(`select json_extract(data,'$.reason') from events where type='SandboxKept' and stream_id='thread-${edit}'`)[0][0]
  expect(volumes().includes(vol(editBox)) && /changed file|commit/.test(reason), 'the thread with a change keeps its volume: ' + reason)
  await r.goto(`${BASE}/c/${channel()}/t/${rosa}`)
  await r.waitForSelector('[data-testid=thread-panel] [data-testid=outcome-branch]:has-text("no changes, deleted with its volume")')
  ok('Rosa\'s outcome says: "no changes, deleted with its volume"')
  await toBottom(r); await shots(r, '21-volume-deleted')
  await r.goto(`${BASE}/c/${channel()}/t/${edit}`)
  await r.waitForSelector('[data-testid=thread-panel] [data-testid=outcome-branch]:has-text("so its volume is kept")')
  ok("the edit thread's outcome says why its volume is kept")
  await toBottom(r); await shots(r, '21-volume-kept')
  console.log('PHASE 11 LIVE CHECK PASSED')
} catch (e) {
  console.log('FAILED', e.message)
  for (const [p, n] of [[a, 'alex'], [r, 'rosa'], [s, 'sam']]) await shot(p, 'FAIL-' + n).catch(() => {})
  if (server?.log) console.log(server.log.split('\n').filter(l => /fail|error|exception|warn/i.test(l)).slice(-20).join('\n'))
  process.exitCode = 1
} finally {
  await stopGroup(runner); await stopGroup(pool); await stopGroup(server)
  await browser.close()
  // twice: a container start already on its way when the pool stopped can land after the first sweep
  for (const pass of [1, 2]) { try { cleanupPodman() } catch (e) { console.log('cleanup:', e.message) } if (pass === 1) await new Promise(r => setTimeout(r, 5000)) }
  SCRATCH.cleanup()
  if (poolKey) console.log(`removed every container and volume labelled cowork.pool=${poolKey}`)
}
