// Brief 8: threads you can find. Browsers for Alex (admin, Checkout app's lead) and Rosa (a member), a REAL pool with REAL containers and
// REAL OpenCode 2.4.1 for the researcher template, model calls through the gateway to this machine's GitHub Copilot sign-in (the user agreed,
// 25 Nov 2026).
// The demo: Rosa's thread appears under #checkout-app in both lists, live, with its title, researcher's avatar and its status as a dot
// (starting → working) → Alex has unread replies on its row until he opens it → it opens as a full conversation in the main area (its own
// URL, reloadable; the session line, tool rows, the outcome card, its own composer) → resolved, its title is the outcome's; Rosa renames it
// and Alex's list follows → five threads per channel, most recent activity first, then "All threads"; the chevron folds them, remembered →
// All threads with its filters → a thread waiting for Rosa shows "waiting for you" to her and "waiting for Rosa" to Alex → a mention of an
// agent that isn't in the project says so in a new thread → added to a project with no repository, its sandbox says so in its starting rows →
// the phone: threads in the drawer, a thread fills the screen. A private channel's threads are only in its members' lists.
// Starts its own server (default port 5885). Everything in podman carries the pool's label and is removed by it at the end.
//   node tests/live/phase12.mjs          (screenshots go to docs/screens/brief8/)
import { launch, person, expect, ok } from './lib.mjs'
import { spawn, execFileSync } from 'node:child_process'
import { homedir } from 'node:os'
import { rmSync, mkdirSync, writeFileSync } from 'node:fs'

const HOME = homedir()
const PORT = process.env.PORT ?? '5885'
const BASE = `http://127.0.0.1:${PORT}`
const REPO = new URL('../../', import.meta.url).pathname.replace(/\/$/, '')
const DIR = `${HOME}/.cache/cowork-p12`
const DB = `${DIR}/data/cowork.db`
const SHOTS = process.env.SHOTS_DIR ?? `${REPO}/docs/screens/brief8/`
const SERVER = `${REPO}/src/CoWork.Server/bin/Debug/net10.0/CoWork.Server.dll`
const RUNNER = `${REPO}/src/CoWork.Runner/bin/Debug/net10.0/cowork-runner.dll`
const IMAGE = 'cowork-sandbox-opencode:2.4.1'
const LONG = 240000
rmSync(DIR, { recursive: true, force: true }); mkdirSync(`${DIR}/data`, { recursive: true }); mkdirSync(SHOTS, { recursive: true })
const sql = (q) => JSON.parse(execFileSync('python3', ['-I', '-c', 'import sqlite3,sys,json;c=sqlite3.connect(sys.argv[1]);print(json.dumps(c.execute(sys.argv[2]).fetchall()))', DB, q]).toString())
const until = async (what, probe, ms = 60000) => { const end = Date.now() + ms; for (;;) { const v = await probe(); if (v) return v; if (Date.now() > end) throw new Error('FAILED: timed out waiting for ' + what); await new Promise(r => setTimeout(r, 250)) } }
const clean = (t) => (t ?? '').replace(/\s+/g, ' ').trim()
const podman = (...a) => execFileSync('podman', a, { stdio: ['ignore', 'pipe', 'pipe'] }).toString().trim()
const git = (cwd, ...args) => execFileSync('git', args, { cwd, env: { ...process.env, GIT_AUTHOR_NAME: 'Alex', GIT_AUTHOR_EMAIL: 'alex@lumen.example', GIT_COMMITTER_NAME: 'Alex', GIT_COMMITTER_EMAIL: 'alex@lumen.example' } }).toString().trim()

if (!podman('images', '-q', IMAGE)) { console.log(`FAILED: the image ${IMAGE} isn't built here. Run deploy/sandbox/build.sh first.`); process.exit(1) }
const seed = `${DIR}/seed`; mkdirSync(`${seed}/src`, { recursive: true })
git(seed, 'init', '-q', '-b', 'main')
writeFileSync(`${seed}/README.md`, '# checkout-app\n\nCart, payment and order confirmation for the Lumen Labs web shop.\n')
writeFileSync(`${seed}/src/tax.ts`, '// Tax is rounded per line, to the cent, before the lines are added up.\nexport function roundTax(amount: number): number {\n  return Math.round(amount * 100) / 100\n}\n')
git(seed, 'add', '.'); git(seed, 'commit', '-q', '-m', 'Cart and tax')
const BARE = `${DIR}/git/checkout-app.git`
git(DIR, 'clone', '-q', '--bare', seed, BARE)

let server = null, pool = null, poolKey = null
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
const A = await person(browser), R = await person(browser)
const a = A.page, r = R.page
const shot = async (p, name) => { await p.waitForTimeout(400); await p.screenshot({ path: SHOTS + name + '.png' }) }
const shots = async (p, name) => {
  for (const theme of ['dark', 'light']) { await p.evaluate(t => { localStorage.setItem('weave:theme', t); document.documentElement.dataset.theme = t; document.documentElement.style.colorScheme = t }, theme); await shot(p, `${name}-${theme}`) }
  await p.evaluate(() => { localStorage.setItem('weave:theme', 'dark'); document.documentElement.dataset.theme = 'dark'; document.documentElement.style.colorScheme = 'dark' })
}
const api = (p, method, path, body) => p.evaluate(async ([m, u, b]) => { const res = await fetch('/api' + u, { method: m, headers: { 'Content-Type': 'application/json' }, body: b === undefined ? undefined : JSON.stringify(b) }); const t = await res.text(); return t ? JSON.parse(t) : null }, [method, path, body])
async function join(p, handle, name) {
  const code = (await api(a, 'POST', '/invites', {})).code
  await p.goto(`${BASE}/invite/${code}`)
  await p.fill('[name=username]', handle); await p.fill('[name=displayName]', name); await p.fill('[name=password]', `${handle}-password`); await p.click('button[type=submit]')
  await p.waitForSelector('[data-channel=general]')
}
const events = (type, thread) => sql(`select count(*) from events where type='${type}'${thread ? ` and stream_id='thread-${thread}'` : ''}`)[0][0]
const threadOf = (text) => until('the thread', () => sql(`select root_id from threads t join posts p on p.id=t.root_id where p.body like '%${text}%'`)[0]?.[0])
const answer = (thread) => sql(`select group_concat(body, ' ') from posts where thread_id=${thread} and kind='text' and author_id=(select id from members where handle='researcher')`)[0][0] ?? ''
const channel = (name) => sql(`select id from channels where name='${name}'`)[0][0]
const row = (thread) => `[data-threads=checkout-app] [data-thread="${thread}"]`
const rowsIn = (p, name) => p.$$eval(`[data-threads=${name}] [data-testid=thread-row]`, els => els.map(e => Number(e.dataset.thread)))
const listed = async (p, ch) => ((await api(p, 'GET', '/thread-list')) ?? []).find(c => c.channelId === ch)?.threads ?? []
const unreadOf = async (p, ch, thread) => (await listed(p, ch)).find(t => t.rootId === thread)?.unread ?? 0
async function post(p, text) { await p.fill('[data-testid=composer]', text); await p.keyboard.press('Enter') }

try {
  await startServer()
  await a.goto(BASE)
  await a.fill('[name=serverName]', 'Lumen Labs'); await a.fill('[name=username]', 'alex'); await a.fill('[name=displayName]', 'Alex'); await a.fill('[name=password]', 'correct horse')
  await a.click('button[type=submit]'); await a.waitForSelector('[data-channel=general]')
  await join(r, 'rosa', 'Rosa')
  // a pool, Copilot, the researcher template
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-where]'); await a.click('[data-testid=add-pool]')
  const command = clean(await a.textContent('[data-testid=pool-command]'))
  pool = spawnLogged([...command.split(' ').slice(1), '--host', 'build-02'], { ...process.env, COWORK_RUNNER_CONFIG: `${DIR}/pool/config`, COWORK_RUNNER_DATA: `${DIR}/pool/data` }, 'pool')
  await a.waitForSelector('[data-testid=pool-connected]', { timeout: 30000 }); await a.keyboard.press('Escape')
  poolKey = /labels cowork\.pool=(\S+);/.exec(pool.log)?.[1]
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-server-settings]')
  await a.click('[data-testid=provider-copilot]'); await a.click('[data-testid=save-models]'); await a.waitForSelector('[data-testid=models-saved]')
  await a.fill('[name=idleSeconds]', '120'); await a.click('[data-testid=save-idle]'); await a.waitForSelector('[data-testid=idle-saved]')
  await a.keyboard.press('Escape')
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-invite-agent]'); await a.click('[data-testid=runs-on-pool]')
  await a.fill('[name=handle]', 'researcher'); await a.fill('[name=about]', 'Reads the code and the conversation, and finds out')
  await a.fill('[name=instructions]', 'You are researcher, on the Lumen Labs checkout app. Answer in one short sentence. Use your tools when asked to (read, webfetch, cowork_resolve_thread). Never ask questions back.')
  await a.click('[data-testid=create-agent]'); await a.waitForSelector('[data-testid=template-created]'); await a.keyboard.press('Escape')
  // Checkout app (a repository), with Rosa and researcher; Billing (nothing yet), with Rosa and without researcher; a private #leads for Alex
  await a.click('[data-testid=new-project]')
  await a.fill('[name=projectName]', 'Checkout app'); await a.fill('[name=about]', 'Cart, payment and order confirmation')
  await a.click('[data-testid=start-repository]'); await a.fill('[name=repoUrl]', BARE); await a.fill('[name=branch]', 'main')
  await a.click('[data-testid=add-person]'); await a.click('[role=option]:has-text("Rosa")')
  await a.click('[data-testid=create-project]')
  await a.waitForSelector('[data-testid=project-settings-page] [data-testid=project-title]:has-text("Checkout app")')
  await a.click('[data-testid=add-agent]'); await a.waitForSelector('[data-testid=boundary] [data-line=stops]')
  await a.click('[data-testid=add-agent-confirm]'); await a.waitForSelector('[data-agent-row=researcher]')
  const rosaId = sql("select id from members where handle='rosa'")[0][0]
  const billing = await api(a, 'POST', '/projects', { name: 'Billing', about: 'Invoices and payouts', memberIds: [rosaId], channels: [{ name: 'billing', kind: 'text' }] })
  const leads = (await api(a, 'POST', '/channels', { name: 'leads', isPrivate: true })).id
  const CH = channel('checkout-app')
  for (const p of [r, a]) { await p.goto(`${BASE}/c/${p === r ? CH : channel('general')}`); await p.waitForSelector('[data-testid=channel-title]') }
  ok('set up: pool-01, Copilot, researcher (a template) in Checkout app (a repository) with Rosa; Billing with Rosa and without researcher; Alex is in #general')

  // --- 1. Rosa's thread appears under #checkout-app in both lists, live: its title, researcher's avatar, its status as a dot ---
  for (const p of [r, a]) await p.evaluate(() => {          // every state a thread row's dot shows, as it changes
    window.__dots = {}
    new MutationObserver(() => document.querySelectorAll('[data-threads] [data-testid=thread-row]').forEach(el => {
      const s = el.querySelector('[data-testid=thread-dot]')?.dataset.state, t = el.dataset.thread, seen = (window.__dots[t] ??= [])
      if (s && seen.at(-1) !== s) seen.push(s)
    })).observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['data-state', 'data-thread'] })
  })
  await post(r, '@researcher what does roundTax in src/tax.ts do?')
  const T1 = await threadOf('what does roundTax in src/tax.ts do?')
  await r.waitForSelector(row(T1)); await a.waitForSelector(row(T1))
  const title1 = clean(await r.textContent(`${row(T1)} [data-testid=thread-row-title]`))
  expect(title1 === 'What does roundTax in src/tax.ts do?' && clean(await a.textContent(`${row(T1)} [data-testid=thread-row-title]`)) === title1,
    `the thread is under #checkout-app in Rosa's list and in Alex's (he's in #general), live: "${title1}", the first words of the post without the mention`)
  expect((await r.getAttribute(row(T1), 'title')).includes('@researcher'), "its row shows researcher's avatar, and says so on hover: " + await r.getAttribute(row(T1), 'title'))
  await until("researcher's answer", () => events('TurnEnded', T1) >= 1, LONG)
  await until('the row to say idle', async () => (await r.getAttribute(`${row(T1)} [data-testid=thread-dot]`, 'data-state')) === 'idle', 20000)
  const dots = await r.evaluate(t => window.__dots[t], String(T1))
  expect(dots.includes('starting') && dots.includes('working') && dots.at(-1) === 'idle', `its dot followed the work live: ${dots.join(' → ')}; on hover: "${await r.getAttribute(`${row(T1)} [data-testid=thread-dot]`, 'title')}"`)
  expect(/round/i.test(answer(T1)), 'researcher answered: "' + answer(T1).slice(0, 140) + '"')
  await shots(r, '1-list-and-panel')

  // --- 5. unread replies: bold with a count on Alex's row (he hasn't opened it); Rosa had it open, so hers are read ---
  await until("Alex's unread count", async () => Number(clean(await a.textContent(`${row(T1)} [data-testid=thread-unread]`).catch(() => '0'))) >= 1, 20000)
  expect(await a.locator(`${row(T1)}.unread`).count() === 1 && await r.locator(`${row(T1)} [data-testid=thread-unread]`).count() === 0 && await unreadOf(r, CH, T1) === 0,
    `Alex's row is bold with ${clean(await a.textContent(`${row(T1)} [data-testid=thread-unread]`))} unread; Rosa had the thread open, so none are unread for her`)
  await shots(a, '5-unread')

  // --- 3. a thread in the list opens as a full conversation in the main area, with its own URL ---
  await a.click(row(T1))
  await a.waitForSelector('[data-testid=thread-view] [data-testid=thread-title]')
  expect(new URL(a.url()).pathname === `/c/${CH}/threads/${T1}` && await a.locator('[data-testid=feed]').count() === 0 && await a.locator('[data-testid=thread-panel]').count() === 0,
    `Alex clicked it: the thread fills the main area (no feed beside it) at /c/${CH}/threads/${T1}`)
  await until("Alex's unread to clear", async () => await a.locator(`${row(T1)} [data-testid=thread-unread]`).count() === 0 && await unreadOf(a, CH, T1) === 0, 15000)
  ok('opening it marked its replies read (the count went, and the server says 0)')
  await a.waitForSelector('[data-testid=thread-view] [data-testid=tool-row]', { timeout: 20000 })
  const header = clean(await a.textContent('[data-testid=thread-view] header'))
  expect(clean(await a.textContent('[data-testid=thread-view] [data-testid=thread-title]')) === title1 && header.includes('in checkout-app') && header.includes('team template') && header.includes('sandbox on pool-01') && header.includes('started by Rosa'),
    'its header: the title, "in #checkout-app", the session line (' + clean(await a.textContent('[data-testid=thread-view] [data-testid=session-line]')) + ')')
  expect(await a.locator(`[data-testid=thread-view] [data-post="${T1}"]`).count() === 1 && await a.locator('[data-testid=thread-view] [data-testid=tool-row]').count() >= 1 && await a.locator('[data-testid=thread-view] [data-testid=composer]').count() === 1,
    'the opening post, the reply and its tool rows (' + (await a.$$eval('[data-testid=thread-view] [data-testid=tool-row]', els => els.map(e => e.textContent.replace(/\s+/g, ' ').trim()))).join(' | ') + '), and its own composer')
  await a.reload(); await a.waitForSelector('[data-testid=thread-view] [data-testid=thread-title]')
  expect(clean(await a.textContent('[data-testid=thread-view] [data-testid=thread-title]')) === title1, 'reloaded, the URL opens the same full view')
  await a.click('[data-testid=thread-channel-link]')
  await a.waitForSelector(`[data-testid=feed] [data-post="${T1}"][data-focused=true]`)
  ok('"in #checkout-app" goes back to the post in the channel, lit for a moment')

  // --- 2. resolved: its title is the outcome's; Rosa renames it from the full view and Alex's list follows ---
  await r.click('[data-testid=open-full-view]')
  await r.waitForSelector('[data-testid=thread-view] [data-testid=thread-title]')
  expect(new URL(r.url()).pathname === `/c/${CH}/threads/${T1}`, 'Rosa\'s quick look has "Open full view", which opens it in the main area')
  await r.fill('[data-testid=thread-view] [data-testid=composer]', '@researcher thanks. Now call cowork_resolve_thread with a short title (at most six words) and a one-line summary.')
  await r.keyboard.press('Enter')
  await until('the thread to be resolved', () => events('ThreadResolved', T1) >= 1, LONG)
  await r.waitForSelector('[data-testid=thread-view] [data-testid=outcome-card]')
  const outcome = sql(`select json_extract(data,'$.title') from events where type='ThreadResolved' and stream_id='thread-${T1}'`)[0][0]
  await until('the outcome title in the header and in both lists', async () => clean(await r.textContent('[data-testid=thread-view] [data-testid=thread-title]')) === outcome
    && clean(await a.textContent(`${row(T1)} [data-testid=thread-row-title]`)) === outcome, 20000)
  ok(`resolved: the outcome card in the full view, and its title "${outcome}" in the header and both lists, live`)
  await until('the done dot', async () => (await a.getAttribute(`${row(T1)} [data-testid=thread-dot]`, 'data-state')) === 'done', 30000)
  ok('its dot says done')
  await until("researcher's turn to end", () => events('TurnEnded', T1) >= 2, LONG)
  await r.evaluate(() => { const el = document.querySelector('[data-testid=thread-view] .thin-scrollbar'); if (el) el.scrollTop = el.scrollHeight })
  await shots(r, '2-full-view')
  // the title is the full view's heading, edited in place: Esc cancels, an empty title is refused, Enter saves
  const titleIn = async (p, where) => clean(await p.locator(`${where} [data-testid=thread-title]`).textContent())
  await r.click('[data-testid=thread-view] [data-testid=thread-title]')
  await r.fill('[data-testid=thread-view] [name=threadTitle]', 'Never mind'); await r.keyboard.press('Escape')
  await r.waitForSelector('[data-testid=thread-view] [data-testid=thread-title]')
  expect(await titleIn(r, '[data-testid=thread-view]') === outcome && events('ThreadRenamed', T1) === 0, 'clicking the heading edits the title in place; Esc cancels and nothing is saved')
  await r.click('[data-testid=thread-view] [data-testid=rename-thread]')
  await r.fill('[data-testid=thread-view] [name=threadTitle]', '   '); await r.keyboard.press('Enter')
  await r.waitForSelector('[data-testid=title-error]')
  expect(clean(await r.textContent('[data-testid=title-error]')) === 'A thread needs a title.' && await r.locator('[data-testid=thread-view] [name=threadTitle]').count() === 1 && events('ThreadRenamed', T1) === 0,
    'an empty title is refused: "A thread needs a title.", and it stays in the editor')
  await r.fill('[data-testid=thread-view] [name=threadTitle]', 'roundTax rounds each line'); await r.keyboard.press('Enter')
  await until("Alex's list and the post's chip to show the new name", async () => clean(await a.textContent(`${row(T1)} [data-testid=thread-row-title]`)) === 'roundTax rounds each line'
    && clean(await a.textContent(`[data-post="${T1}"] [data-card-title]`)) === 'roundTax rounds each line', 15000)
  expect(await titleIn(r, '[data-testid=thread-view]') === 'roundTax rounds each line' && events('ThreadRenamed', T1) === 1 && sql(`select actor_id from events where type='ThreadRenamed'`)[0][0] === rosaId,
    "Enter saves it (ThreadRenamed, by Rosa); Alex's list row and the post's \"N replies\" chip follow, live")

  // --- 1 (cont). five threads per channel, most recent activity first, then All threads; the chevron folds them, remembered ---
  const people = []
  for (const [i, text] of ['Release notes for 2.3', 'Coupon codes at checkout', 'Cart badge after a refund', 'Order confirmation copy', 'Refund emails'].entries()) {
    const root = (await api(a, 'POST', `/channels/${CH}/posts`, { body: text })).id
    await api(a, 'POST', `/channels/${CH}/posts`, { body: `Notes on ${text.toLowerCase()} go here.`, threadId: root })
    people.push(root)
  }
  await until("Rosa's list to show five", async () => (await rowsIn(r, 'checkout-app')).length === 5 && (await rowsIn(r, 'checkout-app'))[0] === people[4], 15000)
  expect(JSON.stringify(await rowsIn(r, 'checkout-app')) === JSON.stringify([...people].reverse()) && clean(await r.textContent('[data-threads=checkout-app] [data-testid=all-threads]')) === 'All threads · 1 more',
    'Alex started five more threads: Rosa\'s list shows the five most recent, newest first, then "All threads · 1 more"')
  await api(a, 'POST', `/channels/${CH}/posts`, { body: 'One more note.', threadId: people[0] })
  await until('the oldest to move up', async () => (await rowsIn(r, 'checkout-app'))[0] === people[0], 15000)
  ok('a reply in the oldest moves it to the top (most recent activity first)')
  // anyone who may post in a thread may rename it: Rosa, from the quick look of Alex's thread she never wrote in
  await r.click('[data-channel=checkout-app]'); await r.click(`[data-post="${people[1]}"] [data-testid=thread-card]`)
  await r.waitForSelector('[data-testid=thread-panel] [data-testid=thread-title]:has-text("Coupon codes at checkout")')
  await r.click('[data-testid=thread-panel] [data-testid=thread-title]')
  await r.fill('[data-testid=thread-panel] [name=threadTitle]', 'Coupon copy for checkout'); await r.keyboard.press('Enter')
  await until('the panel heading and Alex\'s list', async () => await titleIn(r, '[data-testid=thread-panel]') === 'Coupon copy for checkout'
    && clean(await a.textContent(`[data-threads=checkout-app] [data-thread="${people[1]}"] [data-testid=thread-row-title]`)) === 'Coupon copy for checkout', 15000)
  ok("the quick look's heading is the title too: Rosa renamed Alex's thread there (she can post in it), and Alex's list follows")
  const audit = await until('the audit line', async () => ((await api(a, 'GET', '/audit')).entries ?? []).map(e => e.sentence).find(t => t.includes('renamed a thread')))
  const found = await until('search to find it by its title', async () => ((await api(r, 'GET', '/search?q=coupon+copy')).threads?.items ?? []).find(t => t.rootId === people[1]))
  expect(found.title === 'Coupon copy for checkout' && (await api(a, 'GET', '/audit')).entries.some(e => e.sentence === 'Rosa renamed a thread in #checkout-app to “Coupon copy for checkout”.'),
    `search finds the thread by its title and shows it ("${found.title}"); the audit says "${audit}"`)
  await r.click('[data-fold=checkout-app]')
  await until('the threads to fold', async () => await r.locator('[data-threads=checkout-app]').count() === 0, 5000)
  await r.reload(); await r.waitForSelector('[data-testid=thread-panel] [data-testid=thread-title]')
  await r.waitForTimeout(800)
  expect(await r.locator('[data-threads=checkout-app]').count() === 0 && await r.getAttribute('[data-fold=checkout-app]', 'aria-expanded') === 'false', "#checkout-app's chevron folds its threads away, and it's remembered after a reload")
  await r.click('[data-fold=checkout-app]'); await r.waitForSelector('[data-threads=checkout-app] [data-testid=thread-row]')

  // --- private channels: their threads are only in their members' lists ---
  const secret = (await api(a, 'POST', `/channels/${leads}/posts`, { body: 'Q4 refund budget' })).id
  await api(a, 'POST', `/channels/${leads}/posts`, { body: 'It is 40k, not for sharing yet.', threadId: secret })
  await a.waitForSelector(`[data-threads=leads] [data-thread="${secret}"]`)
  await r.waitForTimeout(1000)
  expect(await r.locator('[data-channel=leads]').count() === 0 && !JSON.stringify(await api(r, 'GET', '/thread-list')).includes(`"rootId":${secret}`) && (await api(r, 'GET', `/channels/${leads}/threads`)).error === 'No such channel.',
    "a thread in Alex's private #leads is in his list and nowhere in Rosa's (the API says 404 to her)")

  // --- 4 + waiting: a thread waiting for Rosa says "waiting for you" to her and "waiting for Rosa" to Alex; All threads and its filters ---
  await r.click('[data-channel=checkout-app]'); await r.waitForSelector('[data-testid=feed]')
  await post(r, '@researcher Fetch https://example.com with your webfetch tool and tell me the page title.')
  const T2 = await threadOf('Fetch https://example.com')
  await until('the ask', () => sql(`select count(*) from posts where thread_id=${T2} and kind='ask'`)[0][0] >= 1, LONG)
  await until('the waiting dots', async () => (await r.getAttribute(row(T2), 'title').catch(() => ''))?.includes('waiting for you') && (await a.getAttribute(row(T2), 'title').catch(() => ''))?.includes('waiting for Rosa'), 20000)
  expect(await r.getAttribute(`${row(T2)} [data-testid=thread-dot]`, 'data-state') === 'waiting-you' && await a.getAttribute(`${row(T2)} [data-testid=thread-dot]`, 'data-state') === 'waiting',
    'researcher asked to reach example.com: Rosa\'s row says "waiting for you" (amber, pulsing), Alex\'s "waiting for Rosa"')
  await shots(r, '4-waiting-for-you')
  await r.click('[data-testid=channel-threads]')
  await r.waitForSelector('[data-testid=threads-view] [data-testid=threads-row]')
  const allRows = await r.$$eval('[data-testid=threads-row]', els => els.map(e => Number(e.dataset.thread)))
  expect(allRows.length === 7 && allRows.includes(T1) && allRows.includes(T2) && new URL(r.url()).pathname === `/c/${CH}/threads`, `the Threads button opens All threads: all ${allRows.length} of #checkout-app's threads`)
  expect(clean(await r.textContent(`[data-testid=threads-row][data-thread="${people[1]}"] [data-testid=threads-row-title]`)) === 'Coupon copy for checkout'
    && clean(await r.textContent(`[data-testid=threads-row][data-thread="${T1}"] [data-testid=threads-row-title]`)) === 'roundTax rounds each line', 'All threads shows the same titles')
  const firstRow = clean(await r.textContent(`[data-testid=threads-row][data-thread="${T2}"]`))
  expect(firstRow.includes('@researcher') && firstRow.includes('started by Rosa') && firstRow.includes('waiting for you') && firstRow.includes('last activity'), 'each with its title, agent, who started it, status and last activity: ' + firstRow)
  const filtered = async (p, f) => { await p.click(`[data-filter=${f}]`); await p.waitForTimeout(600); return p.$$eval('[data-testid=threads-row]', els => els.map(e => Number(e.dataset.thread))) }
  const waiting = await filtered(r, 'waiting'), done = await filtered(r, 'done'), open = await filtered(r, 'open')
  expect(JSON.stringify(waiting) === JSON.stringify([T2]) && JSON.stringify(done) === JSON.stringify([T1]) && open.length === 6 && !open.includes(T1),
    `filters: waiting for me → Rosa's ask only; done → the resolved one; open → the other ${open.length}`)
  await r.click('[data-filter=waiting]'); await shots(r, '3-all-threads')
  await a.goto(`${BASE}/c/${CH}/threads`); await a.waitForSelector('[data-testid=threads-row]')
  expect((await filtered(a, 'waiting')).length === 0 && clean(await a.textContent('[data-testid=threads-empty]')).startsWith('Nothing is waiting for you'), "Alex's \"waiting for me\" is empty: it waits for Rosa")
  await r.goto(`${BASE}/c/${CH}/threads/${T2}`)
  await r.waitForSelector('[data-testid=thread-view] [data-testid=ask-card] button:has-text("Deny")')
  await r.click('[data-testid=thread-view] [data-testid=ask-card] button:has-text("Deny")')
  await until('the dot to stop waiting', async () => (await r.getAttribute(`${row(T2)} [data-testid=thread-dot]`, 'data-state')) !== 'waiting-you', 20000)
  ok('Rosa answered the ask in the full view, and her row stopped waiting')

  // --- 6. a mention that can't reach the agent says so, in a new thread ---
  const BILL = channel('billing')
  await r.goto(`${BASE}/c/${BILL}`); await r.waitForSelector('[data-testid=channel-title]:has-text("billing")')
  await post(r, '@researcher can you check the invoice totals?')
  const T3 = await threadOf('check the invoice totals')
  await r.waitForSelector('[data-testid=thread-panel] [data-testid=notice]')
  const notice = clean(await r.textContent('[data-testid=thread-panel] [data-testid=notice]'))
  expect(notice === "researcher: researcher isn't in Billing. A lead can add it in the project's settings." && events('MentionUnreachable', T3) === 1 && events('SandboxRequested', T3) === 0,
    'mentioning researcher in #billing (it isn\'t in Billing): "' + notice + '", and no sandbox')
  await r.waitForSelector(`[data-threads=billing] [data-thread="${T3}"]`)
  await shots(r, '6-refused-mention')

  // --- 7. added to a project with no repository: its sandbox says so in its starting rows ---
  await a.goto(`${BASE}/p/${billing.id}`); await a.waitForSelector('[data-testid=project-settings-page] [data-testid=project-title]:has-text("Billing")')
  await a.click('[data-testid=add-agent]'); await a.waitForSelector('[data-testid=boundary] [data-line=stops]')
  await a.click('[data-testid=add-agent-confirm]'); await a.waitForSelector('[data-agent-row=researcher]')
  await r.click('[aria-label="Close thread"]')
  await post(r, '@researcher what is 17 times 3?')
  const T4 = await threadOf('what is 17 times 3')
  await r.waitForSelector('[data-testid=thread-panel] [data-testid=sandbox-card]')
  await until('the no-repository row', async () => clean(await r.textContent('[data-testid=thread-panel] [data-testid=sandbox-card]')).includes("No repository: add one in the project's settings to give researcher something to work on."), 20000)
  ok("Alex added researcher to Billing (no repository); Rosa's next mention starts a sandbox whose starting rows say: \"No repository: add one in the project's settings to give researcher something to work on.\"")
  await until('the answer in #billing', () => events('TurnEnded', T4) >= 1, LONG)
  expect(clean(await r.textContent('[data-testid=thread-panel] [data-testid=sandbox-card]')).includes('Made an empty workspace. No repository') && /51/.test(answer(T4)),
    'started: "Made an empty workspace. No repository: …", and it answered: "' + answer(T4).slice(0, 80) + '"')
  await shots(r, '7-no-repository')

  // --- the phone: threads in the drawer, a thread fills the screen ---
  const P = await person(browser, { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true })
  const ph = P.page
  await ph.goto(BASE); await ph.fill('[name=username]', 'rosa'); await ph.fill('[name=password]', 'rosa-password'); await ph.click('button[type=submit]')
  await ph.waitForSelector('[data-testid=phone-menu]')
  await ph.click('[data-testid=phone-menu]'); await ph.waitForSelector('[data-testid=phone-nav] [data-threads=checkout-app] [data-testid=thread-row]')
  await shots(ph, '8-phone-list')
  await ph.click('[data-testid=phone-nav] [data-threads=checkout-app] [data-testid=thread-row] >> nth=0')
  await ph.waitForSelector('[data-testid=thread-view] [data-testid=thread-title]')
  const fills = await ph.evaluate(() => { const el = document.querySelector('[data-testid=thread-view]'); const b = el.getBoundingClientRect(); return b.width >= window.innerWidth - 2 && !document.querySelector('[data-testid=phone-nav]') && !document.querySelector('[data-testid=feed]') })
  const wraps = await ph.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)
  expect(fills && wraps, 'on a phone the list is in the drawer, and a thread fills the screen (nothing wider than it)')
  await shots(ph, '9-phone-full-view')
  console.log('PHASE 12 LIVE CHECK PASSED')
} catch (e) {
  console.log('FAILED', e.message)
  for (const [p, n] of [[a, 'alex'], [r, 'rosa']]) await shot(p, 'FAIL-' + n).catch(() => {})
  if (server?.log) console.log(server.log.split('\n').filter(l => /fail|error|exception|warn/i.test(l)).slice(-20).join('\n'))
  process.exitCode = 1
} finally {
  await stopGroup(pool); await stopGroup(server)
  await browser.close()
  // twice: a container start already on its way when the pool stopped can land after the first sweep
  for (const pass of [1, 2]) { try { cleanupPodman() } catch (e) { console.log('cleanup:', e.message) } if (pass === 1) await new Promise(r => setTimeout(r, 5000)) }
  if (poolKey) console.log(`removed every container and volume labelled cowork.pool=${poolKey}`)
}
