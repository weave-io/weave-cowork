// Shared helpers for the live checks (Playwright against a scratch server). Run with:
//   PLAYWRIGHT_HOST_PLATFORM_OVERRIDE=ubuntu24.04-x64 BASE=http://127.0.0.1:5881 node tests/live/phase1.mjs
import { chromium } from '/home/pgermishuys/source/crumb/node_modules/playwright/index.mjs'
import { homedir } from 'node:os'
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync, existsSync, symlinkSync } from 'node:fs'
export const BASE = process.env.BASE ?? 'http://127.0.0.1:5881'
export const SHOTS = process.env.SHOTS_DIR ? process.env.SHOTS_DIR.replace(/\/?$/, '/') : new URL('../../docs/screens/', import.meta.url).pathname
export const TIMEOUT = 20000
/** The sandbox image the live checks' team templates run (decided 25 Nov 2026): never cowork-sandbox-opencode:2.4.1, the tag the user's
 *  trial pool runs, which only its owner rebuilds. Build this one with TAG=cowork-sandbox-opencode:2.4.1-b9 deploy/sandbox/build.sh. */
export const SANDBOX_IMAGE = process.env.COWORK_SANDBOX_IMAGE || 'cowork-sandbox-opencode:2.4.1-b9'
export async function launch(extraArgs = []) {
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
