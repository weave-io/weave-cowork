import { readFileSync } from 'node:fs'
import { expect, test } from 'vitest'
import { themes } from './theme'
import { avatarColors } from './avatarColors'

// Text keeps 4.5:1 on the surfaces it sits on, in every theme (Fleet's tokens already do; this guards our copy of them).
const css = readFileSync(new URL('./assets/main.css', import.meta.url), 'utf8')
function block(id: string) {
  const sel = id === 'dark' ? ':root {' : `:root[data-theme="${id}"] {`
  const start = css.indexOf(sel); expect(start, id).toBeGreaterThan(-1)
  return css.slice(start, css.indexOf('\n}', start))
}
const vars = (id: string) => {
  const out: Record<string, string> = {}
  for (const src of id === 'dark' ? [block('dark')] : [block('dark'), block(id)])
    for (const m of src.matchAll(/--([a-z0-9-]+):\s*([^;]+);/g)) out[m[1]] = m[2].trim()
  return out
}
const lum = (hex: string) => {
  const c = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16) / 255).map(v => v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4)
  return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]
}
const ratio = (a: string, b: string) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05) }
const resolve = (v: Record<string, string>, name: string): string => { let x = v[name]; for (let i = 0; i < 4 && x?.startsWith('var('); i++) x = v[x.slice(6, -1)]; return x }

for (const t of themes) test(`${t.id}: text reads at 4.5:1 on its surfaces`, () => {
  const v = vars(t.id)
  const surfaces = ['rail-bg', 'panel-bg', 'card-bg', 'main-bg'].map(n => resolve(v, n))
  for (const s of surfaces) for (const fg of ['text', 'muted', 'accent-text', 'error-text', 'running-text', 'idle-text'])
    expect(ratio(resolve(v, fg), s), `${fg} on ${s}`).toBeGreaterThanOrEqual(4.5)
})

test('avatar colours: white initials read at 4.5:1 and no two colours are alike', () => {
  for (const c of avatarColors) expect(ratio('#FFFFFF', c), c).toBeGreaterThanOrEqual(4.5)
  expect(new Set(avatarColors).size).toBe(avatarColors.length)
})
