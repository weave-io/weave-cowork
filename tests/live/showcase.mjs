// The showcase (brief 11): builds a scratch Weave CoWork from nothing, lets people and real agents work in it, and photographs it for the README
// and the website. Run it again whenever the app changes; it replaces the pictures in docs/screens/showcase/.
//
// What it makes: Lumen Labs, set up by Alex Morgan (admin); Rosa Delgado, Sam Okafor and Priya Nair join with invite links. Two projects:
// Checkout app (a small invented repository with the one-cent VAT bug; #checkout-app, #checkout-alerts and 🔊 checkout-standup) and Research
// (no repository; #research), and #general outside them. Two agents, both real OpenCode 2.4.1: researcher, a team template that runs in a
// sandbox per thread on a real pool, and reviewer, Alex's own agent on a real runner started with --host build-01. People talk in channels,
// agents do real work in threads (a report shown on the canvas and changed to v2, code explained with tool rows, a host asked for and allowed
// once, a thread resolved with an outcome, one left waiting, one photographed while it works), three people meet in a voice room.
//
// The pictures (desktop 1600×1000 at device scale 2, dark and light): 01-hero, 02-channel, 03-agent-at-work, 04-ask-rosa and 04-ask-alex,
// 05-add-agent, 06-project-settings, 07-search, 08-voice, 09-audit, 10-phone-thread (390×844 at scale 3), and social.png (1200×630). PNGs over
// 700 KB are reduced to a palette with sharp (libimagequant) when it can be loaded.
//
// Needs, on a Linux machine with rootless podman:
//   - the server and runner built: `cd web && bun install && bun run build`, then `dotnet build CoWork.slnx`
//   - the sandbox image: `TAG=cowork-sandbox-opencode:2.4.1 deploy/sandbox/build.sh` (or COWORK_SANDBOX_IMAGE=<your tag>)
//   - OpenCode 2.4.1 for the pinned agent: OPENCODE_BIN=/path/to/opencode (default: Weave's harness folder, then PATH)
//   - a model: GitHub Copilot signed in with OpenCode on this machine (the default), or SHOWCASE_MODELS_URL and SHOWCASE_MODELS_KEY for an
//     OpenAI-compatible provider (then OPENCODE_MODEL picks the pinned agent's model)
//   - Playwright with Chromium (PLAYWRIGHT_MODULE=/path/to/playwright/index.mjs if it isn't installed where node finds it)
//   - for voice, the LiveKit image docker.io/livekit/livekit-server:v1.13.11 (SHOWCASE_VOICE=0 skips the voice picture)
//   - optional: sharp, to keep each PNG small (SHARP_DIR=the folder where you ran `bun add sharp`)
// Then:
//   PLAYWRIGHT_HOST_PLATFORM_OVERRIDE=ubuntu24.04-x64 node tests/live/showcase.mjs
// It starts its own server (PORT, default 5886) and LiveKit (LIVEKIT_PORT, default 7980, with +1/tcp and +2/udp), keeps its data in
// ~/.cache/cowork-showcase, and removes every container and volume it made (by its labels) when it's done. Takes 15 to 25 minutes.
import { launch, person, expect, ok, freshHarnessHome, MODEL, SANDBOX_IMAGE } from './lib.mjs'
import { spawn, execFileSync } from 'node:child_process'
import { homedir, hostname, userInfo, tmpdir } from 'node:os'
import { rmSync, mkdirSync, writeFileSync, readFileSync, existsSync, readdirSync, statSync } from 'node:fs'
import { dirname, join as joinPath } from 'node:path'
import { createRequire } from 'node:module'

const HOME = homedir()
const PORT = process.env.PORT ?? '5886'
const BASE = `http://127.0.0.1:${PORT}`
const REPO = new URL('../../', import.meta.url).pathname.replace(/\/$/, '')
const DIR = `${HOME}/.cache/cowork-showcase`
const DB = `${DIR}/data/cowork.db`
const OUT = (process.env.SHOTS_DIR ?? `${REPO}/docs/screens/showcase/`).replace(/\/?$/, '/')
const SERVER = `${REPO}/src/CoWork.Server/bin/Debug/net10.0/CoWork.Server.dll`
const RUNNER = `${REPO}/src/CoWork.Runner/bin/Debug/net10.0/cowork-runner.dll`
const IMAGE = SANDBOX_IMAGE
const LONG = 300000
const VOICE = process.env.SHOWCASE_VOICE !== '0'
const LK_PORT = Number(process.env.LIVEKIT_PORT ?? 7980)
const LK_NAME = `cowork-showcase-livekit-${PORT}`
const OPENCODE = process.env.OPENCODE_BIN || [`${HOME}/.weave/harnesses/opencode2/.opencode/bin/opencode`].find(existsSync) || 'opencode'
const DESKTOP = { viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 2 }
const PEOPLE = { alex: 'Alex Morgan', rosa: 'Rosa Delgado', sam: 'Sam Okafor', priya: 'Priya Nair' }

rmSync(DIR, { recursive: true, force: true }); mkdirSync(`${DIR}/data`, { recursive: true }); mkdirSync(OUT, { recursive: true })
const SCRATCH = freshHarnessHome('showcase')       // the pinned runner's own home and OpenCode data, deleted at the end
writeFileSync(`${SCRATCH.dir}/config/opencode/opencode.json`, JSON.stringify({ $schema: 'https://opencode.ai/config.json', permission: { edit: 'ask', bash: 'deny', webfetch: 'deny' }, ...MODEL }))
const sql = (q) => JSON.parse(execFileSync('python3', ['-I', '-c', 'import sqlite3,sys,json;c=sqlite3.connect(sys.argv[1]);print(json.dumps(c.execute(sys.argv[2]).fetchall()))', DB, q]).toString())
const until = async (what, probe, ms = 60000) => { const end = Date.now() + ms; for (;;) { const v = await probe(); if (v) return v; if (Date.now() > end) throw new Error('FAILED: timed out waiting for ' + what); await new Promise(r => setTimeout(r, 250)) } }
const clean = (t) => (t ?? '').replace(/\s+/g, ' ').trim()
const podman = (...a) => execFileSync('podman', a, { stdio: ['ignore', 'pipe', 'pipe'] }).toString().trim()
const podmanTry = (...a) => { try { return podman(...a) } catch { return '' } }
const git = (cwd, ...args) => execFileSync('git', args, { cwd, env: { ...process.env, GIT_AUTHOR_NAME: 'Alex Morgan', GIT_AUTHOR_EMAIL: 'alex@lumen.example', GIT_COMMITTER_NAME: 'Alex Morgan', GIT_COMMITTER_EMAIL: 'alex@lumen.example' } }).toString().trim()

if (!podman('images', '-q', IMAGE)) { console.log(`FAILED: the image ${IMAGE} isn't built here. Run TAG=${IMAGE} deploy/sandbox/build.sh first.`); process.exit(1) }
const MODELS_URL = process.env.SHOWCASE_MODELS_URL, MODELS_KEY = process.env.SHOWCASE_MODELS_KEY
const copilot = (() => { try { return 'github-copilot' in JSON.parse(readFileSync(`${HOME}/.local/share/opencode/auth.json`, 'utf8')) } catch { return false } })()
if (!copilot && !MODELS_URL) { console.log('FAILED: no model. Sign in to GitHub Copilot with OpenCode, or set SHOWCASE_MODELS_URL and SHOWCASE_MODELS_KEY.'); process.exit(1) }

// --- the invented repository: a cart, VAT per line (rounded per line: the one-cent bug) and refunds (rounded once, on the total) ---
const seed = `${DIR}/seed`
for (const d of ['src', 'test', 'scripts']) mkdirSync(`${seed}/${d}`, { recursive: true })
git(seed, 'init', '-q', '-b', 'main')
const files = {
  'README.md': '# checkout-app\n\nCart, payment and refunds for the Lumen Labs web shop. Prices are in euro cents; VAT is per line.\n\n- `src/cart.ts`: lines, totals\n- `src/tax.ts`: VAT per line\n- `src/refunds.ts`: refunds of a whole order or some lines\n',
  'package.json': JSON.stringify({ name: 'checkout-app', private: true, type: 'module', scripts: { test: 'vitest run' }, devDependencies: { vitest: '^3.2.0' } }, null, 2) + '\n',
  'src/cart.ts': "import { roundTax } from './tax'\n\nexport type Line = { sku: string; qty: number; unitPrice: number; vatRate: number }  // unitPrice in euros\n\nexport function lineTotal(l: Line): number {\n  return l.qty * l.unitPrice\n}\n\n/** What the customer pays: each line's price plus its VAT, rounded per line. */\nexport function orderTotal(lines: Line[]): number {\n  return lines.reduce((sum, l) => sum + lineTotal(l) + roundTax(lineTotal(l), l.vatRate), 0)\n}\n",
  'src/tax.ts': '/** VAT for one order line, in euros, rounded to the cent. */\nexport function roundTax(amount: number, rate: number): number {\n  return Math.round(amount * rate * 100) / 100\n}\n',
  'src/refunds.ts': "import type { Line } from './cart'\nimport { lineTotal } from './cart'\n\n/** What we pay back: the lines' prices plus VAT on their sum, rounded once. */\nexport function refundTotal(lines: Line[]): number {\n  const net = lines.reduce((sum, l) => sum + lineTotal(l), 0)\n  const vat = lines.reduce((sum, l) => sum + lineTotal(l) * l.vatRate, 0)\n  return net + Math.round(vat * 100) / 100\n}\n",
  'test/refunds.test.ts': "import { expect, test } from 'vitest'\nimport { orderTotal } from '../src/cart'\nimport { refundTotal } from '../src/refunds'\n\ntest('a full refund pays back what was charged', () => {\n  const lines = [{ sku: 'MUG-01', qty: 1, unitPrice: 12.5, vatRate: 0.21 }]\n  expect(refundTotal(lines)).toBe(orderTotal(lines))\n})\n",
  'scripts/agent-setup.sh': '#!/bin/sh\n# What an agent needs before it works on the checkout app.\necho "checking the package registry…"\necho "registry.npmjs.org answered $(curl -s -o /dev/null -w "%{http_code}" -m 20 https://registry.npmjs.org/)"\necho "ready: node $(node --version 2>/dev/null || echo "not installed"), 3 source files"\n',
}
for (const [f, text] of Object.entries(files)) writeFileSync(`${seed}/${f}`, text)
git(seed, 'add', '.'); git(seed, 'commit', '-q', '-m', 'Cart, VAT per line and refunds')
const BARE = `${DIR}/git/checkout-app.git`
git(DIR, 'clone', '-q', '--bare', seed, BARE)

// --- processes ---
let server = null, pool = null, runner = null, poolKey = null
function startServer() {
  const voice = VOICE ? { LIVEKIT_API_KEY: 'devkey', LIVEKIT_API_SECRET: 'secret', LIVEKIT_URL: `ws://127.0.0.1:${LK_PORT}` } : {}
  server = spawn('dotnet', [SERVER], { cwd: `${REPO}/src/CoWork.Server`, env: { ...process.env, COWORK_DATA: `${DIR}/data`, COWORK_DB: DB, ASPNETCORE_URLS: BASE, ...voice }, stdio: ['ignore', 'pipe', 'pipe'], detached: true })
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
function startLiveKit() {
  podmanTry('rm', '-f', LK_NAME)
  const config = `port: ${LK_PORT}\nrtc:\n  tcp_port: ${LK_PORT + 1}\n  udp_port: ${LK_PORT + 2}\n  use_external_ip: false\n`
  podman('run', '-d', '--rm', '--name', LK_NAME, '--label', 'cowork.showcase=1', '--network', 'host', 'docker.io/livekit/livekit-server:v1.13.11', '--dev', '--bind', '127.0.0.1', '--config-body', config)
}
function cleanupPodman() {
  if (poolKey) {
    for (const id of podman('ps', '-aq', '--filter', `label=cowork.pool=${poolKey}`).split('\n').filter(Boolean)) podman('rm', '-f', id)
    for (const v of podman('volume', 'ls', '-q', '--filter', `label=cowork.pool=${poolKey}`).split('\n').filter(Boolean)) podman('volume', 'rm', v)
  }
  for (const id of podmanTry('ps', '-aq', '--filter', `name=^${LK_NAME}$`, '--filter', 'label=cowork.showcase=1').split('\n').filter(Boolean)) podman('rm', '-f', id)
}

// --- the browsers: four people, each with a fake microphone (a steady tone, so the speaking ring lights) ---
function toneWav(path, seconds = 4, hz = 220, rate = 48000) {
  const n = rate * seconds, buf = Buffer.alloc(44 + n * 2)
  buf.write('RIFF', 0); buf.writeUInt32LE(36 + n * 2, 4); buf.write('WAVEfmt ', 8); buf.writeUInt32LE(16, 16); buf.writeUInt16LE(1, 20); buf.writeUInt16LE(1, 22)
  buf.writeUInt32LE(rate, 24); buf.writeUInt32LE(rate * 2, 28); buf.writeUInt16LE(2, 32); buf.writeUInt16LE(16, 34); buf.write('data', 36); buf.writeUInt32LE(n * 2, 40)
  for (let i = 0; i < n; i++) buf.writeInt16LE(Math.round(Math.sin(2 * Math.PI * hz * i / rate) * 0.8 * 32767), 44 + i * 2)
  writeFileSync(path, buf)
}
const TONE = `${process.env.TMPDIR ?? tmpdir()}/cowork-showcase-tone-${process.pid}.wav`
toneWav(TONE)
const browser = await launch(['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream', `--use-file-for-fake-audio-capture=${TONE}`])
const A = await person(browser, DESKTOP), R = await person(browser, DESKTOP), S = await person(browser, DESKTOP), P = await person(browser, DESKTOP)
for (const X of [A, R, S, P]) await X.ctx.grantPermissions(['microphone'], { origin: BASE })
const a = A.page, r = R.page, s = S.page, pr = P.page

// --- pictures: this machine never shows in them. Paths and names from here are replaced on the page before each one, and a picture whose
//     page still says any of them is reported at the end (and the run fails) ---
const SWAP = [[BARE, '/srv/git/checkout-app.git'], [`${SCRATCH.dir}/own-workspace`, '/home/alex/work'], [SCRATCH.dir, '/home/alex'], [DIR, '/srv/cowork'], [HOME, '/home/alex'],
  [IMAGE, 'cowork-sandbox-opencode:2.4.1'], [`http://127.0.0.1:${PORT}`, 'https://cowork.lumen.example']]
const SECRET = [HOME, userInfo().username, hostname(), DIR, IMAGE === 'cowork-sandbox-opencode:2.4.1' ? null : IMAGE].filter(Boolean)
const leaks = []
async function scrub(p, name) {
  const found = await p.evaluate(([swap, secret]) => {
    const fix = (s) => swap.reduce((t, [from, to]) => t.split(from).join(to), s)
    const walk = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT)
    for (let n = walk.nextNode(); n; n = walk.nextNode()) { const t = fix(n.nodeValue); if (t !== n.nodeValue) n.nodeValue = t }
    for (const el of document.querySelectorAll('[title], input, textarea')) {
      if (el.title) el.title = fix(el.title)
      if ('value' in el && typeof el.value === 'string' && el.value) { const v = fix(el.value); if (v !== el.value) el.value = v }
    }
    const all = document.body.innerText + ' ' + [...document.querySelectorAll('input, textarea')].map(e => e.value).join(' ')
    return secret.filter(x => all.includes(x))
  }, [SWAP, SECRET])
  if (found.length) { leaks.push(`${name}: ${found.join(', ')}`); console.log(`  ! ${name} still shows ${found.join(', ')}`) }
}
const setTheme = (p, t) => p.evaluate(t => { localStorage.setItem('weave:theme', t); document.documentElement.dataset.theme = t; document.documentElement.style.colorScheme = t }, t)
async function shots(p, name, before) {
  for (const theme of ['dark', 'light']) {
    await setTheme(p, theme); if (before) await before()
    await p.waitForTimeout(450); await scrub(p, `${name}-${theme}`)
    await p.screenshot({ path: `${OUT}${name}-${theme}.png` })
  }
  await setTheme(p, 'dark')
  ok(`photographed ${name} (dark and light)`)
}

// --- helpers ---
async function join(p, handle) {
  const code = await a.evaluate(async () => (await (await fetch('/api/invites', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json()).code)
  await p.goto(`${BASE}/invite/${code}`)
  await p.fill('[name=username]', handle); await p.fill('[name=displayName]', PEOPLE[handle]); await p.fill('[name=password]', `${handle}-showcase-password`); await p.click('button[type=submit]')
  await p.waitForSelector('[data-channel=general]')
}
/** a post (or a thread reply) as the person signed in on p, through the same API the composer uses; the author comes from the cookie */
const say = (p, channelName, body, threadId = null) => p.evaluate(async ([ch, body, threadId]) => {
  const res = await fetch(`/api/channels/${ch}/posts`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ body, threadId }) })
  if (!res.ok) throw new Error(await res.text()); return (await res.json()).id
}, [chan(channelName), body, threadId])
const chan = (name) => sql(`select id from channels where name='${name}'`)[0][0]
const events = (type, thread) => sql(`select count(*) from events where type='${type}'${thread ? ` and stream_id='thread-${thread}'` : ''}`)[0][0]
const threadOf = (text) => until('the thread', () => sql(`select root_id from threads t join posts p on p.id=t.root_id where p.body like '%${text}%'`)[0]?.[0])
const openTurn = (thread) => sql(`select count(*) from open_turns where thread_id=${thread}`)[0][0] > 0
const toBottom = (p, where = 'thread-view') => p.evaluate((w) => { for (const el of document.querySelectorAll(`[data-testid=${w}] .thin-scrollbar`)) el.scrollTop = el.scrollHeight }, where)
const fullView = async (p, channelName, thread) => { await p.goto(`${BASE}/c/${chan(channelName)}/threads/${thread}`); await p.waitForSelector('[data-testid=thread-view]') }
const quickLook = async (p, channelName, thread) => { await p.goto(`${BASE}/c/${chan(channelName)}/t/${thread}`); await p.waitForSelector('[data-testid=thread-panel]') }
// a template's own asks (an edit, say) go to whoever started the thread: they allow them, never a host it wants to reach
async function allowEdits(p) {
  const btn = p.locator('[data-testid=thread-view] [data-testid=ask-card]:not(:has([data-testid=ask-egress])) button[data-answer]')
  const n = await btn.count()
  if (n) await btn.nth(n - 1).click().catch(() => {})
}

const RESEARCHER = 'You are researcher at Lumen Labs. You compare options and write reports. Be brief in chat: two or three sentences, or a short list. ' +
  'When someone asks for a report, write it as one self-contained HTML page in your workspace, under reports/<a-short-name>/index.html, with clean, readable styles ' +
  '(a light page, a clear table, no external fonts or scripts), and show it with CoWork\'s cowork_show tool. Don\'t look comparisons up on the internet: make up ' +
  'plausible figures and say in the report that they are illustrative. When someone asks you to fetch a page, use your webfetch tool; if it is refused, say so in one ' +
  'sentence and stop; when you are told a host is allowed, fetch it again.'
const REVIEWER = 'You are reviewer at Lumen Labs and you review the checkout app. Read the code with your tools before you answer, one file at a time. Be brief: a few ' +
  'sentences or a short list, with code in backticks and at most one short code block. Never resolve a thread on your own. Only when the latest ' +
  'message asks you to resolve it, call CoWork\'s cowork_resolve_thread tool with a short title and a two-sentence summary of what was found.'

try {
  if (VOICE) startLiveKit()
  await startServer()
  // --- Lumen Labs: Alex sets it up; Rosa, Sam and Priya join with invite links ---
  await a.goto(BASE)
  await a.fill('[name=serverName]', 'Lumen Labs'); await a.fill('[name=username]', 'alex'); await a.fill('[name=displayName]', PEOPLE.alex); await a.fill('[name=password]', 'alex-showcase-password')
  await a.click('button[type=submit]'); await a.waitForSelector('[data-channel=general]')
  await join(r, 'rosa'); await join(s, 'sam'); await join(pr, 'priya')
  ok('Lumen Labs: Alex Morgan set it up; Rosa Delgado, Sam Okafor and Priya Nair joined with invite links')

  // --- where agents run: a pool, the models, then researcher (a template) and reviewer (Alex's own, on build-01) ---
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-where]'); await a.click('[data-testid=add-pool]')
  const command = clean(await a.textContent('[data-testid=pool-command]'))
  const poolArgs = command.split(' ').slice(1)
  if (poolArgs.includes('--slots')) poolArgs[poolArgs.indexOf('--slots') + 1] = '4'; else poolArgs.push('--slots', '4')
  pool = spawnLogged([...poolArgs, '--host', 'build-02'], { ...process.env, COWORK_RUNNER_CONFIG: `${DIR}/pool/config`, COWORK_RUNNER_DATA: `${DIR}/pool/data` }, 'pool')
  await a.waitForSelector('[data-testid=pool-connected]', { timeout: 30000 }); await a.keyboard.press('Escape')
  poolKey = /labels cowork\.pool=(\S+);/.exec(pool.log)?.[1]
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-server-settings]')
  if (MODELS_URL) { await a.click('[data-testid=provider-openai]'); await a.fill('[name=baseUrl]', MODELS_URL); await a.fill('[name=apiKey]', MODELS_KEY ?? '') }
  else await a.click('[data-testid=provider-copilot]')
  await a.click('[data-testid=save-models]'); await a.waitForSelector('[data-testid=models-saved]')
  await a.fill('[name=idleSeconds]', '60'); await a.click('[data-testid=save-idle]'); await a.waitForSelector('[data-testid=idle-saved]'); await a.keyboard.press('Escape')
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-invite-agent]'); await a.click('[data-testid=runs-on-pool]'); await a.fill('[name=image]', IMAGE)
  await a.fill('[name=handle]', 'researcher'); await a.fill('[name=about]', 'Compares options and writes reports')
  await a.fill('[name=instructions]', RESEARCHER)
  await a.click('[data-testid=create-agent]'); await a.waitForSelector('[data-testid=template-created]'); await a.keyboard.press('Escape')
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-invite-agent]')
  await a.fill('[name=handle]', 'reviewer'); await a.fill('[name=about]', 'Reads the checkout app and explains it')
  await a.fill('[name=instructions]', REVIEWER)
  await a.fill('[name=harness]', 'opencode acp'); await a.fill('[name=workspace]', `${SCRATCH.dir}/own-workspace`)
  await a.click('[data-testid=create-agent]'); await a.waitForSelector('[data-testid=runner-command]')
  const parts = clean(await a.textContent('[data-testid=runner-command]')).split(' ')
  runner = spawnLogged(['connect', BASE, '--code', parts[parts.length - 1], '--host', 'build-01'], { ...SCRATCH.env, PATH: `${dirname(OPENCODE)}:${process.env.PATH}` }, 'runner')
  await a.waitForSelector('[data-testid=agent-connected]', { timeout: 30000 }); await a.click('button:has-text("Skip")')
  ok('pool-01 on build-02 (4 slots), models through the gateway, researcher (a team template) and reviewer (Alex\'s, on build-01)')

  // --- projects: Checkout app from the repository, with its network list, setup and budget; Research with nothing yet ---
  await a.click('[data-testid=new-project]')
  await a.fill('[name=projectName]', 'Checkout app'); await a.fill('[name=about]', 'Cart, payment and refunds for the web shop')
  await a.click('[data-testid=start-repository]'); await a.fill('[name=repoUrl]', BARE); await a.fill('[name=branch]', 'main')
  if (!(await a.isChecked('[data-pick-channel=checkout-alerts]'))) await a.check('[data-pick-channel=checkout-alerts]')
  for (const who of ['Rosa', 'Sam', 'Priya']) { await a.click('[data-testid=add-person]'); await a.click(`[role=option]:has-text("${who}")`) }
  await a.click('[data-testid=create-project]')
  await a.waitForSelector('[data-testid=project-settings-page] [data-testid=project-title]:has-text("Checkout app")')
  const checkout = Number(a.url().split('/').pop())
  for (const h of ['github.com', 'registry.npmjs.org']) { await a.fill('[name=networkHost]', h); await a.click('[data-testid=add-host]'); await a.waitForSelector(`[data-testid=network-list] [data-host="${h}"]`) }
  await a.fill('[name=setupCommand]', 'sh scripts/agent-setup.sh'); await a.click('[data-testid=save-setup]'); await a.waitForSelector('[data-testid=setup-saved]')
  await a.fill('[name=monthTokens]', '5000000'); await a.fill('[name=threadTokens]', '400000'); await a.click('[data-testid=save-budget]'); await a.waitForSelector('[data-testid=budget-saved]')
  // 5. adding an agent: the boundary, in sentences
  await a.click('[data-testid=add-agent]'); await a.waitForSelector('[data-testid=boundary] [data-line=stops]')
  await a.click('[data-pick-agent=reviewer]'); await a.waitForTimeout(400)
  await a.click('[data-testid=add-agent-confirm]'); await a.waitForSelector('[data-agent-row=reviewer]')
  await a.click('[data-testid=add-agent]'); await a.waitForSelector('[data-testid=boundary] [data-line=stops]')
  if (await a.locator('[data-pick-agent=researcher]').count()) { await a.click('[data-pick-agent=researcher]'); await a.waitForTimeout(400) }
  await shots(a, '05-add-agent')
  await a.click('[data-testid=add-agent-confirm]'); await a.waitForSelector('[data-agent-row=researcher]')
  await a.click('[data-testid=new-project]')
  await a.fill('[name=projectName]', 'Research'); await a.fill('[name=about]', 'Reading, comparisons and reports')
  if (await a.locator('[data-testid=start-none]').count()) await a.click('[data-testid=start-none]')
  if (await a.isChecked('[data-pick-channel=research-standup]')) await a.uncheck('[data-pick-channel=research-standup]')
  for (const who of ['Rosa', 'Sam', 'Priya']) { await a.click('[data-testid=add-person]'); await a.click(`[role=option]:has-text("${who}")`) }
  await a.click('[data-testid=create-project]')
  await a.waitForSelector('[data-testid=project-settings-page] [data-testid=project-title]:has-text("Research")')
  await a.click('[data-testid=add-agent]'); await a.waitForSelector('[data-testid=boundary] [data-line=stops]')
  if (await a.locator('[data-pick-agent=researcher]').count()) { await a.click('[data-pick-agent=researcher]'); await a.waitForTimeout(400) }
  await a.click('[data-testid=add-agent-confirm]'); await a.waitForSelector('[data-agent-row=researcher]')
  ok('Checkout app (repository, #checkout-app, #checkout-alerts, 🔊 checkout-standup; reviewer and researcher) and Research (no repository, #research; researcher)')

  // --- people talk ---
  await say(a, 'general', 'Welcome to Lumen Labs\' CoWork 👋 Checkout work lives in **Checkout app**, reading and comparisons in **Research**. Mention an agent to start a thread with it.')
  await say(pr, 'general', 'Morning all. Standup is in 🔊 checkout-standup at 9:30, I\'ll bring the refund numbers.')
  await say(s, 'general', 'Thanks Alex. Finance shared the November refund export, I\'ll put the odd ones in #checkout-alerts.')
  await say(s, 'checkout-alerts', 'Refund R-2207 paid back €36.29, the order was charged €36.30. Three lines, 21% VAT.')
  await say(s, 'checkout-alerts', 'Refund R-2231 paid back €58.11 for €58.12. Four lines, 21% VAT. 14 like this in November so far.')
  await say(pr, 'checkout-alerts', 'All of them are refunds of the whole order? None of the single-line ones?')
  await say(s, 'checkout-alerts', 'Yes, whole orders with three or more lines.')
  await say(s, 'checkout-app', 'Finance found 14 refunds in November that came back a cent short. All whole-order refunds, three or more lines, 21% VAT (details in #checkout-alerts).')
  await say(r, 'checkout-app', 'That smells like rounding: the cart rounds VAT per line and refunds round once on the total?')
  await say(pr, 'research', 'We need a tax API for EU refunds before Q1. Shortlist so far: Tallyrate, Levy.io and Vatbook.')
  await say(s, 'research', 'Levy.io was the one with batch endpoints, I think. Pricing pages are all over the place though.')
  ok('people talk in #general, #checkout-alerts, #checkout-app and #research')

  // --- two threads at once: researcher's report on the canvas (Rosa, #research) and reviewer explaining roundTax (Alex, #checkout-app) ---
  await say(r, 'research', '@researcher Compare Tallyrate, Levy.io and Vatbook for EU refunds. Make me a short report: a table with the price per 1,000 calls, EU countries covered, refunds API, uptime, and a paragraph on each. Show it to me so I can read it beside this thread.')
  await say(a, 'checkout-app', '@reviewer Explain `roundTax` in src/tax.ts and how src/cart.ts and src/refunds.ts use it. Could a refund come out a cent different from what we charged?')
  const report = await threadOf('Compare Tallyrate, Levy.io and Vatbook')
  const explain = await threadOf('Explain `roundTax`')
  await fullView(r, 'research', report)
  const reportFlow = (async () => {
    await until('researcher to show the report', async () => { await allowEdits(r); return events('ArtifactPublished', report) >= 1 }, LONG)
    await until("researcher's turn to end", async () => { await allowEdits(r); return events('TurnEnded', report) >= 1 }, LONG)
    await r.waitForSelector('[data-testid=canvas] [data-testid=artifact-frame]', { timeout: 30000 })
    await say(pr, 'research', 'Nice. Whichever we pick has to do partial refunds, finance asked twice.', report)
    await until("researcher's answer to Priya", async () => { await allowEdits(r); return events('TurnEnded', report) >= 2 }, LONG)
    await r.fill('[data-testid=canvas-ask]', 'Add a short recommendation at the top, and a row for what 50,000 refunds a month would cost with each.')
    await r.click('[data-testid=canvas-send]')
    await until('researcher to show v2', async () => { await allowEdits(r); return events('ArtifactPublished', report) >= 2 }, LONG)
    await until("researcher's turn to end", async () => { await allowEdits(r); return events('TurnEnded', report) >= 3 }, LONG)
  })()
  const explainFlow = (async () => {
    await until("reviewer's explanation", () => events('TurnEnded', explain) >= 1, LONG)
    await say(s, 'checkout-app', 'That matches the export: every short refund had three or more lines. @reviewer can you resolve this thread with what we found? Rosa will pick up the fix.', explain)
    // done means done at the end: a person's message reopens a thread, so a resolve before Sam asked doesn't count
    const resolved = () => sql(`select resolved_post from threads where root_id=${explain}`)[0][0] != null
    await until("reviewer's turn to end", () => events('TurnEnded', explain) >= 2, LONG)
    if (!resolved()) {
      await say(a, 'checkout-app', '@reviewer Please resolve this thread now, with a short title and summary.', explain)
      await until("reviewer's turn to end", () => events('TurnEnded', explain) >= 3, LONG)
    }
    await until('the thread to be resolved', resolved, 30000)
  })()
  await Promise.all([reportFlow, explainFlow])
  expect(events('ArtifactPublished', report) >= 2, 'researcher showed its report on the canvas and made v2 after Rosa asked')
  expect(sql(`select resolved_post from threads where root_id=${explain}`)[0][0] != null && events('ToolCallStarted', explain) >= 2, 'reviewer explained roundTax with tool rows and resolved the thread with an outcome')

  // --- an ask: Rosa's sandbox wants a host that isn't on the list; Priya's is left waiting ---
  await say(r, 'checkout-app', '@researcher Can you reach the web from your sandbox? Fetch https://example.com and tell me the page title.')
  const fetchT = await threadOf('Can you reach the web from your sandbox')
  await say(pr, 'checkout-app', '@researcher Fetch https://status.tallyrate.example and tell me whether Tallyrate had outages in November.')
  const statusT = await threadOf('status.tallyrate.example')
  await quickLook(r, 'checkout-app', fetchT)
  await r.waitForSelector('[data-testid=thread-panel] [data-testid=ask-card] [data-testid=ask-egress]', { timeout: LONG })
  await until('the refused turn to end', () => !openTurn(fetchT), LONG)
  await quickLook(a, 'checkout-app', fetchT)
  await a.waitForSelector('[data-testid=thread-panel] [data-testid=ask-waiting]')
  await toBottom(r, 'thread-panel'); await shots(r, '04-ask-rosa')
  await toBottom(a, 'thread-panel'); await shots(a, '04-ask-alex')
  await r.click('[data-testid=thread-panel] [data-answer=once]')
  await until("researcher's second try", () => events('TurnEnded', fetchT) >= 2, LONG)
  await until("Priya's ask", () => sql(`select count(*) from posts where thread_id=${statusT} and kind='ask'`)[0][0] >= 1, LONG)
  ok('the proxy refused example.com, Rosa allowed it once and researcher read it; Priya\'s ask for status.tallyrate.example is left waiting')

  // --- an agent at work (Sam, full view) and the channel with threads working, waiting and done (Alex) ---
  await a.goto(`${BASE}/c/${chan('checkout-app')}`); await a.waitForSelector('[data-testid=feed]')
  await say(s, 'checkout-app', '@researcher Read README.md, src/cart.ts, src/tax.ts, src/refunds.ts and test/refunds.test.ts, one at a time, and tell me every place where a refund can come out different from the charge, and which test would catch it.')
  const work = await threadOf('every place where a refund can come out')
  await fullView(s, 'checkout-app', work)
  const tools = () => sql(`select count(*) from events where type='ToolCallStarted' and stream_id='thread-${work}' and coalesce(json_extract(data,'$.toolId'),'')<>'cowork-setup'`)[0][0]
  const said = () => sql(`select count(*) from posts where thread_id=${work} and kind='text' and author_id=(select id from members where handle='researcher')`)[0][0]
  // the channel first, as soon as its thread is working (the dot and the pill say so), then the thread itself once it has read a little
  await until('researcher to be working', async () => { await allowEdits(s); return (openTurn(work) && tools() >= 1) || events('TurnEnded', work) >= 1 }, LONG)
  if (!openTurn(work)) console.log('  ! the turn ended before the picture: 02 shows it idle')
  await shots(a, '02-channel', () => a.evaluate(() => { const f = document.querySelector('[data-testid=feed]'); if (f) f.scrollTop = f.scrollHeight }))
  await until('researcher to be at work (two reads and some text)', async () => { await allowEdits(s); return (tools() >= 2 && said() >= 1) || events('TurnEnded', work) >= 1 }, LONG)
  if (!openTurn(work)) console.log('  ! the turn ended before the picture: 03 shows it idle')
  await toBottom(s); await shots(s, '03-agent-at-work')
  await until("Sam's turn to end", async () => { await allowEdits(s); return events('TurnEnded', work) >= 1 }, LONG)

  // --- 8. voice: Alex, Rosa and Sam in 🔊 checkout-standup ---
  if (VOICE) {
    for (const p of [a, r, s]) { await p.goto(`${BASE}/c/${chan('checkout-standup')}`); await p.click('[data-testid=voice-join]'); await p.waitForSelector('[data-testid=voice-tile]') }
    await r.waitForFunction(() => document.querySelectorAll('[data-testid=voice-tile]').length === 3, null, { timeout: 30000 })
    for (const p of [r, s]) await p.click('[data-testid=voice-mute]')
    await r.waitForSelector('[data-testid=voice-tile][data-member=sam][data-muted=true]')
    await r.waitForSelector('[data-testid=voice-tile][data-member=alex][data-speaking=true]', { timeout: 30000 })
    // a muted microphone stops the ring once LiveKit's next speaker update arrives
    await r.waitForFunction(() => ['rosa', 'sam'].every(h => document.querySelector(`[data-testid=voice-tile][data-member=${h}]`)?.dataset.speaking === 'false'), null, { timeout: 15000 })
      .catch(() => console.log('  ! a muted tile still shows its speaking ring'))
    await shots(r, '08-voice', () => r.waitForSelector('[data-testid=voice-tile][data-member=alex][data-speaking=true]', { timeout: 15000 }).catch(() => {}))
    for (const p of [a, r, s]) await p.click('[data-testid=voice-leave]')
  }

  // --- 6. project settings, 7. search, 9. the audit ---
  await a.goto(`${BASE}/p/${checkout}`); await a.waitForSelector('[data-testid=project-budget] [data-testid=budget-used]')
  // the page is taller than the window: scroll it so the workspace stays at the top and the budget's caps show at the bottom
  const settingsScroll = async () => a.evaluate(() => {
    const ws = document.querySelector('[data-testid=workspaces]'), budget = document.querySelector('[data-testid=project-budget]')
    let el = ws.parentElement; while (el && !(el.scrollHeight > el.clientHeight + 4 && /auto|scroll/.test(getComputedStyle(el).overflowY))) el = el.parentElement
    if (!el) return
    const box = el.getBoundingClientRect(), top = ws.getBoundingClientRect().top - box.top - 12, need = budget.getBoundingClientRect().bottom - box.bottom + 12
    el.scrollTop += Math.max(0, Math.min(top, need))
  })
  await shots(a, '06-project-settings', settingsScroll)
  await r.goto(`${BASE}/search`); await r.fill('[data-testid=search-input]', 'refund')
  await r.waitForSelector('[data-testid=search-outcomes] [data-testid=search-hit]', { timeout: 15000 })
  await shots(r, '07-search')
  await a.goto(`${BASE}/c/${chan('general')}`); await a.waitForSelector('[data-testid=feed]')
  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-audit]'); await a.waitForSelector('[data-testid=audit-entry]')
  await shots(a, '09-audit')
  await a.keyboard.press('Escape')

  // --- 10. a thread on a phone (Rosa) ---
  const Ph = await person(browser, { viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true })
  await Ph.ctx.addCookies(await R.ctx.cookies())
  await fullView(Ph.page, 'checkout-app', explain)
  await Ph.page.waitForSelector('[data-testid=thread-view] [data-post]')
  await shots(Ph.page, '10-phone-thread', () => toBottom(Ph.page))
  await Ph.ctx.close()

  // --- 1. the hero: Rosa's report thread in the full view, v2 on the canvas, threads under their channels ---
  await fullView(r, 'research', report)
  await r.goto(`${BASE}/c/${chan('research')}/threads/${report}?a=${JSON.parse(sql(`select data from events where type='ArtifactPublished' and stream_id='thread-${report}' order by position desc limit 1`)[0][0]).artifactId}`)
  await r.waitForSelector('[data-testid=canvas] [data-testid=artifact-frame]')
  await shots(r, '01-hero', async () => { await toBottom(r); await r.waitForTimeout(600) })

  // --- the social card (1200×630), from the hero ---
  const hero = readFileSync(`${OUT}01-hero-dark.png`).toString('base64')
  const logo = readFileSync(`${REPO}/web/src/assets/weave_logo.png`).toString('base64')
  const card = await browser.newContext({ viewport: { width: 1200, height: 630 }, deviceScaleFactor: 1 })
  const cp = await card.newPage()
  await cp.setContent(`<!doctype html><html><body style="margin:0;width:1200px;height:630px;overflow:hidden;background:#0d0d10;color:#e8e8ec;font-family:Inter,'Segoe UI',system-ui,sans-serif;position:relative">
    <div style="position:absolute;inset:0;background:radial-gradient(900px 500px at 85% 0%,rgba(99,101,241,.28),transparent 70%)"></div>
    <img src="data:image/png;base64,${hero}" style="position:absolute;left:560px;top:96px;width:900px;border-radius:14px;box-shadow:0 0 0 1px rgba(255,255,255,.08),0 30px 80px -20px rgba(0,0,0,.9)">
    <div style="position:absolute;left:64px;top:84px;width:450px">
      <img src="data:image/png;base64,${logo}" style="height:40px">
      <h1 style="font-size:58px;line-height:1.05;margin:36px 0 18px;font-weight:700;letter-spacing:-.02em">Weave CoWork</h1>
      <p style="font-size:25px;line-height:1.35;margin:0;color:#b9b9c4">A self-hosted place where people and AI agents work together.</p>
      <p style="margin:34px 0 0;display:inline-block;font-size:17px;font-weight:600;color:#f5b343;border:1.5px solid rgba(245,179,67,.55);background:rgba(245,158,11,.12);border-radius:999px;padding:6px 16px">Experimental</p>
    </div></body></html>`)
  await cp.waitForTimeout(300)
  await cp.screenshot({ path: `${OUT}social.png` })
  await card.close()
  ok('the social card: social.png')
  console.log('SHOWCASE DONE')
} catch (e) {
  console.log('FAILED', e.message)
  for (const [p, n] of [[a, 'alex'], [r, 'rosa'], [s, 'sam'], [pr, 'priya']]) await p.screenshot({ path: `${DIR}/FAIL-${n}.png` }).catch(() => {})
  console.log(`  the browsers' last pages are in ${DIR}/FAIL-*.png`)
  if (server?.log) console.log(server.log.split('\n').filter(l => /fail|error|exception|warn/i.test(l)).slice(-20).join('\n'))
  process.exitCode = 1
} finally {
  await stopGroup(runner); await stopGroup(pool); await stopGroup(server)
  await browser.close()
  for (const pass of [1, 2]) { try { cleanupPodman() } catch (e) { console.log('cleanup:', e.message) } if (pass === 1) await new Promise(r => setTimeout(r, 5000)) }
  if (poolKey) console.log(`removed every container and volume labelled cowork.pool=${poolKey}, and the LiveKit container`)
  SCRATCH.cleanup(); rmSync(TONE, { force: true })
}

// --- keep each picture small: a palette PNG (libimagequant through sharp) for any over 700 KB ---
if (!process.exitCode) {
  let sharp = null
  for (const from of [process.env.SHARP_DIR, REPO].filter(Boolean)) { try { sharp = createRequire(joinPath(from, 'noop.js'))('sharp'); break } catch { /* next */ } }
  if (!sharp) console.log('  sharp isn\'t installed: the PNGs are as Chromium wrote them (set SHARP_DIR to make the large ones smaller)')
  for (const f of readdirSync(OUT).filter(f => f.endsWith('.png'))) {
    const path = OUT + f, before = statSync(path).size
    if (!sharp || before <= 700 * 1024) continue
    for (const quality of [90, 80, 70]) {
      const out = await sharp(readFileSync(path)).png({ palette: true, quality, effort: 10, compressionLevel: 9 }).toBuffer()
      if (out.length <= 700 * 1024 || quality === 70) { writeFileSync(path, out); console.log(`  ${f}: ${Math.round(before / 1024)} KB → ${Math.round(out.length / 1024)} KB (palette, quality ${quality})`); break }
    }
  }
  if (leaks.length) { console.log('FAILED: these pictures still show something from this machine:\n  ' + leaks.join('\n  ')); process.exitCode = 1 }
  else console.log(`SHOWCASE PASSED: ${readdirSync(OUT).filter(f => f.endsWith('.png')).length} pictures in ${OUT.replace(REPO + '/', '')}`)
}
