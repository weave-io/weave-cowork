import type { FeedState, Post, ThreadAgent } from './types'

/** Applies one server event to the feed (mutates it). Pure of any browser API, so it's testable. */
export function applyEvent(s: FeedState, evt: string, p: any): void {
  switch (evt) {
    case 'post-created': return onCreated(s, p as Post)
    case 'post-updated': return onUpdated(s, p as Post)
    case 'post-upsert': return find(s, p.id) ? onUpdated(s, p as Post) : onCreated(s, p as Post)       // catching up: add it, or replace what we have
    case 'post-delta': {
      const t = find(s, p.id)
      if (!t) return
      // deltas aren't stored, so they say where in the answer they start; text we already have is skipped, and a gap
      // (we missed some) waits for the next stored segment to fill it
      if (p.offset === undefined || p.offset === t.body.length) t.body += p.text
      else if (p.offset < t.body.length && p.offset + p.text.length > t.body.length) t.body += p.text.slice(t.body.length - p.offset)
      return
    }
    case 'thread-updated': {
      if (p.channelId !== s.channelId) return
      const root = s.posts.find(x => x.id === p.rootId)
      if (root) Object.assign(root, p.post)
      if (s.thread && s.thread.rootId === p.rootId) {
        Object.assign(s.thread.root, p.post)
        s.thread.agents = p.agents as ThreadAgent[]
      }
      return
    }
  }
}

function find(s: FeedState, id: number): Post | undefined {
  return s.posts.find(x => x.id === id) ?? s.thread?.replies.find(x => x.id === id)
}

function onCreated(s: FeedState, post: Post) {
  if (post.channelId !== s.channelId) return
  if (post.threadId == null) {
    if (!s.posts.some(x => x.id === post.id)) insertInOrder(s.posts, post)
    return
  }
  if (s.thread && s.thread.rootId === post.threadId && !s.thread.replies.some(x => x.id === post.id)) insertInOrder(s.thread.replies, post)
}

/** Posts are shown in the order they were made (their ids rise with time), whatever order they reach us in. */
function insertInOrder(list: Post[], post: Post) {
  const i = list.findIndex(x => x.id > post.id)
  if (i === -1) list.push(post); else list.splice(i, 0, post)
}

function onUpdated(s: FeedState, post: Post) {
  const t = find(s, post.id)
  if (!t) return
  // an older copy never replaces a newer one (a catch-up can arrive after a live update)
  if (post.position !== undefined && t.position !== undefined && post.position < t.position) return
  // a streaming answer never gets shorter: the typing we already show may be ahead of what's stored
  if (t.streaming && post.streaming && post.body.length < t.body.length) { Object.assign(t, { ...post, body: t.body }); return }
  Object.assign(t, post)
}
