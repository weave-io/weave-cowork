import { expect, test } from 'vitest'
import { renderBody } from './render'

test('escapes HTML', () => expect(renderBody('<img src=x onerror=1>')).not.toContain('<img'))
test('highlights only known mentions', () => {
  const h = renderBody('hi @reviewer and @nobody', new Set(['reviewer']))
  expect(h).toContain('<span class="mention">@reviewer</span>')
  expect(h).not.toContain('mention">@nobody')
})
test('fenced code is escaped and kept', () => expect(renderBody('```ts\nif (a < b) {}\n```')).toContain('a &lt; b'))
