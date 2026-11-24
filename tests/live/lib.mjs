// Shared helpers for the live checks (Playwright against a scratch server). Run with:
//   PLAYWRIGHT_HOST_PLATFORM_OVERRIDE=ubuntu24.04-x64 BASE=http://127.0.0.1:5881 node tests/live/phase1.mjs
import { chromium } from '/home/pgermishuys/source/crumb/node_modules/playwright/index.mjs'
import { homedir } from 'node:os'
export const BASE = process.env.BASE ?? 'http://127.0.0.1:5881'
export const SHOTS = new URL('../../docs/screens/', import.meta.url).pathname
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
