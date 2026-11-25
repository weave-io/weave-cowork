// Brief 9: artifacts and markdown. Browsers for Alex (admin, Checkout app's lead), Rosa and Sam (not in Checkout app); a REAL pool with REAL
// containers and REAL OpenCode 2.4.1 (the researcher template), and a REAL runner with --host build-01 serving Alex's pinned reviewer with
// REAL OpenCode 2.4.1. Model calls go to this machine's GitHub Copilot sign-in (the user agreed, 25 Nov 2026).
// The demo: Rosa asks researcher for a report she can share with finance; its answer renders as markdown (a heading, a list, a table, inline
// code, a link that opens in a new tab) → it writes reports/tax-apis/index.html with a CSS file and an image beside it and calls cowork_show:
// a card in its message → Rosa, who started the thread and is looking at it, gets the canvas beside the thread; Alex, also looking, only the
// card, and clicking it opens the canvas for him → tabs, the version picker, full screen, copy link, download, close, "Open full view" →
// Rosa asks for a change under the canvas: her reply quotes the artifact and its version; researcher edits the file and shows it again: v2,
// the card and the open tabs follow, v1 is still in the picker → Alex's pinned reviewer shows a markdown document from its worktree → from
// inside the report, document.cookie, a fetch to /api/… and a fetch to any other host all fail; Sam gets 404 for the artifact → on a phone the
// canvas fills the screen with a back button.
// Starts its own server (default port 5885). Everything in podman carries the pool's label and is removed by it at the end.
//   node tests/live/phase13.mjs          (screenshots go to docs/screens/brief9/)
import { launch, person, expect, ok, freshHarnessHome, MODEL, SANDBOX_IMAGE } from './lib.mjs'
import { spawn, execFileSync } from 'node:child_process'
import { homedir } from 'node:os'
import { rmSync, mkdirSync, writeFileSync, readFileSync } from 'node:fs'

const HOME = homedir()
const PORT = process.env.PORT ?? '5885'
const BASE = `http://127.0.0.1:${PORT}`
const REPO = new URL('../../', import.meta.url).pathname.replace(/\/$/, '')
const DIR = `${HOME}/.cache/cowork-p13`
const DB = `${DIR}/data/cowork.db`
const SHOTS = process.env.SHOTS_DIR ?? `${REPO}/docs/screens/brief9/`
const SERVER = `${REPO}/src/CoWork.Server/bin/Debug/net10.0/CoWork.Server.dll`
const RUNNER = `${REPO}/src/CoWork.Runner/bin/Debug/net10.0/cowork-runner.dll`
const HARNESS = `${HOME}/.weave/harnesses/opencode2/.opencode/bin/opencode acp`
const IMAGE = SANDBOX_IMAGE              // never the trial's own tag (see lib.mjs)
const LONG = 300000
rmSync(DIR, { recursive: true, force: true }); mkdirSync(`${DIR}/data`, { recursive: true }); mkdirSync(SHOTS, { recursive: true })
const SCRATCH = freshHarnessHome('p13')            // the pinned runner's own home and OpenCode data, deleted at the end
writeFileSync(`${SCRATCH.dir}/config/opencode/opencode.json`, JSON.stringify({ $schema: 'https://opencode.ai/config.json', permission: { edit: 'allow', bash: 'deny', webfetch: 'deny' }, ...MODEL }))
const sql = (q) => JSON.parse(execFileSync('python3', ['-I', '-c', 'import sqlite3,sys,json;c=sqlite3.connect(sys.argv[1]);print(json.dumps(c.execute(sys.argv[2]).fetchall()))', DB, q]).toString())
const until = async (what, probe, ms = 60000) => { const end = Date.now() + ms; for (;;) { const v = await probe(); if (v) return v; if (Date.now() > end) throw new Error('FAILED: timed out waiting for ' + what); await new Promise(r => setTimeout(r, 250)) } }
const clean = (t) => (t ?? '').replace(/\s+/g, ' ').trim()
const podman = (...a) => execFileSync('podman', a, { stdio: ['ignore', 'pipe', 'pipe'] }).toString().trim()
const git = (cwd, ...args) => execFileSync('git', args, { cwd, env: { ...process.env, GIT_AUTHOR_NAME: 'Alex', GIT_AUTHOR_EMAIL: 'alex@lumen.example', GIT_COMMITTER_NAME: 'Alex', GIT_COMMITTER_EMAIL: 'alex@lumen.example' } }).toString().trim()

if (!podman('images', '-q', IMAGE)) { console.log(`FAILED: the image ${IMAGE} isn't built here. Run TAG=${IMAGE} deploy/sandbox/build.sh first.`); process.exit(1) }
const seed = `${DIR}/seed`; mkdirSync(`${seed}/src`, { recursive: true })
git(seed, 'init', '-q', '-b', 'main')
writeFileSync(`${seed}/README.md`, '# checkout-app\n\nCart, payment and order confirmation for the Lumen Labs web shop.\n')
writeFileSync(`${seed}/src/tax.ts`, 'export function roundTax(lines: number[]): number {\n  // rounds each line, then sums\n  return lines.reduce((sum, l) => sum + Math.round(l * 100) / 100, 0)\n}\n')
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
for (const P of [A, R]) await P.ctx.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: BASE })
const a = A.page, r = R.page, s = S.page
const shot = async (p, name) => { await p.waitForTimeout(500); await p.screenshot({ path: SHOTS + name + '.png' }) }
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
const channel = () => sql("select id from channels where name='checkout-app'")[0][0]
const memberId = (handle) => sql(`select id from members where handle='${handle}'`)[0][0]
// a template's asks go to whoever started the thread: Rosa allows its edits (every time), never a host it wants to reach
async function allowEdits(p) {
  const btn = p.locator('[data-testid=thread-view] [data-testid=ask-card]:not(:has([data-testid=ask-egress])) button[data-answer]')
  const n = await btn.count()
  if (n) await btn.nth(n - 1).click().catch(() => {})
}
const frameOf = (p) => p.frames().find(f => /\/a\/\d+-\d+-\d+-[\w-]+\//.test(f.url()) && f.parentFrame() && !f.url().includes('about:'))
const canvasFrame = async (p) => { await p.waitForSelector('[data-testid=canvas] [data-testid=artifact-frame]'); const h = await p.$('[data-testid=canvas] [data-testid=artifact-frame]'); const f = await h.contentFrame(); await f.waitForLoadState('load'); return f }

try {
  await startServer()
  await a.goto(BASE)
  await a.fill('[name=serverName]', 'Lumen Labs'); await a.fill('[name=username]', 'alex'); await a.fill('[name=displayName]', 'Alex'); await a.fill('[name=password]', 'correct horse')
  await a.click('button[type=submit]'); await a.waitForSelector('[data-channel=general]')
  await join(r, 'rosa', 'Rosa'); await join(s, 'sam', 'Sam')
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-where]'); await a.click('[data-testid=add-pool]')
  const command = clean(await a.textContent('[data-testid=pool-command]'))
  pool = spawnLogged([...command.split(' ').slice(1), '--host', 'build-02'], { ...process.env, COWORK_RUNNER_CONFIG: `${DIR}/pool/config`, COWORK_RUNNER_DATA: `${DIR}/pool/data` }, 'pool')
  await a.waitForSelector('[data-testid=pool-connected]', { timeout: 30000 }); await a.keyboard.press('Escape')
  poolKey = /labels cowork\.pool=(\S+);/.exec(pool.log)?.[1]
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-server-settings]')
  await a.click('[data-testid=provider-copilot]'); await a.click('[data-testid=save-models]'); await a.waitForSelector('[data-testid=models-saved]')
  await a.keyboard.press('Escape')
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-invite-agent]'); await a.click('[data-testid=runs-on-pool]'); await a.fill('[name=image]', IMAGE)
  await a.fill('[name=handle]', 'researcher'); await a.fill('[name=about]', 'Researches and writes reports')
  await a.fill('[name=instructions]', 'You are researcher, on the Lumen Labs checkout app. Be brief in chat. When you make a report or document, write it as files in your workspace and show it with CoWork\'s cowork_show tool. Never look anything up on the internet; make up plausible figures when asked.')
  await a.click('[data-testid=create-agent]'); await a.waitForSelector('[data-testid=template-created]'); await a.keyboard.press('Escape')
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-invite-agent]')
  await a.fill('[name=handle]', 'reviewer'); await a.fill('[name=about]', 'Reviews the checkout app')
  await a.fill('[name=instructions]', 'You are reviewer, on the Lumen Labs checkout app. Be brief in chat. When you make a document, write it as a file in your workspace and show it with CoWork\'s cowork_show tool.')
  await a.fill('[name=harness]', HARNESS); await a.fill('[name=workspace]', `${SCRATCH.dir}/own-workspace`)
  await a.click('[data-testid=create-agent]'); await a.waitForSelector('[data-testid=runner-command]')
  const parts = clean(await a.textContent('[data-testid=runner-command]')).split(' ')
  runner = spawnLogged(['connect', BASE, '--code', parts[parts.length - 1], '--host', 'build-01'], SCRATCH.env, 'runner')
  await a.waitForSelector('[data-testid=agent-connected]', { timeout: 30000 }); await a.click('button:has-text("Skip")')
  // Checkout app: Alex leads it, Rosa is in it, Sam isn't
  await a.click('[data-testid=new-project]')
  await a.fill('[name=projectName]', 'Checkout app'); await a.fill('[name=about]', 'Cart, payment and order confirmation')
  await a.click('[data-testid=start-repository]'); await a.fill('[name=repoUrl]', BARE); await a.fill('[name=branch]', 'main')
  await a.click('[data-testid=add-person]'); await a.click('[role=option]:has-text("Rosa")')
  await a.click('[data-testid=create-project]')
  await a.waitForSelector('[data-testid=project-settings-page] [data-testid=project-title]:has-text("Checkout app")')
  for (const handle of ['researcher', 'reviewer']) {
    await a.click('[data-testid=add-agent]'); await a.waitForSelector('[data-testid=boundary] [data-line=stops]')
    if (await a.locator(`[data-pick-agent=${handle}]`).count()) { await a.click(`[data-pick-agent=${handle}]`); await a.waitForTimeout(400) }
    await a.click('[data-testid=add-agent-confirm]'); await a.waitForSelector(`[data-agent-row=${handle}]`)
  }
  for (const p of [r, a]) { await p.goto(`${BASE}/c/${channel()}`); await p.waitForSelector('[data-testid=channel-title]:has-text("checkout-app")') }
  ok('set up: pool-01 and Copilot; researcher (a template) and reviewer (pinned on build-01) in Checkout app; Rosa is in it, Sam isn\'t')

  // --- 1 and 2. Rosa asks for a report; the answer is markdown; researcher writes a page with its CSS and an image and shows it ---
  await r.fill('[data-testid=composer]', '@researcher Compare three tax APIs (Tallyrate, Levy.io and Vatbook) for EU refunds and make a report I can share with finance. ' +
    'Write it as an HTML page at reports/tax-apis/index.html, with its styles in reports/tax-apis/style.css and a small bar chart of the cost per 1,000 calls as an SVG image at reports/tax-apis/chart.svg (shown with <img src="chart.svg">). ' +
    'Then call cowork_show with path reports/tax-apis/index.html and title "Tax APIs compared". Make the figures up. ' +
    'In your chat answer use markdown: a level-3 heading, a bullet list, a table of the three providers, the function name roundTax as inline code, and a link to https://docs.tallyrate.example')
  await r.keyboard.press('Enter')
  const thread = await threadOf('Compare three tax APIs')
  await r.waitForSelector('[data-testid=thread-panel]'); await r.click('[data-testid=open-full-view]')
  await r.waitForSelector('[data-testid=thread-view]')
  await a.goto(`${BASE}/c/${channel()}/threads/${thread}`); await a.waitForSelector('[data-testid=thread-view]')
  ok(`Rosa and Alex are both looking at the thread's full view (/c/${channel()}/threads/${thread})`)
  await until('researcher to show the report', async () => { await allowEdits(r); return events('ArtifactPublished', thread) >= 1 }, LONG)
  await until("researcher's turn to end", async () => { await allowEdits(r); return events('TurnEnded', thread) >= 1 }, LONG)
  const published = JSON.parse(sql(`select data from events where type='ArtifactPublished' and stream_id='thread-${thread}'`)[0][0])
  const artifact = published.artifactId
  expect(published.path === 'reports/tax-apis/index.html' && published.kind === 'html' && published.version === 1 && published.files.length >= 3,
    `cowork_show: ${published.path} (${published.kind}, v${published.version}) with ${published.files.map(f => f.path).join(', ')}, each by SHA-256 (${published.files[0].sha256.slice(0, 12)}…)`)
  expect(published.files.some(f => f.path.endsWith('.css')) && published.files.some(f => /\.(svg|png|jpe?g|gif|webp)$/.test(f.path)), 'the CSS file and the image beside the page went with it')
  // the answer, as markdown
  const researcherPosts = r.locator('[data-testid=thread-view] [data-post]', { has: r.locator('b:text-is("researcher")') })
  await r.waitForSelector('[data-testid=thread-view] [data-post] .body h3')
  const md = await researcherPosts.evaluateAll(els => {
    const q = (sel) => els.some(e => e.querySelector('.body ' + sel))
    const link = els.flatMap(e => [...e.querySelectorAll('.body a')]).find(x => x.href.startsWith('https://docs.tallyrate.example'))
    return { h3: q('h3'), list: q('ul li, ol li'), table: q('table th'), code: els.some(e => [...e.querySelectorAll('.body code')].some(c => /roundTax/.test(c.textContent))), link: link ? `${link.target}|${link.rel}` : null }
  })
  expect(md.h3 && md.list && md.table && md.code && md.link?.startsWith('_blank|') && md.link.includes('noopener'),
    `researcher's answer renders as markdown: a heading, a list, a table, inline code (roundTax), a link that opens in a new tab (${JSON.stringify(md)})`)
  // the card in its message
  const card = r.locator('[data-testid=thread-view] [data-testid=artifact-card]').first()
  await card.waitFor()
  expect(clean(await card.locator('[data-testid=artifact-title]').textContent()) === 'Tax APIs compared' && clean(await card.locator('[data-testid=kind-chip]').textContent()) === 'html'
    && /^page · v1 · \d+ KB · shown \d/.test(clean(await card.locator('[data-testid=artifact-meta]').textContent())),
    `a card in researcher's message: "Tax APIs compared", html, ${clean(await card.locator('[data-testid=artifact-meta]').textContent())}`)
  await card.locator('[data-testid=artifact-thumb]').waitFor()
  ok('the card has a thumbnail of the page')

  // --- 3. Rosa started it and is looking: the canvas opened by itself. Alex sees only the card; clicking it opens the canvas for him ---
  await r.waitForSelector(`[data-testid=canvas][data-artifact="${artifact}"]`)
  const rf = await canvasFrame(r)
  const inside = await rf.evaluate(() => ({ h: document.querySelector('h1,h2')?.textContent ?? '', css: [...document.styleSheets].length, img: [...document.images].map(i => i.naturalWidth), origin: self.origin }))
  expect(inside.h.length > 0 && inside.css >= 1 && inside.img.length > 0 && inside.img.every(w => w > 0),
    `Rosa's canvas opened by itself beside the thread with the report: "${clean(inside.h)}", its stylesheet and its image loaded (${inside.img.length} image(s))`)
  expect(await r.locator('[data-testid=canvas-tab]').count() === 1 && clean(await r.textContent('[data-testid=canvas-title]')) === 'Tax APIs compared', 'one tab, the report')
  await shots(r, '01-rosa-canvas')
  expect(await a.locator('[data-testid=canvas]').count() === 0 && await a.locator('[data-testid=thread-view] [data-testid=artifact-card]').count() >= 1, 'Alex, also looking, sees only the card')
  await shots(a, '02-alex-card')
  await a.click('[data-testid=thread-view] [data-testid=artifact-card] [data-testid=artifact-open]')
  await a.waitForSelector(`[data-testid=canvas][data-artifact="${artifact}"]`)
  await canvasFrame(a)
  expect(new URL(a.url()).searchParams.get('a') === String(artifact), `clicking the card opened the canvas for Alex (the address says ?a=${artifact})`)

  // --- 4. the canvas: version picker, full screen, copy link, download, close, its own page ---
  expect((await a.$$eval('[data-testid=canvas-version] option', os => os.map(o => o.textContent.trim()))).length === 1, 'the version picker has v1')
  await a.click('[data-testid=canvas-full]')
  await until('full screen', async () => !(await a.isVisible('[data-testid=thread-view]')), 5000)
  ok('full screen: the canvas has the whole area')
  await shots(a, '03-full-screen')
  await a.click('[data-testid=canvas-full]'); await a.waitForSelector('[data-testid=thread-view]')
  await a.click('[data-testid=canvas-copy]'); await a.waitForSelector('[data-testid=canvas-copy][data-copied]')
  const link = await a.evaluate(() => navigator.clipboard.readText())
  expect(link === `${BASE}/c/${channel()}/threads/${thread}?a=${artifact}`, 'copy link: ' + link)
  const [dl] = await Promise.all([a.waitForEvent('download'), a.click('[data-testid=canvas-download]')])
  expect(dl.suggestedFilename() === 'tax-apis-compared-v1.zip', 'download: ' + dl.suggestedFilename())
  await a.click('[data-testid=canvas-page]')
  await a.waitForSelector('[data-testid=artifact-page] [data-testid=artifact-frame]')
  expect(new URL(a.url()).pathname === `/artifacts/${artifact}` && clean(await a.textContent('[data-testid=artifact-page-title]')) === 'Tax APIs compared', '"Open full view": the artifact on its own page at /artifacts/' + artifact)
  await shots(a, '04-artifact-page')
  await a.click('[data-testid=artifact-back]')
  await a.waitForSelector(`[data-testid=canvas][data-artifact="${artifact}"]`)
  await a.goto(link); await a.waitForSelector(`[data-testid=canvas][data-artifact="${artifact}"]`)
  ok('back to the thread, and the copied link opens the canvas too')
  await a.click('[data-testid=canvas-close]')
  await until('the canvas to close', async () => await a.locator('[data-testid=canvas]').count() === 0, 5000)
  expect(!new URL(a.url()).searchParams.has('a'), 'close: the canvas is gone, and so is ?a= from the address')
  await a.click('[data-testid=thread-view] [data-testid=artifact-card]'); await a.waitForSelector('[data-testid=canvas]')

  // --- 7. the report can't reach CoWork; Sam can't see it ---
  const probe = await rf.evaluate(async (base) => {
    const out = {}
    try { out.cookie = 'read: ' + JSON.stringify(document.cookie) } catch (e) { out.cookie = 'refused (' + e.name + ')' }
    try { await fetch('/api/bootstrap'); out.api = 'reached' } catch { out.api = 'refused' }
    try { await fetch(base + '/api/bootstrap', { credentials: 'include' }); out.apiAbs = 'reached' } catch { out.apiAbs = 'refused' }
    try { await fetch('https://example.com/'); out.other = 'reached' } catch { out.other = 'refused' }
    try { localStorage.length; out.storage = 'read' } catch (e) { out.storage = 'refused (' + e.name + ')' }
    out.origin = self.origin
    return out
  }, BASE)
  expect(probe.cookie.startsWith('refused') && probe.api === 'refused' && probe.apiAbs === 'refused' && probe.other === 'refused' && probe.origin === 'null',
    `from inside the report: document.cookie ${probe.cookie}, fetch /api/… ${probe.api} (and ${BASE}/api/… ${probe.apiAbs}), fetch https://example.com ${probe.other}, storage ${probe.storage}, its origin "${probe.origin}"`)
  const samApi = await s.evaluate(async (id) => (await fetch(`/api/artifacts/${id}`)).status, artifact)
  const samRaw = await s.evaluate(async (id) => (await fetch(`/api/artifacts/${id}/versions/1/download`)).status, artifact)
  await s.goto(`${BASE}/artifacts/${artifact}`); await s.waitForSelector('[data-testid=artifact-page] :text("No such artifact")')
  expect(samApi === 404 && samRaw === 404, `Sam, not in #checkout-app, gets 404 for the artifact (${samApi}) and its download (${samRaw}), and its page says "No such artifact"`)
  await shots(s, '09-sam-no-such-artifact')

  // --- 5. Rosa asks for a change under the canvas; researcher shows v2; the card and the open tabs follow; v1 stays ---
  await r.fill('[data-testid=canvas-ask]', 'add the cost for 50,000 refunds a month')
  await r.click('[data-testid=canvas-send]')
  const quote = r.locator('[data-testid=thread-view] [data-testid=post-quote]').last()
  await quote.waitFor()
  expect(clean(await quote.textContent()) === 'About Tax APIs compared · v1', 'her message is posted in the thread as her reply, quoting the artifact and its version: ' + clean(await quote.textContent()))
  const heard = sql(`select json_extract(data,'$.text') from events where type='MessageDelivered' and stream_id='thread-${thread}' order by position desc limit 1`)[0][0]
  expect(heard.includes('artifact="Tax APIs compared" version="1" path="reports/tax-apis/index.html"'), 'researcher hears which artifact, which version and where it is in its workspace')
  await until('researcher to show v2', async () => { await allowEdits(r); return events('ArtifactPublished', thread) >= 2 }, LONG)
  await until("researcher's second turn to end", async () => { await allowEdits(r); return events('TurnEnded', thread) >= 2 }, LONG)
  const v2 = JSON.parse(sql(`select data from events where type='ArtifactPublished' and stream_id='thread-${thread}' order by position desc limit 1`)[0][0])
  expect(v2.artifactId === artifact && v2.version === 2, `the same path again: artifact ${v2.artifactId}, version ${v2.version}`)
  await r.waitForSelector(`[data-testid=canvas][data-version="2"]`)
  await a.waitForSelector(`[data-testid=canvas][data-version="2"]`)
  const cards = r.locator('[data-testid=thread-view] [data-testid=artifact-card]')
  await until('every card to show v2', async () => (await cards.evaluateAll(els => els.every(e => e.dataset.version === '2'))) && await cards.count() >= 1, 15000)
  expect(true, `the open tabs (Rosa's and Alex's) and every card (${await cards.count()}) show v2`)
  const v2text = clean(await (await canvasFrame(r)).evaluate(() => document.body.innerText))
  expect(/50[,. ]?000|50k/i.test(v2text), 'v2 has the cost for 50,000 refunds a month')
  const options = await r.$$eval('[data-testid=canvas-version] option', os => os.map(o => o.textContent.trim()))
  expect(options.length === 2 && options[0].startsWith('v2') && options[1].startsWith('v1'), 'the version picker: ' + options.join(', '))
  await shots(r, '05-v2')
  await r.selectOption('[data-testid=canvas-version]', '1')
  await r.waitForSelector('[data-testid=canvas][data-version="1"]')
  const v1text = clean(await (await canvasFrame(r)).evaluate(() => document.body.innerText))
  expect(v1text !== v2text && new URL(r.url()).searchParams.get('v') === '1', 'v1 is still there, as it was (and the address says ?v=1)')
  await r.selectOption('[data-testid=canvas-version]', '2'); await r.waitForSelector('[data-testid=canvas][data-version="2"]')

  // --- 6. Alex's pinned reviewer shows a markdown document from its worktree ---
  await a.goto(`${BASE}/c/${channel()}`); await a.waitForSelector('[data-testid=channel-title]:has-text("checkout-app")')
  await a.fill('[data-testid=composer]', '@reviewer Read src/tax.ts and write a short review of it as a markdown document at reviews/tax/review.md (a level-1 heading, a bullet list and a small table), then call cowork_show with that path.')
  await a.keyboard.press('Enter')
  const alexThread = await threadOf('@reviewer Read src/tax.ts')
  await a.waitForSelector('[data-testid=thread-panel]'); await a.click('[data-testid=open-full-view]'); await a.waitForSelector('[data-testid=thread-view]')
  await until("reviewer's document", () => events('ArtifactPublished', alexThread) >= 1, LONG)
  const doc = JSON.parse(sql(`select data from events where type='ArtifactPublished' and stream_id='thread-${alexThread}'`)[0][0])
  const by = sql(`select actor_id, on_behalf_of from events where type='ArtifactPublished' and stream_id='thread-${alexThread}'`)[0]
  const where = sql(`select place, host from thread_agents where thread_id=${alexThread}`)[0]
  expect(doc.kind === 'markdown' && doc.path === 'reviews/tax/review.md' && by[0] === memberId('reviewer') && by[1] === memberId('alex') && where[0] === 'worktree' && where[1] === 'build-01',
    `reviewer (pinned) showed ${doc.path} (${doc.kind}) from its worktree on build-01, for Alex`)
  await a.waitForSelector(`[data-testid=canvas][data-artifact="${doc.artifactId}"] [data-testid=artifact-doc] h1`)
  ok('Alex started that thread and was looking: his canvas opened with the document rendered: ' + clean(await a.textContent('[data-testid=artifact-doc] h1')))
  await until("reviewer's turn to end", () => events('TurnEnded', alexThread) >= 1, LONG)
  await shots(a, '06-reviewer-markdown')

  // the audit has every version and who published it
  await until('the audit', () => sql("select count(*) from audit_entries where sentence like '%showed “%'")[0][0] >= 3, 20000)
  const audit = sql("select sentence from audit_entries where sentence like '%showed “%' order by id").map(x => x[0])
  expect(audit.some(x => x.startsWith('researcher (Alex\'s agent) showed “Tax APIs compared” v2') && x.endsWith('for Rosa.')), 'the audit: ' + audit.join(' | '))

  // --- the phone: the canvas fills the screen, with a back button ---
  const P = await person(browser, { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true })
  await P.ctx.addCookies((await R.ctx.cookies()).map(c => ({ ...c })))
  await P.page.goto(`${BASE}/c/${channel()}/threads/${thread}?a=${artifact}`)
  await P.page.waitForSelector('[data-testid=canvas] [data-testid=canvas-back]')
  const box = await P.page.locator('[data-testid=canvas]').boundingBox()
  expect(box.width >= 389 && !(await P.page.isVisible('[data-testid=thread-view]')), 'on a phone the canvas fills the screen, with a back button')
  await shots(P.page, '07-phone-canvas')
  await P.page.click('[data-testid=canvas-back]'); await P.page.waitForSelector('[data-testid=thread-view]')
  await P.page.locator('[data-testid=thread-view] .thin-scrollbar').evaluate(el => { el.scrollTop = el.scrollHeight })
  await shots(P.page, '08-phone-thread')
  ok('back to the thread on the phone')
  await P.ctx.close()
  // Rosa's thread from the top, the answer in markdown beside the canvas
  await r.locator('[data-testid=thread-view] [data-post] .body table').first().evaluate(el => el.scrollIntoView({ block: 'center' }))
  await shots(r, '10-rosa-markdown-answer')
  console.log('PHASE 13 LIVE CHECK PASSED')
} catch (e) {
  console.log('FAILED', e.message)
  for (const [p, n] of [[a, 'alex'], [r, 'rosa'], [s, 'sam']]) await shot(p, 'FAIL-' + n).catch(() => {})
  if (server?.log) console.log(server.log.split('\n').filter(l => /fail|error|exception|warn/i.test(l)).slice(-20).join('\n'))
  process.exitCode = 1
} finally {
  await stopGroup(runner); await stopGroup(pool); await stopGroup(server)
  await browser.close()
  for (const pass of [1, 2]) { try { cleanupPodman() } catch (e) { console.log('cleanup:', e.message) } if (pass === 1) await new Promise(r => setTimeout(r, 5000)) }
  SCRATCH.cleanup()
  if (poolKey) console.log(`removed every container and volume labelled cowork.pool=${poolKey}`)
}
