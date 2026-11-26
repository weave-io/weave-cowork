// Brief 10: panels you can size. Browsers for Alex (admin, Checkout app's lead) and Rosa; a REAL pool with REAL containers and REAL OpenCode
// 2.4.1 (the researcher template), model calls through the gateway to this machine's GitHub Copilot sign-in (the user agreed, 25 Nov 2026).
// The demo: Rosa asks researcher for a report without saying where to put it, and it shows it on the first try (cowork_show's description
// says the file must be in the workspace) → her canvas opens beside the thread; the handle on its left edge shows a line and a resize cursor on
// hover; she drags it narrower (past the canvas's minimum, the pointer over the report's iframe, which doesn't take the drag) and wider; the
// thread and the canvas resize live and the report reflows → limits: the canvas never below 320 px, the thread never below 360 → double-click
// puts the default back → the width survives a reload; Alex's browser keeps its own → a narrower window clamps it → the keyboard: a separator
// with aria-valuenow/min/max, arrows 16 px, Shift+arrows 64, Home and End → the thread's quick look and the member list have the same handle,
// each with its own remembered width → on a phone the canvas is full screen with no handle → asked to show a file from /tmp, researcher hears
// where to write it instead and shows it from its workspace.
// Starts its own server (default port 5888). Everything in podman carries the pool's label and is removed by it at the end.
//   node tests/live/phase14.mjs          (screenshots go to docs/screens/brief10/)
import { launch, person, expect, ok, SANDBOX_IMAGE } from './lib.mjs'
import { spawn, execFileSync } from 'node:child_process'
import { homedir } from 'node:os'
import { rmSync, mkdirSync, writeFileSync } from 'node:fs'

const HOME = homedir()
const PORT = process.env.PORT ?? '5888'
const BASE = `http://127.0.0.1:${PORT}`
const REPO = new URL('../../', import.meta.url).pathname.replace(/\/$/, '')
const DIR = `${HOME}/.cache/cowork-p14`
const DB = `${DIR}/data/cowork.db`
const SHOTS = process.env.SHOTS_DIR ?? `${REPO}/docs/screens/brief10/`
const SERVER = `${REPO}/src/CoWork.Server/bin/Debug/net10.0/CoWork.Server.dll`
const RUNNER = `${REPO}/src/CoWork.Runner/bin/Debug/net10.0/cowork-runner.dll`
const IMAGE = SANDBOX_IMAGE              // never the trial's own tag (see lib.mjs)
const LONG = 300000
rmSync(DIR, { recursive: true, force: true }); mkdirSync(`${DIR}/data`, { recursive: true }); mkdirSync(SHOTS, { recursive: true })
const sql = (q) => JSON.parse(execFileSync('python3', ['-I', '-c', 'import sqlite3,sys,json;c=sqlite3.connect(sys.argv[1]);print(json.dumps(c.execute(sys.argv[2]).fetchall()))', DB, q]).toString())
const until = async (what, probe, ms = 60000) => { const end = Date.now() + ms; for (;;) { const v = await probe(); if (v) return v; if (Date.now() > end) throw new Error('FAILED: timed out waiting for ' + what); await new Promise(r => setTimeout(r, 250)) } }
const clean = (t) => (t ?? '').replace(/\s+/g, ' ').trim()
const podman = (...a) => execFileSync('podman', a, { stdio: ['ignore', 'pipe', 'pipe'] }).toString().trim()
const git = (cwd, ...args) => execFileSync('git', args, { cwd, env: { ...process.env, GIT_AUTHOR_NAME: 'Alex', GIT_AUTHOR_EMAIL: 'alex@lumen.example', GIT_COMMITTER_NAME: 'Alex', GIT_COMMITTER_EMAIL: 'alex@lumen.example' } }).toString().trim()
const near = (x, want, slack = 2) => Math.abs(x - want) <= slack

if (!podman('images', '-q', IMAGE)) { console.log(`FAILED: the image ${IMAGE} isn't built here. Run TAG=${IMAGE} deploy/sandbox/build.sh first.`); process.exit(1) }
const seed = `${DIR}/seed`; mkdirSync(`${seed}/src`, { recursive: true })
git(seed, 'init', '-q', '-b', 'main')
writeFileSync(`${seed}/README.md`, '# checkout-app\n\nCart, payment and order confirmation for the Lumen Labs web shop.\n')
writeFileSync(`${seed}/src/tax.ts`, 'export function roundTax(lines: number[]): number {\n  // rounds each line, then sums\n  return lines.reduce((sum, l) => sum + Math.round(l * 100) / 100, 0)\n}\n')
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
const A = await person(browser), R = await person(browser, { viewport: { width: 1600, height: 900 } })
const a = A.page, r = R.page
const shot = async (p, name) => { await p.waitForTimeout(400); await p.screenshot({ path: SHOTS + name + '.png' }) }
const setTheme = (p, t) => p.evaluate(t => { localStorage.setItem('weave:theme', t); document.documentElement.dataset.theme = t; document.documentElement.style.colorScheme = t }, t)
const shots = async (p, name, keep) => {
  for (const theme of ['dark', 'light']) { await setTheme(p, theme); if (keep) await keep(); await shot(p, `${name}-${theme}`) }
  await setTheme(p, 'dark')
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
// every cowork_show call the harness made, by its tool id, in order (a call the harness itself refused, like one with the page's content in
// place of a path, never reaches CoWork, so the harness's own tool rows count too)
const harnessShows = (thread) => [...new Set(sql(`select data from events where type='ToolCallDetailed' and stream_id='thread-${thread}' order by position`).map(x => JSON.parse(x[0])).filter(d => /^cowork_show\b/.test(d.title ?? '')).map(d => d.toolId))]
const showCalls = (thread) => sql(`select data from events where type='CoworkToolCalled' and stream_id='thread-${thread}' order by position`).map(x => JSON.parse(x[0])).filter(d => d.tool === 'cowork_show')
// a template's asks go to whoever started the thread: Rosa allows its edits, never a host it wants to reach
async function allowEdits(p) {
  const btn = p.locator('[data-testid=thread-view] [data-testid=ask-card]:not(:has([data-testid=ask-egress])) button[data-answer]')
  const n = await btn.count()
  if (n) await btn.nth(n - 1).click().catch(() => {})
}
const canvasFrame = async (p) => { await p.waitForSelector('[data-testid=canvas] [data-testid=artifact-frame]'); const f = await (await p.$('[data-testid=canvas] [data-testid=artifact-frame]')).contentFrame(); await f.waitForLoadState('load'); return f }
const width = async (p, sel) => (await p.locator(sel).first().boundingBox())?.width ?? 0
const handleAt = async (p, which) => { const b = await p.locator(`[data-testid=resize-${which}]`).boundingBox(); return { x: b.x + b.width / 2, y: b.y + b.height / 2 } }
const aria = (p, which) => p.locator(`[data-testid=resize-${which}]`).evaluate(el => ({ now: Number(el.getAttribute('aria-valuenow')), min: Number(el.getAttribute('aria-valuemin')), max: Number(el.getAttribute('aria-valuemax')), role: el.getAttribute('role'), orientation: el.getAttribute('aria-orientation'), tab: el.tabIndex, label: el.getAttribute('aria-label') }))
const stored = (p, key) => p.evaluate(k => localStorage.getItem('cowork:panel:' + k), key)
async function drag(p, which, dx, { steps = 12, during } = {}) {
  const { x, y } = await handleAt(p, which)
  await p.mouse.move(x, y); await p.mouse.down()
  await p.mouse.move(x + dx, y, { steps })
  const seen = during ? await during(x + dx, y) : null
  await p.mouse.up()
  await p.waitForTimeout(100)
  return seen
}

try {
  await startServer()
  await a.goto(BASE)
  await a.fill('[name=serverName]', 'Lumen Labs'); await a.fill('[name=username]', 'alex'); await a.fill('[name=displayName]', 'Alex'); await a.fill('[name=password]', 'correct horse')
  await a.click('button[type=submit]'); await a.waitForSelector('[data-channel=general]')
  await join(r, 'rosa', 'Rosa')
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-where]'); await a.click('[data-testid=add-pool]')
  const command = clean(await a.textContent('[data-testid=pool-command]'))
  pool = spawnLogged([...command.split(' ').slice(1), '--host', 'build-02'], { ...process.env, COWORK_RUNNER_CONFIG: `${DIR}/pool/config`, COWORK_RUNNER_DATA: `${DIR}/pool/data` }, 'pool')
  await a.waitForSelector('[data-testid=pool-connected]', { timeout: 30000 }); await a.keyboard.press('Escape')
  poolKey = /labels cowork\.pool=(\S+);/.exec(pool.log)?.[1]
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-server-settings]')
  await a.click('[data-testid=provider-copilot]'); await a.click('[data-testid=save-models]'); await a.waitForSelector('[data-testid=models-saved]')
  await a.keyboard.press('Escape')
  // instructions that don't say where files go: the tool's own description has to
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-invite-agent]'); await a.click('[data-testid=runs-on-pool]'); await a.fill('[name=image]', IMAGE)
  await a.fill('[name=handle]', 'researcher'); await a.fill('[name=about]', 'Researches and writes reports')
  await a.fill('[name=instructions]', 'You are researcher, on the Lumen Labs checkout app. Be brief in chat. When someone asks for a report, make it an HTML page and show it to them with CoWork\'s cowork_show tool. Never look anything up on the internet; make up plausible figures when asked.')
  await a.click('[data-testid=create-agent]'); await a.waitForSelector('[data-testid=template-created]'); await a.keyboard.press('Escape')
  await a.click('[data-testid=new-project]')
  await a.fill('[name=projectName]', 'Checkout app'); await a.fill('[name=about]', 'Cart, payment and order confirmation')
  await a.click('[data-testid=start-repository]'); await a.fill('[name=repoUrl]', BARE); await a.fill('[name=branch]', 'main')
  await a.click('[data-testid=add-person]'); await a.click('[role=option]:has-text("Rosa")')
  await a.click('[data-testid=create-project]')
  await a.waitForSelector('[data-testid=project-settings-page] [data-testid=project-title]:has-text("Checkout app")')
  await a.click('[data-testid=add-agent]'); await a.waitForSelector('[data-testid=boundary] [data-line=stops]')
  if (await a.locator('[data-pick-agent=researcher]').count()) { await a.click('[data-pick-agent=researcher]'); await a.waitForTimeout(400) }
  await a.click('[data-testid=add-agent-confirm]'); await a.waitForSelector('[data-agent-row=researcher]')
  for (const p of [r, a]) { await p.goto(`${BASE}/c/${channel()}`); await p.waitForSelector('[data-testid=channel-title]:has-text("checkout-app")') }
  ok('set up: pool-01 and Copilot; researcher (a template, with instructions that don\'t say where files go) in Checkout app with Rosa')

  // --- 7. asked for a report without a path, researcher shows it on the first try ---
  await r.fill('[data-testid=composer]', '@researcher Make me a short report comparing three tax APIs (Tallyrate, Levy.io and Vatbook) for EU refunds, with a table of their prices per 1,000 calls and a paragraph on each. Make the figures up. Show it to me so I can read it beside this thread.')
  await r.keyboard.press('Enter')
  const thread = await threadOf('Make me a short report comparing')
  await r.waitForSelector('[data-testid=thread-panel]'); await r.click('[data-testid=open-full-view]')
  await r.waitForSelector('[data-testid=thread-view]')
  await until('researcher to show the report', async () => { await allowEdits(r); return events('ArtifactPublished', thread) >= 1 }, LONG)
  await until("researcher's turn to end", async () => { await allowEdits(r); return events('TurnEnded', thread) >= 1 }, LONG)
  const published = JSON.parse(sql(`select data from events where type='ArtifactPublished' and stream_id='thread-${thread}'`)[0][0])
  const first = showCalls(thread), tries = harnessShows(thread)
  console.log('  cowork_show calls:', JSON.stringify(first), 'harness rows:', tries.length)
  expect(tries.length === 1 && first.length === 1 && first[0].refused === false && !published.path.startsWith('/'),
    `researcher showed the report on the first try: ${published.path} (one cowork_show call, not refused)`)

  // --- 1. the canvas and its handle ---
  await r.waitForSelector(`[data-testid=canvas][data-artifact="${published.artifactId}"]`)
  const rf = await canvasFrame(r)
  const C = '[data-testid=canvas]', T = '[data-testid=thread-view]', WRAP = '[data-testid=thread-view-wrap]'
  const wrap = await width(r, WRAP)
  const byDefault = await width(r, C)
  expect(near(byDefault, Math.min(860, Math.max(420, wrap * 0.48)), 3) && await stored(r, 'canvas') === null,
    `Rosa's canvas opened beside the thread at its default width (${Math.round(byDefault)} px of ${Math.round(wrap)})`)
  await r.locator('[data-testid=resize-canvas]').hover(); await r.waitForTimeout(400)
  const hover = await r.locator('[data-testid=resize-canvas]').evaluate(el => ({ cursor: getComputedStyle(el).cursor, line: getComputedStyle(el.firstElementChild).opacity, w: el.firstElementChild.getBoundingClientRect().width }))
  expect(hover.cursor === 'col-resize' && hover.line === '1' && hover.w <= 3, `on hover the handle shows a thin line (${hover.w} px) and a resize cursor`)
  await shots(r, '01-canvas-handle-hover', async () => { await r.mouse.move(0, 0); await r.locator('[data-testid=resize-canvas]').hover() })
  await r.mouse.move(0, 0)

  // drag narrower, far past the minimum: the pointer ends up over the report's iframe, which doesn't take the drag
  const reportBefore = await rf.evaluate(() => document.documentElement.clientWidth)
  const start = await handleAt(r, 'canvas')
  await r.mouse.move(start.x, start.y); await r.mouse.down()
  await r.mouse.move(start.x + 120, start.y, { steps: 8 })
  const live = { canvas: await width(r, C), thread: await width(r, T), cover: await r.locator('[data-testid=resize-cover]').count(), report: await rf.evaluate(() => document.documentElement.clientWidth) }
  expect(near(live.canvas, byDefault - 120) && live.cover === 1 && live.report < reportBefore,
    `while she drags, the canvas (${Math.round(live.canvas)} px) and the thread (${Math.round(live.thread)} px) resize live and the report reflows (${reportBefore} → ${live.report} px); a cover keeps the iframe from the pointer`)
  const past = start.x + (byDefault - 320) + 150                  // 150 px past where the canvas stops: inside the report
  await r.mouse.move(past, start.y, { steps: 10 })
  const frameBox = await r.locator(`${C} [data-testid=artifact-frame]`).boundingBox()
  const overFrame = past > frameBox.x && past < frameBox.x + frameBox.width
  const atMin = await width(r, C)
  expect(overFrame && near(atMin, 320, 1), `past the limit the canvas stops at ${Math.round(atMin)} px (never below 320), with the pointer over the report's iframe`)
  await shots(r, '02-canvas-at-its-minimum')
  await r.mouse.move(start.x - 200, start.y, { steps: 10 })      // back across the iframe: the drag is still hers
  await r.mouse.up(); await r.waitForTimeout(100)
  const wider = await width(r, C)
  expect(near(wider, byDefault + 200) && Number(await stored(r, 'canvas')) === Math.round(wider) && await r.locator('[data-testid=resize-cover]').count() === 0,
    `dragged back across the iframe and wider: the canvas is ${Math.round(wider)} px, remembered when she lets go`)
  await shots(r, '03-canvas-wider')

  // --- 2. limits and the default ---
  await drag(r, 'canvas', -1200)
  const thin = await width(r, T), maxCanvas = await width(r, C)
  expect(thin >= 359.5 && near(thin, 360, 1) && near(maxCanvas, wrap - 360, 2), `dragged all the way: the thread keeps ${Math.round(thin)} px (never below 360), the canvas ${Math.round(maxCanvas)}`)
  await r.locator('[data-testid=resize-canvas]').dblclick(); await r.waitForTimeout(150)
  expect(near(await width(r, C), byDefault, 1) && await stored(r, 'canvas') === null, 'double-clicking the handle puts the default width back and forgets the chosen one')

  // --- 3. remembered per browser, kept on reload, clamped on a narrower window ---
  await drag(r, 'canvas', -150)
  const chosen = await width(r, C)
  await r.reload(); await r.waitForSelector(`${C} [data-testid=artifact-frame]`); await r.waitForTimeout(300)
  expect(near(await width(r, C), chosen, 1) && Number(await stored(r, 'canvas')) === Math.round(chosen), `after a reload the canvas is still ${Math.round(chosen)} px (localStorage cowork:panel:canvas)`)
  await a.goto(`${BASE}/c/${channel()}/threads/${thread}?a=${published.artifactId}`); await a.waitForSelector(`${C} [data-testid=artifact-frame]`); await a.waitForTimeout(300)
  const alexWrap = await width(a, WRAP)
  expect(near(await width(a, C), Math.min(860, Math.max(420, alexWrap * 0.48)), 3), `Alex's browser keeps its own: his canvas opens at the default (${Math.round(await width(a, C))} px)`)
  await r.setViewportSize({ width: 1100, height: 900 }); await r.reload(); await r.waitForSelector(`${C} [data-testid=artifact-frame]`); await r.waitForTimeout(300)
  const narrowWrap = await width(r, WRAP), narrow = await width(r, C)
  expect(narrow < chosen && near(narrow, narrowWrap - 360, 2) && near(await width(r, T), 360, 2),
    `opened on a narrower window the canvas is clamped to ${Math.round(narrow)} px, leaving the thread its 360`)
  await r.setViewportSize({ width: 1600, height: 900 }); await r.waitForTimeout(300)
  expect(near(await width(r, C), chosen, 1), 'back on the wide window it has the remembered width again')

  // --- 5. the keyboard ---
  const sep = r.locator('[data-testid=resize-canvas]')
  await sep.focus()
  const k0 = await aria(r, 'canvas')
  expect(k0.role === 'separator' && k0.orientation === 'vertical' && k0.tab === 0 && k0.label === 'Resize the canvas' && near(k0.now, chosen, 1) && k0.min === 320 && near(k0.max, (await width(r, WRAP)) - 360, 2),
    `the handle is a focusable separator: aria-valuenow ${k0.now}, aria-valuemin ${k0.min}, aria-valuemax ${k0.max}`)
  await r.keyboard.press('ArrowLeft')
  const k1 = await aria(r, 'canvas')
  await r.keyboard.press('Shift+ArrowLeft')
  const k2 = await aria(r, 'canvas')
  await r.keyboard.press('ArrowRight')
  const k3 = await aria(r, 'canvas')
  const focusCue = await sep.evaluate(el => ({ line: getComputedStyle(el.firstElementChild).width, opacity: getComputedStyle(el.firstElementChild).opacity, outline: getComputedStyle(el).outlineStyle }))
  expect(focusCue.line === '4px' && focusCue.opacity === '1' && focusCue.outline === 'none', `focused from the keyboard the handle shows its line thicker (${focusCue.line}), no ring around the grip`)
  expect(k1.now === k0.now + 16 && k2.now === k1.now + 64 && k3.now === k2.now - 16 && near(await width(r, C), k3.now, 1),
    `arrow keys move it 16 px and Shift+arrow 64 (${k0.now} → ${k1.now} → ${k2.now} → ${k3.now})`)
  await shots(r, '04-canvas-handle-focused', () => sep.focus().then(() => r.keyboard.press('Shift')))
  await r.keyboard.press('Home')
  const home = await aria(r, 'canvas')
  await r.keyboard.press('End')
  const end = await aria(r, 'canvas')
  expect(home.now === 320 && near(await width(r, T) + 0, 360, 2) && end.now === end.max && Number(await stored(r, 'canvas')) === end.max,
    `Home goes to the narrowest (${home.now}), End to the widest (${end.now}, the thread at 360), and it's remembered`)
  await r.locator('[data-testid=resize-canvas]').dblclick()

  // --- 4. the thread's quick look and the member list: the same handle, each its own width ---
  await r.goto(`${BASE}/c/${channel()}`); await r.waitForSelector('[data-testid=feed] [data-testid=thread-card]')
  await r.click('[data-testid=feed] [data-testid=thread-card]'); await r.waitForSelector('[data-testid=thread-panel]')
  const P0 = await width(r, '[data-testid=thread-panel]'), M0 = await width(r, '[data-testid=member-list]')
  expect(near(P0, 400, 1) && near(M0, 220, 1) && await r.locator('[role=separator]').count() === 2, `the quick look (${Math.round(P0)} px) and the member list (${Math.round(M0)} px) each have a handle`)
  await drag(r, 'thread-panel', -120)
  await drag(r, 'members', -60)
  const P1 = await width(r, '[data-testid=thread-panel]'), M1 = await width(r, '[data-testid=member-list]')
  expect(near(P1, 520, 1) && near(M1, 280, 1) && Number(await stored(r, 'thread-panel')) === Math.round(P1) && Number(await stored(r, 'members')) === Math.round(M1) && await stored(r, 'canvas') === null,
    `dragged: the quick look ${Math.round(P1)} px and the member list ${Math.round(M1)} px, each remembered under its own key (the canvas's untouched)`)
  await r.reload(); await r.waitForSelector('[data-testid=thread-panel]'); await r.waitForTimeout(300)
  const P2 = await width(r, '[data-testid=thread-panel]'), M2 = await width(r, '[data-testid=member-list]')
  expect(near(P2, P1, 1) && near(M2, M1, 1), 'after a reload both keep their widths')
  await drag(r, 'thread-panel', -2000)
  const feed = await width(r, 'main')
  expect(feed >= 359.5 && near(feed, 360, 1), `dragged all the way, the quick look stops with the channel at ${Math.round(feed)} px`)
  await r.locator('[data-testid=resize-thread-panel]').focus(); await r.keyboard.press('Home')
  expect(near(await width(r, '[data-testid=thread-panel]'), 320, 1), 'Home: the quick look at its narrowest (320)')
  await r.locator('[data-testid=resize-thread-panel]').dblclick(); await r.waitForTimeout(150)
  await r.evaluate(() => document.activeElement?.blur())
  await shots(r, '05-thread-panel-and-members')
  // the member list alone (no quick look) keeps its own width
  await r.goto(`${BASE}/c/${channel()}`); await r.waitForSelector('[data-testid=member-list]')
  expect(near(await width(r, '[data-testid=member-list]'), M1, 1) && await r.locator('[role=separator]').count() === 1, `the member list beside the channel alone is still ${Math.round(M1)} px, with its handle`)

  // --- 6. a phone: the canvas is full screen, no handle ---
  const Ph = await person(browser, { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true })
  await Ph.ctx.addCookies((await R.ctx.cookies()).map(c => ({ ...c })))
  await Ph.page.goto(`${BASE}/c/${channel()}/threads/${thread}?a=${published.artifactId}`)
  await Ph.page.waitForSelector('[data-testid=canvas] [data-testid=canvas-back]')
  const box = await Ph.page.locator('[data-testid=canvas]').boundingBox()
  expect(box.width >= 389 && await Ph.page.locator('[role=separator]').count() === 0, 'on a phone the canvas fills the screen and there is no handle')
  await shots(Ph.page, '06-phone-canvas')
  await Ph.page.goto(`${BASE}/c/${channel()}/t/${thread}`); await Ph.page.waitForSelector('[data-testid=thread-panel]')
  expect(await Ph.page.locator('[role=separator]').count() === 0 && (await Ph.page.locator('[data-testid=thread-panel]').boundingBox()).width >= 389, 'and the quick look is full width, no handle')
  await Ph.ctx.close()

  // --- 7, again: a file from /tmp gets an answer that says where to write it, and the next call works ---
  await r.goto(`${BASE}/c/${channel()}/threads/${thread}`); await r.waitForSelector('[data-testid=thread-view]')
  const before = showCalls(thread).length, versions = events('ArtifactPublished', thread)
  await r.fill('[data-testid=thread-view] [data-testid=composer]', '@researcher Write a one-paragraph summary as an HTML file at exactly /tmp/opencode/summary.html (that path, not your workspace) and call cowork_show with that path. If it can\'t be shown from there, do what the tool tells you.')
  await r.keyboard.press('Enter')
  await until('researcher to show the summary', async () => { await allowEdits(r); return events('ArtifactPublished', thread) > versions }, LONG)
  const calls = showCalls(thread).slice(before)
  const refusedTmp = calls.find(c => c.refused && JSON.stringify(c.detail ?? {}).includes('/tmp/'))
  const later = JSON.parse(sql(`select data from events where type='ArtifactPublished' and stream_id='thread-${thread}' order by position desc limit 1`)[0][0])
  const answerText = sql(`select data from events where stream_id='thread-${thread}' and type like 'ToolCall%' and data like '%Write the file inside your workspace%' limit 1`)[0]?.[0] ?? ''
  console.log('  cowork_show calls after the /tmp request:', JSON.stringify(calls))
  expect(!later.path.startsWith('/tmp') && (refusedTmp != null || calls.every(c => !c.refused)),
    `asked to show a file from /tmp, researcher ${refusedTmp ? 'heard where to write it and showed' : 'showed'} ${later.path} from its workspace${answerText ? ' (the answer: "Write the file inside your workspace (…), for example …/reports/…")' : ''}`)
  await until("researcher's turn to end", async () => { await allowEdits(r); return events('TurnEnded', thread) >= 2 }, LONG)
  console.log('PHASE 14 LIVE CHECK PASSED')
} catch (e) {
  console.log('FAILED', e.message)
  for (const [p, n] of [[a, 'alex'], [r, 'rosa']]) await shot(p, 'FAIL-' + n).catch(() => {})
  // what the harness did: each sandbox's OpenCode data (its database holds every tool call's input), copied out before the cleanup
  try {
    for (const id of poolKey ? podman('ps', '-aq', '--filter', `label=cowork.pool=${poolKey}`).split('\n').filter(Boolean) : []) {
      mkdirSync(`${DIR}/diag/${id}`, { recursive: true }); podman('cp', `${id}:/work/home/.local/share/opencode`, `${DIR}/diag/${id}/`)
      console.log(`  the harness's data from ${id} is in ${DIR}/diag/${id}`)
    }
  } catch (d) { console.log('  couldn\'t copy the harness data:', d.message) }
  if (server?.log) console.log(server.log.split('\n').filter(l => /fail|error|exception|warn/i.test(l)).slice(-20).join('\n'))
  process.exitCode = 1
} finally {
  await stopGroup(pool); await stopGroup(server)
  await browser.close()
  for (const pass of [1, 2]) { try { cleanupPodman() } catch (e) { console.log('cleanup:', e.message) } if (pass === 1) await new Promise(r => setTimeout(r, 5000)) }
  if (poolKey) console.log(`removed every container and volume labelled cowork.pool=${poolKey}`)
}
