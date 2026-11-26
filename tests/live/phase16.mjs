// Brief 13: pages that look chosen. Browsers for Alex (admin) and Rosa (her computer in LIGHT, CoWork in its default dark); a REAL pool with
// REAL containers and REAL OpenCode 2.4.1 (the researcher template, on an image whose runner installs CoWork's skills), model calls through the
// gateway to this machine's GitHub Copilot sign-in (the user agreed, 25 Nov 2026). researcher's instructions say nothing about design.
// The demo: the sandbox's home has cowork-design where Claude Code, Codex and OpenCode look → Rosa asks for a report (brief 10's words) →
// researcher reads the design guide (the skill, or cowork_design_guide) before it shows the page → the page has its own dark and light, links
// CoWork's font kit and loads nothing from the internet; its fonts load in the canvas's sandboxed frame → with her computer light and CoWork
// dark, the page is dark → she switches CoWork to light and the same page (no reload) turns light, and back → the card's thumbnail is told too →
// opened on its own the page follows the computer → the app's copy of the page is as researcher wrote it, without CoWork's script.
// Starts its own server (default port 5887). Everything in podman carries the pool's label and is removed by it at the end.
//   node tests/live/phase16.mjs          (screenshots go to docs/screens/brief13/)
import { launch, person, expect, ok } from './lib.mjs'
import { spawn, execFileSync } from 'node:child_process'
import { homedir } from 'node:os'
import { rmSync, mkdirSync, writeFileSync } from 'node:fs'

const HOME = homedir()
const PORT = process.env.PORT ?? '5887'
const BASE = `http://127.0.0.1:${PORT}`
const REPO = new URL('../../', import.meta.url).pathname.replace(/\/$/, '')
const DIR = `${HOME}/.cache/cowork-p16`
const DB = `${DIR}/data/cowork.db`
const SHOTS = process.env.SHOTS_DIR ?? `${REPO}/docs/screens/brief13/`
const SERVER = `${REPO}/src/CoWork.Server/bin/Debug/net10.0/CoWork.Server.dll`
const RUNNER = `${REPO}/src/CoWork.Runner/bin/Debug/net10.0/cowork-runner.dll`
const IMAGE = process.env.COWORK_SANDBOX_IMAGE || 'cowork-sandbox-opencode:2.4.1-b13'   // brief 13's runner installs the skill; never the trial's own tag
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
const A = await person(browser), R = await person(browser, { viewport: { width: 1600, height: 900 }, colorScheme: 'light' })
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
// CoWork's theme through its own settings (the canvas's scheme comes from the app's state, so localStorage alone won't do)
async function useTheme(p, id) {
  await p.click('[data-testid=rail-settings]'); await p.click(`[data-theme-card="${id}"]`); await p.keyboard.press('Escape')
  await p.waitForFunction(t => document.documentElement.dataset.theme === t, id)
  await p.mouse.move(640, 450); await p.evaluate(() => document.activeElement?.blur())   // the rail's tooltip stays out of the pictures
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

  // --- the skill in the sandbox's home ---
  await r.fill('[data-testid=composer]', '@researcher Make me a short report comparing three tax APIs (Tallyrate, Levy.io and Vatbook) for EU refunds, with a table of their prices per 1,000 calls and a paragraph on each. Make the figures up. Show it to me so I can read it beside this thread.')
  await r.keyboard.press('Enter')
  const thread = await threadOf('Make me a short report comparing')
  await r.waitForSelector('[data-testid=thread-panel]'); await r.click('[data-testid=open-full-view]')
  await r.waitForSelector('[data-testid=thread-view]')
  const box = await until('the sandbox container', () => poolKey && podman('ps', '-q', '--filter', `label=cowork.pool=${poolKey}`).split('\n').filter(Boolean)[0], LONG)
  const installed = await until('the runner to install the skill', () => {
    try { return podman('exec', box, 'sh', '-c', 'head -2 /work/home/.claude/skills/cowork-design/SKILL.md; head -2 /work/home/.agents/skills/cowork-design/SKILL.md') } catch { return null }
  }, 60000)
  expect(installed.split('\n').filter(l => l === 'name: cowork-design').length === 2,
    "the sandbox's runner installed cowork-design in the harness's home: ~/.claude/skills/cowork-design/SKILL.md and ~/.agents/skills/cowork-design/SKILL.md")

  // --- researcher reads the guide before it shows the page ---
  await until('researcher to show the report', async () => { await allowEdits(r); return events('ArtifactPublished', thread) >= 1 }, LONG)
  await until("researcher's turn to end", async () => { await allowEdits(r); return events('TurnEnded', thread) >= 1 }, LONG)
  const published = JSON.parse(sql(`select data from events where type='ArtifactPublished' and stream_id='thread-${thread}'`)[0][0])
  const rows = sql(`select data, position from events where type in ('ToolCallDetailed','CoworkToolCalled','ArtifactPublished') and stream_id='thread-${thread}' order by position`)
  const titles = sql(`select data from events where type='ToolCallDetailed' and stream_id='thread-${thread}' order by position`).map(x => JSON.parse(x[0]).title ?? '')
  console.log('  harness tool rows:', JSON.stringify([...new Set(titles)].slice(0, 30)))
  // OpenCode's own skill tool shows as a row titled "skill"; which skill it loaded is in OpenCode's database (session_part's input)
  const peek = `${DIR}/peek`; rmSync(peek, { recursive: true, force: true }); mkdirSync(peek, { recursive: true })
  podman('cp', `${box}:/work/home/.local/share/opencode/opencode.db`, `${peek}/`)
  for (const f of ['opencode.db-wal', 'opencode.db-shm']) { try { podman('cp', `${box}:/work/home/.local/share/opencode/${f}`, `${peek}/`) } catch { /* not there */ } }
  const loaded = JSON.parse(execFileSync('python3', ['-I', '-c', "import sqlite3,sys,json;c=sqlite3.connect(sys.argv[1]);print(json.dumps([json.loads(d)['state']['input'].get('id') for (d,) in c.execute('select data from session_part') if json.loads(d).get('state',{}).get('input',{}).get('id')]))", `${peek}/opencode.db`]).toString())
  console.log('  skills OpenCode loaded:', JSON.stringify(loaded))
  const guideAt = rows.findIndex(([d]) => d.includes('cowork_design_guide') || (JSON.parse(d).title === 'skill' && loaded.includes('cowork-design')))
  if (guideAt >= 0) console.log('  the guide, as the harness read it:', rows[guideAt][0].slice(0, 300))
  const shownAt = rows.findIndex(([d]) => JSON.parse(d).artifactId != null)
  const how = guideAt < 0 ? 'neither' : rows[guideAt][0].includes('cowork_design_guide') ? 'cowork_design_guide' : 'the cowork-design skill'
  expect(guideAt >= 0 && guideAt < shownAt, `researcher read the design guide (${how}) before it showed ${published.path}`)

  // --- the page as researcher wrote it ---
  const v = published.version ?? 1
  const source = await r.evaluate(async ([id, v, entry]) => (await fetch(`/api/artifacts/${id}/versions/${v}/raw/${entry}`)).text(), [published.artifactId, v, published.entry ?? 'index.html'])
  const external = [...source.matchAll(/(?:src|href)\s*=\s*["']?(https?:\/\/[^"'\s>]+)|url\(\s*["']?(https?:\/\/[^"')]+)/gi)].map(m => m[1] ?? m[2])
  const links = [...source.matchAll(/<(?:link|script|img)\b[^>]*(?:src|href)\s*=\s*["']?(https?:\/\/[^"'\s>]+)/gi)].map(m => m[1])
  expect(/prefers-color-scheme\s*:\s*dark/i.test(source) && /\/a\/fonts\/fonts\.css/.test(source) && links.length === 0 && !source.includes('data-cowork="scheme"'),
    `the page as researcher wrote it (the app's copy, without CoWork's script) has its own dark theme, links CoWork's font kit and loads nothing from the internet${external.length - links.length > 0 ? ` (links people can follow: ${external.length - links.length})` : ''}`)

  // the guide's recipe for a report, as researcher followed it (reported, not required: the words and the look are the model's)
  const css = (source.match(/<style[^>]*>([\s\S]*?)<\/style>/gi) ?? []).join('\n')
  const recipe = {
    answerFirst: (() => { const body = source.slice(source.search(/<body/i)); const t = body.search(/<table/i); return t < 0 || body.slice(0, t).replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').length > 120 })(),
    chart: /width:\s*\d+(\.\d+)?%/.test(source.slice(source.search(/<body/i))),
    quietHeader: !/(^|[\s,}])(th|thead)[^{]*\{[^}]*background/i.test(css),
    noStripes: !/border-(left|inline-start)\s*:\s*\d/i.test(css),
  }
  console.log('  the recipe:', JSON.stringify(recipe))
  console.log('  bars:', JSON.stringify([...source.slice(source.search(/<body/i)).matchAll(/width:\s*(\d+(?:\.\d+)?)%/g)].map(m => +m[1])), 'figures:', JSON.stringify([...source.replace(/<[^>]+>/g, ' ').matchAll(/[€$£]\s?\d+(?:[.,]\d+)?/g)].map(m => m[0]).slice(0, 12)))
  // --- light and dark follow CoWork, not the computer ---
  await r.waitForSelector(`[data-testid=canvas][data-artifact="${published.artifactId}"]`)
  const rf = await canvasFrame(r)
  const look = () => rf.evaluate(() => {
    const parse = (c) => (c.match(/[\d.]+/g) ?? []).map(Number)
    const lum = (c) => { const [r, g, b, a = 1] = parse(c); return a === 0 ? null : (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255 }
    const body = lum(getComputedStyle(document.body).backgroundColor), root = lum(getComputedStyle(document.documentElement).backgroundColor)
    return { ground: body ?? root ?? 1, hash: location.hash, marker: window.__p16 ?? null, script: !!document.querySelector('script[data-cowork=scheme]'),
      fonts: [...document.fonts].filter(f => f.status === 'loaded').map(f => f.family.replace(/"/g, '')) }
  })
  await rf.evaluate(() => { window.__p16 = 'same page' })
  await r.waitForTimeout(800)
  const dark = await look()
  const osLight = await r.evaluate(() => matchMedia('(prefers-color-scheme: light)').matches)
  expect(osLight && dark.hash === '#scheme=dark' && dark.script && dark.ground < 0.35,
    `Rosa's computer is light and CoWork dark: the canvas tells the page ${dark.hash} and the page is dark (its ground's luminance ${dark.ground.toFixed(2)})`)
  const kit = ['Fraunces', 'Instrument Serif', 'Source Serif 4', 'IBM Plex Sans', 'Bricolage Grotesque', 'IBM Plex Mono']
  const used = [...new Set(dark.fonts)].filter(f => kit.includes(f))
  expect(used.length >= 1, `the page's fonts came from CoWork's kit and loaded in the sandboxed frame: ${used.join(', ')}`)
  // bars as drawn: each element sized with an inline width in %, its drawn width over its parent's (the guide asks for value ÷ largest)
  const drawn = await rf.evaluate(() => [...document.querySelectorAll('body [style*="width"]')].filter(el => /width:\s*[\d.]+%/.test(el.getAttribute('style'))).map(el => ({ asked: parseFloat(/width:\s*([\d.]+)%/.exec(el.getAttribute('style'))[1]), drawn: Math.round(100 * el.getBoundingClientRect().width / el.parentElement.getBoundingClientRect().width) })))
  console.log('  bars as drawn (asked → drawn %):', JSON.stringify(drawn))
  await shot(r, '01-report-in-dark')
  await useTheme(r, 'light')
  await until('the page to turn light', async () => (await look()).ground > 0.6, 10000)
  const light = await look()
  expect(light.marker === 'same page' && light.hash === '#scheme=light',
    `she switches CoWork to light: the same page, without a reload, turns light (${dark.ground.toFixed(2)} → ${light.ground.toFixed(2)})`)
  await shot(r, '02-report-in-light')
  const thumb = await r.locator('[data-testid=artifact-thumb]').first().getAttribute('src').catch(() => null)
  expect(!thumb || thumb.endsWith('#scheme=light'), `the card's thumbnail is told too (${thumb ? thumb.replace(/^.*#/, '#') : 'no thumbnail on screen'})`)
  await useTheme(r, 'dark')
  await until('the page to turn dark again', async () => (await look()).ground < 0.35, 10000)
  ok('and back to dark: the page follows again')

  // --- on its own, the page follows the computer ---
  const url = await r.evaluate(async (id) => (await (await fetch(`/api/artifacts/${id}`)).json()).versions.at(-1).url, published.artifactId)
  const own = await R.ctx.newPage()
  await own.goto(BASE + url); await own.waitForLoadState('load'); await own.waitForTimeout(400)
  const groundOf = (p) => p.evaluate(() => { const m = (getComputedStyle(document.body).backgroundColor.match(/[\d.]+/g) ?? []).map(Number); return m[3] === 0 ? 1 : (0.2126 * m[0] + 0.7152 * m[1] + 0.0722 * m[2]) / 255 })
  const alone = await groundOf(own)
  await own.emulateMedia({ colorScheme: 'dark' }); await own.waitForTimeout(300)
  const aloneDark = await groundOf(own)
  expect(alone > 0.6 && aloneDark < 0.35, `opened on its own (no #scheme), the page follows the computer: light (${alone.toFixed(2)}), and dark when the computer is (${aloneDark.toFixed(2)})`)
  await own.close()
  await useTheme(r, 'light'); await r.waitForTimeout(500); await shot(r, '03-full-view-light')
  await useTheme(r, 'dark'); await r.waitForTimeout(500); await shot(r, '03-full-view-dark')
  console.log('PHASE 16 LIVE CHECK PASSED')
} catch (e) {
  console.log('FAILED', e.message)
  for (const [p, n] of [[a, 'alex'], [r, 'rosa']]) await shot(p, 'FAIL-' + n).catch(() => {})
  if (server?.log) console.log(server.log.split('\n').filter(l => /fail|error|exception|warn/i.test(l)).slice(-20).join('\n'))
  process.exitCode = 1
} finally {
  // what the harness did: each sandbox's OpenCode data (its database holds every tool call's input), copied out before the cleanup
  try {
    for (const id of poolKey ? podman('ps', '-aq', '--filter', `label=cowork.pool=${poolKey}`).split('\n').filter(Boolean) : []) {
      mkdirSync(`${DIR}/diag/${id}`, { recursive: true }); podman('cp', `${id}:/work/home/.local/share/opencode`, `${DIR}/diag/${id}/`)
      console.log(`  the harness's data from ${id} is in ${DIR}/diag/${id}`)
    }
  } catch (d) { console.log('  couldn\'t copy the harness data:', d.message) }
  await stopGroup(pool); await stopGroup(server)
  await browser.close()
  for (const pass of [1, 2]) { try { cleanupPodman() } catch (e) { console.log('cleanup:', e.message) } if (pass === 1) await new Promise(r => setTimeout(r, 5000)) }
  if (poolKey) console.log(`removed every container and volume labelled cowork.pool=${poolKey}`)
}
