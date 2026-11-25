import { describe, expect, test, beforeEach } from 'vitest'
import { parseChannelPath, threadState, patchSummary, loadFolded, toggleFolded, threadPath, threadsPath } from './threads'
import { applyEvent } from './reducer'
import type { FeedState, Post, ThreadSummary } from './types'

const summary = (rootId: number, over: Partial<ThreadSummary> = {}): ThreadSummary => ({
  rootId, channelId: 1, title: 'Why is the total a cent out?', titleFrom: 'words', startedBy: 2, agents: [{ id: 9, handle: 'researcher', displayName: 'researcher' }],
  status: null, lastAt: 0, createdAt: 0, replies: 0, resolvedAt: null, unread: 0, ...over,
})

describe('threads', () => {
  test('the URL says what is open: the feed, a quick look, All threads or a full view', () => {
    expect(parseChannelPath('/c/4')).toEqual({ channel: 4, thread: null, view: 'feed' })
    expect(parseChannelPath('/c/4/t/12')).toEqual({ channel: 4, thread: 12, view: 'panel' })
    expect(parseChannelPath('/c/4/threads')).toEqual({ channel: 4, thread: null, view: 'threads' })
    expect(parseChannelPath(threadPath(4, 12))).toEqual({ channel: 4, thread: 12, view: 'full' })
    expect(parseChannelPath(threadsPath(4) + '/')).toEqual({ channel: 4, thread: null, view: 'threads' })
    expect(parseChannelPath('/search')).toEqual({ channel: null, thread: null, view: 'feed' })
    expect(parseChannelPath('/p/3')).toEqual({ channel: null, thread: null, view: 'feed' })
  })
  test('a status as a dot and in words: waiting is "for you" only for whoever answers', () => {
    expect(threadState(null, 1)).toEqual({ dot: 'idle', label: 'idle' })
    expect(threadState({ state: 'working', waitingForId: null, waitingFor: null, outcome: null, outcomePostId: null }, 1).label).toBe('working')
    const waiting = { state: 'waiting' as const, waitingForId: 1, waitingFor: 'Alex', outcome: null, outcomePostId: null }
    expect(threadState(waiting, 1)).toEqual({ dot: 'waiting-you', label: 'waiting for you' })
    expect(threadState(waiting, 2)).toEqual({ dot: 'waiting', label: 'waiting for Alex' })
    expect(threadState({ state: 'done', waitingForId: null, waitingFor: null, outcome: 'x', outcomePostId: 3 }, 1).dot).toBe('done')
  })
  test('a live update changes a listed thread but keeps the person’s own unread count', () => {
    const list = [summary(1, { unread: 2 }), summary(2)]
    expect(patchSummary(list, summary(1, { title: 'Refund rounding', titleFrom: 'renamed', unread: 0 }))).toBe(true)
    expect(list[0]).toMatchObject({ title: 'Refund rounding', unread: 2 })
    expect(patchSummary(list, summary(3))).toBe(false)
    expect(list.length).toBe(2)
  })
  describe('folding', () => {
    beforeEach(() => { const m = new Map<string, string>(); (globalThis as any).localStorage = { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => m.set(k, v) } })
    test('a channel’s threads fold away and it is remembered per channel', () => {
      expect(loadFolded()).toEqual([])
      let f = toggleFolded([], 4)
      f = toggleFolded(f, 7)
      expect(loadFolded()).toEqual([4, 7])
      f = toggleFolded(f, 4)
      expect(f).toEqual([7]); expect(loadFolded()).toEqual([7])
    })
  })
  test('thread-updated gives the open thread its new title and status, keeping its unread count', () => {
    const root: Post = { id: 1, channelId: 1, threadId: null, authorId: 2, body: '@researcher why', kind: 'text', meta: null, streaming: false, createdAt: 0, replyCount: 0, hasThread: true }
    const s: FeedState = { channelId: 1, posts: [root], thread: { rootId: 1, root: { ...root }, replies: [], agents: [], startedBy: 2, summary: summary(1, { unread: 3 }) } }
    applyEvent(s, 'thread-updated', { rootId: 1, channelId: 1, post: { ...root, replyCount: 1 }, agents: [], thread: summary(1, { title: 'Tax rounds per line', titleFrom: 'outcome' }) })
    expect(s.thread!.summary).toMatchObject({ title: 'Tax rounds per line', titleFrom: 'outcome', unread: 3 })
    expect(s.posts[0].replyCount).toBe(1)
  })
})
