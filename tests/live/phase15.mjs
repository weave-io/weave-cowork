// Brief 11: sign in with single sign-on, no passwords. A real OpenID Connect provider (Dex, in podman, see lib.mjs) and browsers for Alex, Rosa
// and Lee. The demo: Alex opens a fresh server, names it Lumen Labs and continues with single sign-on; Dex signs him in and he comes back to
// choose a handle (suggested from Dex's username) and is the admin → he makes an invite link → Rosa opens it in her own browser: "Alex invited
// you to Lumen Labs", continues with single sign-on, signs in at Dex, keeps the handle it suggests, and is in #general as a member → Lee, whom
// Dex knows but nobody invited, is told he isn't a member of Lumen Labs, and "Use another account" takes him back to Dex → Rosa signs out and
// back in and is still @rosa → the database has no password anywhere; each person is Dex's issuer and subject; the event log holds neither
// subject nor email → a server started without a provider says sign-in isn't set up, and offers no form.
// Starts its own servers (default ports 5887 and 5886).
//   node tests/live/phase15.mjs          (screenshots go to docs/screens/brief11/)
import { launch, person, expect, ok, SSO_ENV, DEX_ISSUER, atDex } from './lib.mjs'
import { spawn, execFileSync } from 'node:child_process'
import { homedir } from 'node:os'
import { rmSync, mkdirSync } from 'node:fs'

const HOME = homedir()
const PORT = process.env.PORT ?? '5887', PORT_BARE = process.env.PORT_BARE ?? '5886'
const BASE = `http://127.0.0.1:${PORT}`, BARE = `http://127.0.0.1:${PORT_BARE}`
const REPO = new URL('../../', import.meta.url).pathname.replace(/\/$/, '')
const DIR = `${HOME}/.cache/cowork-p15`
const DB = `${DIR}/data/cowork.db`, DB_BARE = `${DIR}/bare/cowork.db`
const SHOTS = process.env.SHOTS_DIR ?? `${REPO}/docs/screens/brief11/`
const SERVER = `${REPO}/src/CoWork.Server/bin/Debug/net10.0/CoWork.Server.dll`
rmSync(DIR, { recursive: true, force: true }); mkdirSync(`${DIR}/data`, { recursive: true }); mkdirSync(`${DIR}/bare`, { recursive: true }); mkdirSync(SHOTS, { recursive: true })
const sql = (q) => JSON.parse(execFileSync('python3', ['-I', '-c', 'import sqlite3,sys,json;c=sqlite3.connect(sys.argv[1]);print(json.dumps(c.execute(sys.argv[2]).fetchall()))', DB, q]).toString())
const until = async (what, probe, ms = 30000) => { const end = Date.now() + ms; for (;;) { const v = await probe(); if (v) return v; if (Date.now() > end) throw new Error('FAILED: timed out waiting for ' + what); await new Promise(r => setTimeout(r, 250)) } }
const clean = (t) => (t ?? '').replace(/\s+/g, ' ').trim()

const servers = []
function startServer(base, db, env) {
  const s = spawn('dotnet', [SERVER], { cwd: `${REPO}/src/CoWork.Server`, env: { ...process.env, ...env, COWORK_DB: db, ASPNETCORE_URLS: base }, stdio: ['ignore', 'pipe', 'pipe'], detached: true })
  s.log = ''; s.stdout.on('data', d => s.log += d); s.stderr.on('data', d => s.log += d)
  servers.push(s)
  return until('the server to listen', async () => { try { return (await fetch(base + '/api/setup/status')).ok } catch { return false } })
}
const stopGroup = (p) => new Promise(res => { if (!p || p.exitCode !== null) return res(); p.once('exit', res); try { process.kill(-p.pid, 'SIGTERM') } catch { return res() } setTimeout(res, 8000) })

const browser = await launch()           // starts Dex if it isn't running
const A = await person(browser), R = await person(browser), L = await person(browser), N = await person(browser)
const a = A.page, r = R.page, l = L.page, n = N.page
const shot = async (p, name) => { await p.waitForTimeout(300); await p.screenshot({ path: SHOTS + name + '.png' }) }
const setTheme = (p, t) => p.evaluate(t => { localStorage.setItem('weave:theme', t); document.documentElement.dataset.theme = t; document.documentElement.style.colorScheme = t }, t)
const shots = async (p, name) => { for (const theme of ['light', 'dark']) { await setTheme(p, theme); await shot(p, `${name}-${theme}`) } await setTheme(p, 'light') }

try {
  await startServer(BASE, DB, SSO_ENV)

  // --- 1. Alex sets the server up through the provider ---
  await a.goto(BASE)
  await a.waitForSelector('[data-testid=sso-continue]')
  expect(await a.locator('input[type=password]').count() === 0, 'the setup screen has no password field')
  await a.fill('[name=serverName]', 'Lumen Labs')
  await shots(a, 'setup')
  await a.click('[data-testid=sso-continue]')
  await atDex(a, 'alex')
  ok(`"Continue with single sign-on" went to Dex (${DEX_ISSUER}) and Alex signed in there`)
  await a.waitForSelector('[data-testid=finish-form]')
  expect(clean(await a.innerText('[data-testid=sso-who]')).includes('alex@lumen.example'), 'back at CoWork, it says who Dex signed in: alex@lumen.example')
  expect(await a.inputValue('[name=handle]') === 'alex' && await a.inputValue('[name=serverName]') === 'Lumen Labs', 'the handle is suggested from Dex (alex) and the server name he typed is kept')
  await a.fill('[name=displayName]', 'Alex')
  await shots(a, 'setup-finish')
  await a.click('button[type=submit]')
  await a.waitForSelector('[data-channel=general]')
  const boot = await a.evaluate(async () => (await fetch('/api/bootstrap')).json())
  expect(boot.isAdmin && boot.server.name === 'Lumen Labs', 'Alex is in Lumen Labs, #general is there, and he is the admin')

  // --- 2. an invite link; Rosa joins with single sign-on ---
  const code = await a.evaluate(async () => (await (await fetch('/api/invites', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' })).json()).code)
  await r.goto(`${BASE}/invite/${code}`)
  await r.waitForSelector('[data-testid=sso-continue]')
  expect(clean(await r.innerText('[data-testid=auth-card]')).includes('Alex invited you to Lumen Labs'), 'Rosa\'s invite says "Alex invited you to Lumen Labs"')
  expect(await r.locator('input').count() === 0, 'the invite has no fields at all: one button, to the provider')
  await shots(r, 'invite')
  await r.click('[data-testid=sso-continue]')
  await atDex(r, 'rosa')
  await r.waitForSelector('[data-testid=finish-form]')
  expect(new URL(r.url()).pathname === `/invite/${code}`, 'Dex sent Rosa back to her invite, to choose how people see her')
  expect(await r.inputValue('[name=handle]') === 'rosa', 'her handle is suggested from Dex: rosa')
  await r.fill('[name=handle]', 'alex')
  await r.fill('[name=displayName]', 'Rosa'); await r.click('button[type=submit]')
  await r.waitForSelector('[data-testid=auth-error]')
  expect(clean(await r.innerText('[data-testid=auth-error]')).includes('Someone here is already @alex. Try another.'), 'a taken handle says "Someone here is already @alex. Try another."')
  await r.fill('[name=handle]', 'rosa')
  await shots(r, 'invite-finish')
  await r.click('button[type=submit]')
  await r.waitForSelector('[data-channel=general]')
  expect(new URL(r.url()).pathname === '/', 'Rosa is in Lumen Labs, and the address is the app\'s, not the invite\'s')
  const rboot = await r.evaluate(async () => (await fetch('/api/bootstrap')).json())
  expect(!rboot.isAdmin && rboot.members.some(m => m.handle === 'rosa'), 'Rosa is a member, not an admin')

  // --- 3. Lee: Dex knows him, nobody invited him ---
  await l.goto(BASE)
  await l.waitForSelector('[data-testid=sso-continue]')
  await l.click('[data-testid=sso-continue]')
  await atDex(l, 'lee')
  await l.waitForSelector('[data-testid=not-member]')
  expect(clean(await l.innerText('[data-testid=not-member]')).includes("lee@lumen.example isn't a member of Lumen Labs."), 'Lee is told "lee@lumen.example isn\'t a member of Lumen Labs. Ask someone here for an invite link."')
  expect((await l.evaluate(async () => (await fetch('/api/bootstrap')).status)) === 401, 'and he is not signed in: the API says 401')
  await shots(l, 'not-member')
  await l.click('[data-testid=sso-continue]')
  await l.waitForURL(u => u.toString().startsWith(DEX_ISSUER))
  ok('"Use another account" takes Lee back to Dex to sign in afresh')

  // --- 4. Rosa signs out and back in ---
  await r.click('[data-testid=user-menu]'); await r.click('[data-testid=sign-out]')
  await r.waitForSelector('[data-testid=sso-continue]')
  expect(await r.locator('input').count() === 0, 'the sign-in screen is one button: no username, no password')
  await shots(r, 'signin')
  await r.click('[data-testid=sso-continue]')
  await atDex(r, 'rosa')                 // Dex keeps no session of its own, so it asks again
  await r.waitForSelector('[data-channel=general]')
  const again = await r.evaluate(async () => (await fetch('/api/bootstrap')).json())
  expect(again.meId === rboot.meId, 'signed out and back in, Rosa is the same member (@rosa)')

  // --- 5. what the server keeps ---
  const tables = sql("select name from sqlite_master where type='table'").map(t => t[0])
  expect(!tables.includes('credentials') && !sql("select sql from sqlite_master where sql like '%password%'").length, 'no table or column for passwords in the database')
  const logins = sql('select issuer, subject, email from logins order by created_at')
  expect(logins.length === 2 && logins.every(([iss]) => iss === DEX_ISSUER) && logins.map(x => x[2]).join() === 'alex@lumen.example,rosa@lumen.example',
    `two sign-ins, each Dex's issuer and its subject (${logins.map(x => x[1].slice(0, 12) + '…').join(', ')})`)
  const log = sql('select data from events').map(x => x[0]).join('\n')
  expect(logins.every(([, sub, email]) => !log.includes(sub) && !log.includes(email)), 'the event log holds neither subject nor email')

  // --- 6. a server nobody gave a provider ---
  await startServer(BARE, DB_BARE, {})
  await n.goto(BARE)
  await n.waitForSelector('[data-testid=sso-missing]')
  expect(clean(await n.innerText('[data-testid=auth-card]')).includes("Sign-in isn't set up yet") && await n.locator('input').count() === 0,
    'a server without a provider says "Sign-in isn\'t set up yet", names the three settings, and offers no form')
  await shots(n, 'sso-missing')
  console.log('PHASE 15 LIVE CHECK PASSED')
} catch (e) {
  console.log('FAILED', e.message)
  for (const [p, nm] of [[a, 'alex'], [r, 'rosa'], [l, 'lee'], [n, 'bare']]) await shot(p, 'FAIL-' + nm).catch(() => {})
  for (const s of servers) if (s.log) console.log(s.log.split('\n').filter(x => /fail|error|exception|warn/i.test(x)).slice(-20).join('\n'))
  process.exitCode = 1
} finally {
  for (const s of servers) await stopGroup(s)
  await browser.close()
}
