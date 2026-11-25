import { beforeEach, expect, test } from 'vitest'
import { artifactPostArrived, canvas, canvasLink, closeCanvas, closeTab, formatSize, jsonTable, openInCanvas, parseCsv, shouldAutoOpen, shownVersion, threadArtifacts } from './artifacts'
import type { Post } from './types'

const card = (o: Partial<Post> & { open?: string } = {}): Post => ({
  id: 9, channelId: 2, threadId: 1, authorId: 5, body: 'Tax APIs compared', kind: 'artifact', streaming: false, createdAt: 0, replyCount: 0, hasThread: false,
  meta: { artifactId: 7, version: 1, open: o.open ?? 'canvas', latest: { version: 1 } }, ...o,
} as Post)

beforeEach(() => { closeCanvas(); canvas.threadId = null; canvas.host = null; canvas.panel = null; canvas.request = null })

test('the canvas opens by itself only for whoever started the thread, while they look at its full view, for a canvas card', () => {
  expect(shouldAutoOpen(card(), 3, 1, 3)).toBe(true)
  expect(shouldAutoOpen(card(), 4, 1, 3)).toBe(false)          // Alex didn't start it: he sees the card
  expect(shouldAutoOpen(card(), 3, null, 3)).toBe(false)       // Rosa isn't looking at the full view
  expect(shouldAutoOpen(card(), 3, 2, 3)).toBe(false)          // she's looking at another thread
  expect(shouldAutoOpen(card({ open: 'card' }), 3, 1, 3)).toBe(false)
  expect(shouldAutoOpen({ ...card(), kind: 'text' }, 3, 1, 3)).toBe(false)
})

test('a live card opens a tab for the starter, and only when it is new', () => {
  canvas.host = 1
  expect(artifactPostArrived(card(), false, 3, 3)).toBe(false)
  expect(artifactPostArrived(card(), true, 4, 3)).toBe(false)
  expect(canvas.tabs).toEqual([])
  expect(artifactPostArrived(card(), true, 3, 3)).toBe(true)
  expect(canvas.tabs).toEqual([{ id: 7, version: null }])
  expect([canvas.threadId, canvas.active]).toEqual([1, 7])
})

test('looking at the quick look, the starter is taken to the full view with the canvas', () => {
  canvas.panel = 1
  expect(artifactPostArrived(card(), true, 4, 3)).toBe(false)
  expect(canvas.request).toBeNull()
  expect(artifactPostArrived(card(), true, 3, 3)).toBe(true)
  expect(canvas.request).toEqual({ threadId: 1, id: 7 })
  expect(canvas.tabs).toEqual([])
})

test('tabs: one per artifact, a picked version, closing moves to the next, and another thread starts afresh', () => {
  openInCanvas(1, 7); openInCanvas(1, 8, 2); openInCanvas(1, 7, 1)
  expect(canvas.tabs).toEqual([{ id: 7, version: 1 }, { id: 8, version: 2 }])
  expect(canvas.active).toBe(7)
  closeTab(7)
  expect(canvas.active).toBe(8)
  openInCanvas(4, 9)
  expect(canvas.tabs).toEqual([{ id: 9, version: null }])
  closeTab(9)
  expect([canvas.tabs.length, canvas.active]).toEqual([0, null])
})

test('a tab follows the latest version that is not redacted, unless one was picked', () => {
  const v = (version: number, redacted = false) => ({ version, redacted } as any)
  const a = { versions: [v(3, true), v(2), v(1)] } as any
  expect(shownVersion(a, { id: 1, version: null })?.version).toBe(2)
  expect(shownVersion(a, { id: 1, version: 1 })?.version).toBe(1)
})

test('links, sizes and a thread\'s artifacts', () => {
  expect(canvasLink('https://cowork.lumen.example', 2, 1, 7, 2)).toBe('https://cowork.lumen.example/c/2/threads/1?a=7&v=2')
  expect([formatSize(900), formatSize(41_000), formatSize(3_400_000)]).toEqual(['900 B', '40 KB', '3.2 MB'])
  expect(threadArtifacts([card(), { ...card(), id: 10 }, { ...card(), id: 11, meta: { artifactId: 8 } }, { ...card(), kind: 'text' } as Post])).toEqual([7, 8])
})

test('CSV and JSON as tables', () => {
  expect(parseCsv('Provider,Note\r\nTallyrate,"per line, half-even"\nLevy.io,"says ""hi"""\n').rows).toEqual([['Provider', 'Note'], ['Tallyrate', 'per line, half-even'], ['Levy.io', 'says "hi"']])
  expect(parseCsv('a\n1\n2\n3\n', 2)).toEqual({ rows: [['a'], ['1']], more: true })
  expect(jsonTable([{ a: 1 }, { a: 2, b: 'x' }])).toEqual({ columns: ['a', 'b'], rows: [[1, undefined], [2, 'x']] })
  expect(jsonTable({ providers: [{ name: 'Tallyrate' }] })).toEqual({ columns: ['name'], rows: [['Tallyrate']] })
  expect(jsonTable({ a: 1 })).toBeNull()
})
