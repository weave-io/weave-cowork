// Shared helpers for the live checks (Playwright against a scratch server). Run with:
//   PLAYWRIGHT_HOST_PLATFORM_OVERRIDE=ubuntu24.04-x64 BASE=http://127.0.0.1:5881 node tests/live/phase1.mjs
import { chromium } from '/home/pgermishuys/source/crumb/node_modules/playwright/index.mjs'
import { homedir } from 'node:os'
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs'
export const BASE = process.env.BASE ?? 'http://127.0.0.1:5881'
export const SHOTS = process.env.SHOTS_DIR ? process.env.SHOTS_DIR.replace(/\/?$/, '/') : new URL('../../docs/screens/', import.meta.url).pathname
export const TIMEOUT = 20000
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
export function freshHarnessHome(prefix) {
  const root = `${homedir()}/.cache/cowork-tmp`
  mkdirSync(root, { recursive: true })
  const dir = mkdtempSync(`${root}/${prefix}-`)
  for (const d of ['home', 'config/opencode', 'config/cowork-runner', 'data', 'state', 'cache']) mkdirSync(`${dir}/${d}`, { recursive: true })
  writeFileSync(`${dir}/config/opencode/opencode.json`, JSON.stringify({ $schema: 'https://opencode.ai/config.json', permission: { bash: 'ask', edit: 'ask' } }))
  const env = { ...process.env, HOME: `${dir}/home`, XDG_CONFIG_HOME: `${dir}/config`, XDG_DATA_HOME: `${dir}/data`, XDG_STATE_HOME: `${dir}/state`, XDG_CACHE_HOME: `${dir}/cache` }
  return { dir, env, cleanup: () => rmSync(dir, { recursive: true, force: true }) }
}
