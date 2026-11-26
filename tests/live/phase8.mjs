// Brief 6: search. Two browsers (Alex, the admin, and Rosa), a private channel Rosa isn't in, and a REAL runner with a REAL harness (OpenCode 2
// under a fresh scratch HOME). Walks the demo: Rosa opens Search (the entry and Ctrl+K), types "refund rounding", gets Threads then Messages as she
// types, with the words marked and "rounds" found by "rounding"; the filters (typed and from the chips) narrow it; a result opens the channel at
// the post, or the thread with the reply in view; nothing from the private channel, nothing deleted or redacted; reviewer's real answer is found
// by a word in it. Starts its own server (default port 5886).
//   node tests/live/phase8.mjs          (screenshots go to docs/screens/brief6/)
import { launch, person, expect, ok, freshHarnessHome, setUp, ssoJoin, SSO_ENV } from './lib.mjs'
import { spawn, execFileSync } from 'node:child_process'
import { homedir } from 'node:os'
import { rmSync, mkdirSync } from 'node:fs'

const HOME = homedir()
const PORT = process.env.PORT ?? '5886'
const BASE = `http://127.0.0.1:${PORT}`
const REPO = new URL('../../', import.meta.url).pathname.replace(/\/$/, '')
const DIR = `${HOME}/.cache/cowork-p8`
const DB = `${DIR}/data/cowork.db`
const SCRATCH = freshHarnessHome('p8')
const SHOTS = `${REPO}/docs/screens/brief6/`
const HOST = 'build-01'
const SERVER = `${REPO}/src/CoWork.Server/bin/Debug/net10.0/CoWork.Server.dll`
const RUNNER = `${REPO}/src/CoWork.Runner/bin/Debug/net10.0/cowork-runner.dll`
const HARNESS = process.env.HARNESS ?? `${HOME}/.weave/harnesses/opencode2/.opencode/bin/opencode acp`
const LONG = 150000
rmSync(DIR, { recursive: true, force: true }); mkdirSync(`${DIR}/data`, { recursive: true }); mkdirSync(SHOTS, { recursive: true })
const sql = (q) => JSON.parse(execFileSync('python3', ['-I', '-c', 'import sqlite3,sys,json;c=sqlite3.connect(sys.argv[1]);print(json.dumps(c.execute(sys.argv[2]).fetchall()))', DB, q]).toString())
const until = async (what, probe, ms = 40000) => { const end = Date.now() + ms; for (;;) { const v = await probe(); if (v) return v; if (Date.now() > end) throw new Error('FAILED: timed out waiting for ' + what); await new Promise(r => setTimeout(r, 100)) } }
const clean = (t) => t.replace(/\s+/g, ' ').trim()

let server = null, runner = null
function startServer() {
  server = spawn('dotnet', [SERVER], { cwd: `${REPO}/src/CoWork.Server`, env: { ...process.env, ...SSO_ENV, COWORK_DB: DB, ASPNETCORE_URLS: BASE }, stdio: ['ignore', 'pipe', 'pipe'], detached: true })
  server.log = ''; server.stdout.on('data', d => server.log += d); server.stderr.on('data', d => server.log += d)
  return until('the server to listen', async () => { try { return (await fetch(BASE + '/api/setup/status')).ok } catch { return false } }, 30000)
}
const stopGroup = (p) => new Promise(res => { if (!p || p.exitCode !== null) return res(); p.once('exit', res); try { process.kill(-p.pid, 'SIGTERM') } catch { return res() } setTimeout(res, 8000) })
function startRunner(args) {
  runner = spawn('dotnet', [RUNNER, ...args], { env: SCRATCH.env, stdio: ['ignore', 'pipe', 'pipe'], detached: true })
  runner.stdout.on('data', d => process.stdout.write('  [runner] ' + d)); runner.stderr.on('data', d => process.stdout.write('  [runner!] ' + d))
}

const browser = await launch()
const A = await person(browser), R = await person(browser)
const a = A.page, r = R.page
const shot = async (p, name) => { await p.waitForTimeout(400); await p.screenshot({ path: SHOTS + name + '.png' }) }
const api = (p, method, path, body) => p.evaluate(async ([m, u, b]) => { const x = await fetch('/api' + u, { method: m, headers: b ? { 'Content-Type': 'application/json' } : {}, body: b ? JSON.stringify(b) : undefined }); return { status: x.status, body: await x.json().catch(() => null) } }, [method, path, body])
const say = async (p, text) => { await p.fill('[data-testid=composer]', text); await p.keyboard.press('Enter') }
async function theme(p, id) { await p.click('[data-testid=user-menu]'); await p.click('[data-testid=theme-menu]'); await p.click(`[data-theme-option=${id}]`); await p.keyboard.press('Escape'); await p.keyboard.press('Escape'); await p.waitForSelector('[role=menu]', { state: 'detached' }) }

// what the search view shows: settle on the count for exactly this text in the box, then read the hits
async function results(p) {
  await p.waitForSelector('[data-testid=search-count], [data-testid=search-none], [data-testid=search-empty]')
  await p.waitForFunction(() => !document.querySelector('[data-testid=search-view] [data-slot=spinner]'))
  return p.evaluate(() => [...document.querySelectorAll('[data-testid=search-hit]')].map(h => ({
    kind: h.dataset.kind, text: h.textContent.replace(/\s+/g, ' ').trim(), marks: [...h.querySelectorAll('mark')].map(m => m.textContent), post: h.dataset.postId, root: h.dataset.root,
  })))
}
async function search(p, q) {
  await p.fill('[data-testid=search-input]', q)
  await p.waitForFunction((q) => new URL(location.href).searchParams.get('q') === q || (!q && !location.search), q)
  return results(p)
}

try {
  await startServer()

  // --- Lumen Labs: Alex, Rosa, #checkout-app, and #leads (private, Rosa isn't in it) ---
  await a.goto(BASE)
  await setUp(a)
  await a.waitForSelector('[data-channel=general]')
  await a.click('[data-testid=create-channel]'); await a.fill('[name=name]', 'checkout-app'); await a.fill('[name=topic]', 'Cart, payment and refunds'); await a.click('button:has-text("Create channel")')
  await a.waitForSelector('[data-testid=channel-title]:has-text("checkout-app")')
  const invite = (await api(a, 'POST', '/invites', {})).body.code
  await r.goto(`${BASE}/invite/${invite}`)
  await ssoJoin(r, 'rosa', 'Rosa')
  await r.waitForSelector('[data-channel=checkout-app]')
  const leads = (await api(a, 'POST', '/channels', { name: 'leads', isPrivate: true })).body.id
  expect((await api(a, 'POST', `/channels/${leads}/posts`, { body: 'Refund rounding budget for Q1: keep this between us for now.' })).status === 200, 'Alex wrote in #leads, a private channel Rosa is not in')

  await a.click('[data-testid=server-menu]'); await a.click('[data-testid=menu-invite-agent]')
  await a.fill('[name=handle]', 'reviewer'); await a.fill('[name=about]', 'Reviews the checkout app')
  await a.fill('[name=instructions]', 'You are reviewer, a code reviewer for the Lumen Labs checkout app. Answer from what you know, without using any tools. Answer in at most three short sentences. Never change any file.')
  await a.fill('[name=harness]', HARNESS); await a.fill('[name=workspace]', REPO)
  await a.click('button:has-text("Create agent")')
  await a.waitForSelector('[data-testid=runner-command]')
  const parts = (await a.textContent('[data-testid=runner-command]')).trim().split(' ')
  startRunner(['connect', BASE, '--code', parts[parts.length - 1], '--host', HOST])
  await a.waitForSelector('[data-testid=agent-connected]', { timeout: 30000 })
  await a.check('[data-pick=checkout-app]'); await a.click('[data-testid=join-channels]'); await a.keyboard.press('Escape')
  await a.waitForSelector('[data-agent=reviewer]:has-text("online")')
  ok(`Alex and Rosa are in; reviewer's real runner is online (${HOST})`)

  // --- the conversation: refunds and tax rounding ---
  await r.click('[data-channel=checkout-app]'); await r.waitForSelector('[data-testid=channel-title]:has-text("checkout-app")')
  await say(r, "Finance wants partial refunds by January. Our tax step rounds per line, which is why we're a cent out.")
  await say(a, '@reviewer When a refund is split across three order lines and tax rounding happens on each line, why can the total end up a cent off? Would half-even rounding fix it?')
  await a.waitForSelector('[data-testid=thread-panel]')
  const root = sql("select id from posts where body like '@reviewer When a refund%'")[0][0]
  const reviewerId = sql("select id from members where handle='reviewer'")[0][0]
  await until('reviewer to answer', () => sql(`select count(*) from events where type='TurnEnded'`)[0][0] >= 1, LONG)
  const answer = sql(`select id, body from posts where author_id=${reviewerId} and kind='text' and deleted=0 order by id`)
  expect(answer.length >= 1 && answer.every(x => x[1].length > 20), `reviewer (real OpenCode) answered in the thread: "${clean(answer[0][1]).slice(0, 100)}…"`)
  await r.goto(`${BASE}/c/${sql("select id from channels where name='checkout-app'")[0][0]}/t/${root}`)
  await r.waitForSelector('[data-testid=thread-panel]')
  await r.fill('[data-testid=thread-panel] [data-testid=composer]', 'Do we round half-up or half-even on refunds today?'); await r.keyboard.press('Enter')
  await until('Rosa\'s reply', () => sql("select count(*) from posts where body like 'Do we round half-up%'")[0][0] === 1)
  // a message she takes back (redacted) and one she deletes: no button yet, the API is the way (brief 4)
  const redacted = (await api(r, 'POST', `/channels/${sql("select id from channels where name='checkout-app'")[0][0]}/posts`, { body: 'Refund rounding draft figure: 4417 cents, do not quote.' })).body.id
  const deleted = (await api(r, 'POST', `/channels/${sql("select id from channels where name='checkout-app'")[0][0]}/posts`, { body: 'Scratchpad: refund rounding ideas, ignore.' })).body.id
  expect((await api(r, 'POST', `/posts/${redacted}/redact`)).status === 200 && (await api(r, 'DELETE', `/posts/${deleted}`)).status === 200, 'Rosa redacted one message and deleted another')
  await r.click('[aria-label="Close thread"]')

  // --- 1. Rosa opens Search: the entry under Mentions, and Ctrl+K from anywhere ---
  const navY = async (sel) => (await r.locator(sel).boundingBox()).y
  expect(await navY('[data-testid=search-nav]') > await navY('[data-testid=mentions]') && await navY('[data-testid=search-nav]') < await navY('[data-channel=general]'), 'the Search entry sits under Mentions, above the channels')
  await r.click('[data-testid=search-nav]')
  await r.waitForSelector('[data-testid=search-view] [data-testid=search-empty]')
  expect(new URL(r.url()).pathname === '/search' && await r.evaluate(() => document.activeElement?.dataset.testid) === 'search-input', 'Search opens in the main area with the box focused, and says what it can do')
  await shot(r, '1-search-empty-dark')
  await r.click('[data-channel=general]'); await r.waitForSelector('[data-testid=channel-title]:has-text("general")')
  await r.keyboard.press('Control+k')
  await r.waitForSelector('[data-testid=search-input]')
  expect(await r.evaluate(() => document.activeElement?.dataset.testid) === 'search-input', 'Ctrl+K opens Search from a channel, focused')

  // --- 2. results as she types: Threads first, then Messages, words marked, stems ---
  await r.locator('[data-testid=search-input]').pressSequentially('refund rounding', { delay: 60 })
  let hits = await results(r)
  await r.waitForFunction(() => new URL(location.href).searchParams.get('q') === 'refund rounding')
  hits = await results(r)
  const kinds = hits.map(h => h.kind)
  expect(kinds[0] === 'thread' && kinds.lastIndexOf('thread') < kinds.indexOf('message'), `results arrive as she types, grouped: ${kinds.filter(k => k === 'thread').length} thread(s) first, then ${kinds.filter(k => k === 'message').length} message(s)`)
  const thread = hits.find(h => h.kind === 'thread' && Number(h.root) === root)
  expect(thread && /2 replies|[2-9] replies/.test(thread.text) && thread.text.includes('@reviewer') && thread.text.includes('checkout-app'), `the thread shows its opening post, its reply count and the agent in it: "${thread?.text.slice(0, 120)}…"`)
  const finance = hits.find(h => h.text.includes('Finance wants partial refunds'))
  expect(finance && finance.marks.some(m => /^rounds$/i.test(m)) && finance.marks.some(m => /^refunds$/i.test(m)), `"rounding" finds "rounds" and "refund" finds "refunds", marked: ${JSON.stringify(finance?.marks)}`)
  expect(hits.some(h => h.text.includes('half-up or half-even') && h.text.includes('thread')), 'thread replies are messages too, with the thread they are in')
  expect(await r.locator('[data-testid=search-threads] h3').textContent().then(t => t.startsWith('Threads')) && await r.locator('[data-testid=search-messages] h3').textContent().then(t => t.startsWith('Messages')), 'the groups are headed Threads and Messages with their counts')
  await shot(r, '2-results-dark')

  // --- 5. only what she may see: not #leads, nothing redacted or deleted ---
  expect(!hits.some(h => h.text.includes('between us')), 'the post in #leads (private, Rosa is not in it) is not among her results')
  expect((await search(r, 'budget')).length === 0 && await r.locator('[data-testid=search-none]').count() === 1, 'searching a word only #leads has finds nothing for Rosa ("No results for …")')
  await shot(r, '7-no-results-dark')
  expect((await search(r, '4417')).length === 0 && (await search(r, 'draft figure')).length === 0 && (await search(r, 'redacted')).length === 0, 'the redacted message is found by nothing it used to say')
  expect((await search(r, 'scratchpad')).length === 0, 'the deleted message is not found')
  await a.click('[data-testid=search-nav]')
  expect((await search(a, 'budget')).some(h => h.text.includes('between us') && h.text.includes('leads')), 'Alex, who is in #leads, finds it')

  // --- 6. reviewer's real answer is found by a word in it ---
  const words = answer.map(x => x[1]).join(' ').match(/[A-Za-z]{7,}/g) ?? []
  const used = new Set(sql(`select body from posts where author_id<>${reviewerId} and kind='text'`).map(x => x[0]).join(' ').toLowerCase().match(/[a-z]+/g))
  const word = words.find(w => !used.has(w.toLowerCase())) ?? words[0]
  hits = await search(r, word)
  expect(hits.some(h => h.kind === 'message' && h.text.includes('reviewer') && h.marks.length > 0), `reviewer's answer is found by "${word}", a word only it used`)
  const byReviewer = await search(r, 'from:@reviewer')
  expect(byReviewer.length >= 1 && byReviewer.every(h => h.text.includes('reviewer')), `from:@reviewer alone lists reviewer's answers (${byReviewer.length})`)

  // --- 3. filters typed into the box, and the chips that insert them ---
  await search(r, 'refund')
  await r.click('[data-chip="in:#checkout-app"]')
  await r.waitForFunction(() => new URL(location.href).searchParams.get('q')?.includes('in:#checkout-app'))
  hits = await results(r)
  expect(await r.inputValue('[data-testid=search-input]') === 'refund in:#checkout-app ' && await r.locator('[data-chip="in:#checkout-app"][aria-pressed=true]').count() === 1 && hits.length > 0 && hits.every(h => h.text.includes('checkout-app')), `the in:#checkout-app chip inserts the filter, shows it on, and every result is in #checkout-app (${hits.length})`)
  await shot(r, '3-filter-chip-dark')
  await r.click('[data-chip="in:#checkout-app"]')
  await r.waitForFunction(() => !new URL(location.href).searchParams.get('q')?.includes('in:'))
  expect(await r.inputValue('[data-testid=search-input]') === 'refund', 'clicking the chip again takes the filter out')
  hits = await search(r, 'refund from:@rosa')
  expect(hits.length >= 1 && hits.every(h => h.kind === 'message' ? h.text.includes('Rosa') : true) && hits.some(h => h.text.includes('Finance')), `from:@rosa: only Rosa's messages (${hits.length})`)
  await r.click('[data-chip="is:thread"]')
  await r.waitForFunction(() => new URL(location.href).searchParams.get('q')?.includes('is:thread'))
  hits = await results(r)
  expect(hits.length >= 1 && hits.every(h => h.kind === 'thread') && await r.locator('[data-testid=search-messages]').count() === 0, 'the is:thread chip narrows it to threads')
  hits = await search(r, 'refund in:#general')
  expect(hits.length === 0, 'in:#general finds nothing about refunds')

  // --- 4. opening a result: keyboard down and Enter to a message (channel at the post); a click on a thread (reply in view) ---
  hits = await search(r, 'refund rounding')
  const firstMessage = hits.findIndex(h => h.kind === 'message')
  for (let i = 0; i < firstMessage; i++) await r.keyboard.press('ArrowDown')
  const target = hits[firstMessage]
  expect(await r.getAttribute('[data-testid=search-hit][data-active=true]', 'data-post-id') === target.post, `↓ moves through the results (to "${target.text.slice(0, 50)}…")`)
  await r.keyboard.press('Enter')
  await r.waitForSelector(`[data-post="${target.post}"][data-focused]`)
  const inView = (sel) => r.evaluate((sel) => { const e = document.querySelector(sel); if (!e) return false; const b = e.getBoundingClientRect(); return b.top >= 0 && b.bottom <= innerHeight }, sel)
  expect(await inView(`[data-post="${target.post}"]`) && /\/c\/\d+/.test(new URL(r.url()).pathname), 'Enter opens the channel with that post in view and lit')
  await shot(r, '5-opened-message-dark')
  await r.keyboard.press('Control+k')
  await r.waitForSelector('[data-testid=search-view]')
  expect(await r.inputValue('[data-testid=search-input]') === '', 'Ctrl+K again: a fresh search')
  await search(r, 'half-up')
  const reply = sql("select id from posts where body like 'Do we round half-up%'")[0][0]
  await r.click(`[data-testid=search-hit][data-root="${root}"]`)
  await r.waitForSelector('[data-testid=thread-panel]')
  const focused = await r.waitForSelector('[data-testid=thread-panel] [data-post][data-focused]')
  const focusedId = Number(await focused.getAttribute('data-post'))
  expect(new URL(r.url()).pathname.endsWith(`/t/${root}`) && await inView(`[data-testid=thread-panel] [data-post="${focusedId}"]`) && [reply, ...answer.map(x => x[0])].includes(focusedId), `a thread result opens the thread with the matching reply in view (post ${focusedId})`)
  await shot(r, '6-opened-thread-dark')

  // --- light (the demo above ran in the default dark theme) ---
  await theme(r, 'light')
  await shot(r, '6-opened-thread-light')
  await r.keyboard.press('Control+k'); await search(r, 'refund rounding')
  await shot(r, '2-results-light')
  await search(r, ''); await shot(r, '1-search-empty-light')
  await search(r, 'refund in:#checkout-app'); await shot(r, '3-filter-chip-light')
  await search(r, 'budget'); await shot(r, '7-no-results-light')

  // --- the index rebuilds from zero to the same answers ---
  const before = (await api(r, 'GET', `/search?q=${encodeURIComponent('refund rounding')}`)).body
  expect((await api(a, 'POST', '/admin/projections/search/rebuild')).status === 200, 'Alex rebuilds the search index from zero')
  const after = (await api(r, 'GET', `/search?q=${encodeURIComponent('refund rounding')}`)).body
  expect(JSON.stringify(before) === JSON.stringify(after), `Rosa's results are the same after the rebuild (${after.threads.total} threads, ${after.messages.total} messages)`)

  // --- the phone: Search from the menu ---
  const P = await person(browser, { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, storageState: await R.ctx.storageState() })
  await P.page.goto(`${BASE}/search?q=${encodeURIComponent('refund rounding')}`)
  await P.page.waitForSelector('[data-testid=search-hit]')
  expect(await P.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth && ![...document.querySelectorAll('[data-testid=search-hit] *')].some(e => e.getBoundingClientRect().right > innerWidth + 0.5)), 'on a phone the results, and everything in them, fit the width')
  await shot(P.page, '8-phone-light')
  console.log('PHASE 8 LIVE CHECK PASSED')
} catch (e) {
  console.log('FAILED', e.message)
  await shot(a, 'FAIL-alex').catch(() => {}); await shot(r, 'FAIL-rosa').catch(() => {})
  console.log(server?.log?.slice(-3000))
  process.exitCode = 1
} finally {
  await stopGroup(runner); await stopGroup(server)
  await browser.close()
  SCRATCH.cleanup()
}
