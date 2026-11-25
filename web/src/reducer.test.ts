import { describe, expect, test } from 'vitest'
import { applyEvent } from './reducer'
import type { FeedState, Post } from './types'

const post = (id: number, over: Partial<Post> = {}): Post => ({
  id, channelId: 1, threadId: null, authorId: 1, body: 'hi', kind: 'text', meta: null, streaming: false, createdAt: 0, replyCount: 0, hasThread: false, ...over,
})
const feed = (over: Partial<FeedState> = {}): FeedState => ({ channelId: 1, posts: [], thread: null, ...over })

describe('applyEvent', () => {
  test('a new top-level post is appended once', () => {
    const s = feed()
    applyEvent(s, 'post-created', post(1)); applyEvent(s, 'post-created', post(1))
    expect(s.posts.map(p => p.id)).toEqual([1])
  })
  test('posts for another channel are ignored', () => {
    const s = feed()
    applyEvent(s, 'post-created', post(1, { channelId: 2 }))
    expect(s.posts).toEqual([])
  })
  test('a reply goes to the open thread only', () => {
    const s = feed({ thread: { rootId: 1, root: post(1), replies: [], agents: [], startedBy: 1 } })
    applyEvent(s, 'post-created', post(2, { threadId: 1 }))
    applyEvent(s, 'post-created', post(3, { threadId: 99 }))
    expect(s.thread!.replies.map(p => p.id)).toEqual([2])
    expect(s.posts).toEqual([])
  })
  test('streamed deltas append to the post body, in the feed or the thread', () => {
    const s = feed({ thread: { rootId: 1, root: post(1), replies: [post(2, { threadId: 1, body: 'He', streaming: true })], agents: [], startedBy: 1 } })
    applyEvent(s, 'post-delta', { id: 2, text: 'llo' })
    expect(s.thread!.replies[0].body).toBe('Hello')
  })
  test('post-updated replaces fields (streaming ends, tool finishes)', () => {
    const s = feed({ posts: [post(1, { streaming: true })] })
    applyEvent(s, 'post-updated', post(1, { streaming: false, body: 'done' }))
    expect(s.posts[0]).toMatchObject({ streaming: false, body: 'done' })
  })
  test('thread-updated refreshes the reply count and the thread agents', () => {
    const s = feed({ posts: [post(1)], thread: { rootId: 1, root: post(1), replies: [], agents: [], startedBy: 1 } })
    applyEvent(s, 'thread-updated', { rootId: 1, channelId: 1, post: post(1, { replyCount: 2, hasThread: true }), agents: [{ id: 7, handle: 'reviewer' }] })
    expect(s.posts[0].replyCount).toBe(2)
    expect(s.thread!.agents).toHaveLength(1)
  })
  test('a delta that starts where the text ends is appended; text we already have is skipped', () => {
    const s = feed({ posts: [post(1, { body: 'Hello', streaming: true })] })
    applyEvent(s, 'post-delta', { id: 1, text: ' wor', offset: 5 })
    applyEvent(s, 'post-delta', { id: 1, text: 'lo wor', offset: 3 })      // already shown, nothing new
    applyEvent(s, 'post-delta', { id: 1, text: 'ld', offset: 9 })
    expect(s.posts[0].body).toBe('Hello world')
  })
  test('a delta after a gap waits for the stored text instead of garbling the answer', () => {
    const s = feed({ posts: [post(1, { body: 'Hel', streaming: true })] })
    applyEvent(s, 'post-delta', { id: 1, text: 'orld', offset: 7 })
    expect(s.posts[0].body).toBe('Hel')
  })
  test('catching up adds a post we never saw and replaces one we have', () => {
    const s = feed({ posts: [post(1, { body: 'old', position: 3 })] })
    applyEvent(s, 'post-upsert', post(1, { body: 'new', position: 5 }))
    applyEvent(s, 'post-upsert', post(2, { position: 6 }))
    expect(s.posts.map(p => [p.id, p.body])).toEqual([[1, 'new'], [2, 'hi']])
  })
  test('an older copy never replaces a newer one', () => {
    const s = feed({ posts: [post(1, { body: 'newer', position: 9 })] })
    applyEvent(s, 'post-updated', post(1, { body: 'older', position: 4 }))
    expect(s.posts[0].body).toBe('newer')
  })
  test('a streaming answer never gets shorter when a stored segment arrives', () => {
    const s = feed({ posts: [post(1, { body: 'Hello world', streaming: true, position: 4 })] })
    applyEvent(s, 'post-updated', post(1, { body: 'Hello', streaming: true, position: 5 }))
    expect(s.posts[0].body).toBe('Hello world')
    applyEvent(s, 'post-updated', post(1, { body: 'Hello world!', streaming: false, position: 6 }))
    expect(s.posts[0]).toMatchObject({ body: 'Hello world!', streaming: false })
  })
  test('posts that arrive out of order are shown in the order they were made', () => {
    const s = feed({ thread: { rootId: 1, root: post(1), replies: [], agents: [], startedBy: 1 } })
    for (const id of [4, 2, 3]) applyEvent(s, 'post-upsert', post(id, { threadId: 1 }))
    expect(s.thread!.replies.map(p => p.id)).toEqual([2, 3, 4])
  })
})
