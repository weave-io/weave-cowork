// Shared helpers for the live checks (Playwright against a scratch server). Run with:
//   PLAYWRIGHT_HOST_PLATFORM_OVERRIDE=ubuntu24.04-x64 BASE=http://127.0.0.1:5881 node tests/live/phase1.mjs
import { chromium } from '/home/pgermishuys/source/crumb/node_modules/playwright/index.mjs'
import { homedir } from 'node:os'
import { execFileSync } from 'node:child_process'
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync, existsSync, symlinkSync } from 'node:fs'
export const BASE = process.env.BASE ?? 'http://127.0.0.1:5881'
export const SHOTS = process.env.SHOTS_DIR ? process.env.SHOTS_DIR.replace(/\/?$/, '/') : new URL('../../docs/screens/', import.meta.url).pathname
export const TIMEOUT = 20000
/** The sandbox image the live checks' team templates run (decided 25 Nov 2026): never cowork-sandbox-opencode:2.4.1, the tag the user's
 *  trial pool runs, which only its owner rebuilds. Build this one with TAG=cowork-sandbox-opencode:2.4.1-b9 deploy/sandbox/build.sh. */
export const SANDBOX_IMAGE = process.env.COWORK_SANDBOX_IMAGE || 'cowork-sandbox-opencode:2.4.1-b9'
export async function launch(extraArgs = []) {
  await ensureDex()
  return chromium.launch({ headless: true, executablePath: process.env.CHROME, args: ['--no-sandbox', ...extraArgs] })
}
export async function person(browser, opts = {}) {
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 860 }, colorScheme: 'light', ...opts })
  const page = await ctx.newPage()
  page.setDefaultTimeout(TIMEOUT)
  page.on('pageerror', e => console.log('PAGE ERROR', e.message))
  return { ctx, page }
}
let n = 0
export const ok = (msg) => console.log(`✓ ${++n}. ${msg}`)
export function expect(cond, msg) { if (!cond) throw new Error('FAILED: ' + msg); ok(msg) }

/** A scratch home for a real runner and a real harness (OpenCode 2), made fresh for each run and deleted at its end, so nothing carries between
 *  runs: not the runner's saved agents and sessions (a stale agent would reconnect to an old server), nor OpenCode's own data (a remembered
 *  "always allow" would stop the permission ask). The config makes the harness ask before shell commands and edits. Credentials come from the environment. */
/** The harness's sign-in and model. OpenCode's free models each have a usage limit (on 25 Nov 2026 all of them had reached it), so when
 *  the person's own OpenCode sign-in has GitHub Copilot, the checks use it by default (the user agreed, 25 Nov 2026): the sign-in is linked,
 *  never copied, and the model is github-copilot/claude-haiku-4.5. OPENCODE_MODEL picks another model; OPENCODE_SIGN_IN=none uses the free
 *  models with no sign-in; OPENCODE_SIGN_IN=link links the sign-in whatever it holds. */
const SIGN_IN = `${homedir()}/.local/share/opencode/auth.json`
const hasCopilot = (() => { try { return 'github-copilot' in JSON.parse(readFileSync(SIGN_IN, 'utf8')) } catch { return false } })()
const LINK_SIGN_IN = process.env.OPENCODE_SIGN_IN === 'link' || (process.env.OPENCODE_SIGN_IN !== 'none' && hasCopilot)
const MODEL_ID = process.env.OPENCODE_MODEL || (process.env.OPENCODE_SIGN_IN !== 'none' && hasCopilot ? 'github-copilot/claude-haiku-4.5' : '')
export const MODEL = MODEL_ID ? { model: MODEL_ID } : {}
export function freshHarnessHome(prefix) {
  const root = `${homedir()}/.cache/cowork-tmp`
  mkdirSync(root, { recursive: true })
  const dir = mkdtempSync(`${root}/${prefix}-`)
  for (const d of ['home', 'config/opencode', 'config/cowork-runner', 'data', 'state', 'cache']) mkdirSync(`${dir}/${d}`, { recursive: true })
  writeFileSync(`${dir}/config/opencode/opencode.json`, JSON.stringify({ $schema: 'https://opencode.ai/config.json', permission: { bash: 'ask', edit: 'ask' }, ...MODEL }))
  // The person's own OpenCode sign-in is linked (never copied) into the scratch data folder as the runner's own sign-in (see MODEL above).
  // The cleanup removes the link, not what it points to.
  if (LINK_SIGN_IN && existsSync(SIGN_IN)) { mkdirSync(`${dir}/data/opencode`, { recursive: true }); symlinkSync(SIGN_IN, `${dir}/data/opencode/auth.json`) }
  const env = { ...process.env, HOME: `${dir}/home`, XDG_CONFIG_HOME: `${dir}/config`, XDG_DATA_HOME: `${dir}/data`, XDG_STATE_HOME: `${dir}/state`, XDG_CACHE_HOME: `${dir}/cache` }
  return { dir, env, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}

/** Sign-in: CoWork has no passwords (brief 11), so the live checks sign people in through a real OpenID Connect provider, Dex, run in podman
 *  on the host network at 127.0.0.1:5556 and left running between checks (stop it with `podman stop cowork-live-dex`). Its users are the
 *  invented people of the checks (alex, rosa, sam, jo, and lee, who is never invited), all with the password "password" (Dex's own
 *  example hash): they are Dex's users, not CoWork's. A server under check is started with SSO_ENV; for phase1, 2 and 4, which use a
 *  server you start yourself, start it with these variables too. */
export const DEX_ISSUER = 'http://127.0.0.1:5556/dex'
export const SSO_ENV = { COWORK_OIDC__AUTHORITY: DEX_ISSUER, COWORK_OIDC__CLIENTID: 'cowork-live', COWORK_OIDC__CLIENTSECRET: 'cowork-live-secret' }
const DEX_IMAGE = 'ghcr.io/dexidp/dex:v2.44.0'
const DEX_PEOPLE = ['alex', 'rosa', 'sam', 'jo', 'lee']
const dexUp = async () => { try { return (await fetch(`${DEX_ISSUER}/.well-known/openid-configuration`)).ok } catch { return false } }
export async function ensureDex() {
  if (await dexUp()) return
  const dir = `${homedir()}/.cache/cowork-tmp/dex`
  mkdirSync(dir, { recursive: true })
  const redirects = []
  for (let port = 5880; port <= 5889; port++) for (const host of ['127.0.0.1', 'localhost']) redirects.push(`http://${host}:${port}/signin-oidc`)
  writeFileSync(`${dir}/config.yaml`, [
    `issuer: ${DEX_ISSUER}`,
    'storage: { type: memory }',
    'web: { http: 127.0.0.1:5556 }',
    'oauth2: { skipApprovalScreen: true }',
    'enablePasswordDB: true',
    'staticClients:',
    `  - { id: ${SSO_ENV.COWORK_OIDC__CLIENTID}, secret: ${SSO_ENV.COWORK_OIDC__CLIENTSECRET}, name: CoWork, redirectURIs: [${redirects.map(r => `'${r}'`).join(', ')}] }`,
    'staticPasswords:',
    ...DEX_PEOPLE.map((h, i) => `  - { email: '${h}@lumen.example', hash: '$2a$10$2b2cU8CPhOTaGrs1HRQuAueS7JTT5ZHsHSzYiFPm1leZck7Mc8T4W', username: '${h}', userID: '0000000${i}-dex-${h}' }`),
  ].join('\n') + '\n')
  execFileSync('podman', ['run', '-d', '--rm', '--name', 'cowork-live-dex', '--network', 'host', '-v', `${dir}/config.yaml:/etc/dex/config.yaml:ro,Z`, DEX_IMAGE, 'dex', 'serve', '/etc/dex/config.yaml'], { stdio: 'ignore' })
  for (const t0 = Date.now(); !(await dexUp()); ) { if (Date.now() - t0 > 30000) throw new Error('Dex did not start'); await new Promise(r => setTimeout(r, 250)) }
}
/** At Dex's sign-in page: sign in as one of its users. */
export async function atDex(page, handle) {
  await page.waitForURL(u => u.toString().startsWith(DEX_ISSUER))
  await page.fill('input[name=login]', `${handle}@lumen.example`); await page.fill('input[name=password]', 'password')
  await page.click('#submit-login')
}
/** The first person sets the server up: names it, signs in with single sign-on, chooses a handle. */
export async function setUp(page, handle = 'alex', name = 'Alex', server = 'Lumen Labs') {
  await page.fill('[name=serverName]', server); await page.click('[data-testid=sso-continue]')
  await atDex(page, handle)
  await page.waitForSelector('[data-testid=finish-form]')
  await page.fill('[name=handle]', handle); await page.fill('[name=displayName]', name); await page.click('button[type=submit]')
  await page.waitForSelector('[data-channel=general]')
}
/** On an invite link: sign in with single sign-on and join with a handle and display name. */
export async function ssoJoin(page, handle, name) {
  await page.click('[data-testid=sso-continue]')
  await atDex(page, handle)
  await page.waitForSelector('[data-testid=finish-form]')
  await page.fill('[name=handle]', handle); await page.fill('[name=displayName]', name); await page.click('button[type=submit]')
}
/** On the sign-in screen: a member signs in with single sign-on. */
export async function ssoSignIn(page, handle) {
  await page.click('[data-testid=sso-continue]')
  await atDex(page, handle)
}
