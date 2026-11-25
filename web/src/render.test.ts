// @vitest-environment jsdom
import { expect, test } from 'vitest'
import { plainText, renderBody, renderDocument } from './render'

test('escapes HTML', () => {
  const h = renderBody('<img src=x onerror=1> <script>alert(1)</script>')
  expect(h).not.toContain('<img')
  expect(h).not.toContain('<script')
  expect(h).toContain('&lt;script&gt;')
})
test('highlights only known mentions', () => {
  const h = renderBody('hi @reviewer and @nobody, mail rosa@lumen.example', new Set(['reviewer', 'lumen']))
  expect(h).toContain('<span class="mention">@reviewer</span>')
  expect(h).not.toContain('mention">@nobody')
  expect(h).not.toContain('mention">@lumen')
})
test('fenced code is escaped, kept and highlighted', () => {
  const h = renderBody('```ts\nif (a < b) { return "x" }\n```')
  expect(h).toContain('&lt;')
  expect(h).toContain('<pre class="hljs" data-lang="ts">')
  expect(h).toContain('hljs-keyword')
})
test('GitHub-flavoured markdown: a heading, a list, a table, inline code and a link that opens in a new tab', () => {
  const h = renderBody('### Short version\n\n- Tallyrate refunds per line\n- Levy.io recalculates\n\n| Provider | Rounding |\n|---|:-:|\n| Tallyrate | half-even |\n\nSee `roundTax` and [the docs](https://docs.tallyrate.example).')
  expect(h).toContain('<h3>Short version</h3>')
  expect(h).toContain('<li>Tallyrate refunds per line</li>')
  expect(h).toContain('<div class="table-wrap"><table>')
  expect(h).toContain('<th>Provider</th>')
  expect(h).toContain('<td style="text-align:center">half-even</td>')
  expect(h).toContain('<code>roundTax</code>')
  expect(h).toMatch(/<a href="https:\/\/docs\.tallyrate\.example" target="_blank" rel="noopener noreferrer nofollow">the docs<\/a>/)
})
test('task lists, quotes and bare links', () => {
  const h = renderBody('- [x] Read src/tax.ts\n- [ ] Check the sandbox\n\n> quoted\n\nhttps://example.com/x')
  expect(h).toContain('class="task-list-item task-list-item--done"')
  expect(h).toContain('class="task-list-item"')
  expect(h).not.toContain('[x]')
  expect(h).toContain('<blockquote>')
  expect(h).toContain('href="https://example.com/x"')
})
test('no script links, no images loaded by a post', () => {
  const h = renderBody('[click](javascript:alert(1)) ![chart](https://tracker.example/p.png)')
  expect(h).not.toContain('href="javascript:')
  expect(h).not.toContain('<img')
  expect(h).toContain('href="https://tracker.example/p.png"')
})
test('a markdown artifact loads its own images from its signed address', () => {
  const h = renderDocument('# Report\n\n![chart](img/chart.png) ![abs](https://elsewhere.example/x.png)', '/a/1-1-9-sig/')
  expect(h).toContain('<img src="/a/1-1-9-sig/img/chart.png" alt="chart">')
  expect(h).toContain('<h1>Report</h1>')
})
test('plain text for previews', () => {
  expect(plainText('### Short version\n- **Tallyrate** refunds per line, see [docs](https://x.example) and `roundTax`')).toBe('Short version Tallyrate refunds per line, see docs and roundTax')
})
