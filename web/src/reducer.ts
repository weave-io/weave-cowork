import type { FeedState, Post, ThreadAgent } from './types'

/** Applies one server event to the feed (mutates it). Pure of any browser API, so it's testable. */
export function applyEvent(s: FeedState, evt: string, p: any): void {
  switch (evt) {
    case 'post-created': return onCreated(s, p as Post)
    case 'post-updated': return onUpdated(s, p as Post)
    case 'post-delta': {
      const t = find(s, p.id)
      if (t) { t.body += p.text }
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
    if (!s.posts.some(x => x.id === post.id)) s.posts.push(post)
    return
  }
  if (s.thread && s.thread.rootId === post.threadId && !s.thread.replies.some(x => x.id === post.id)) s.thread.replies.push(post)
}

function onUpdated(s: FeedState, post: Post) {
  const t = find(s, post.id)
  if (t) Object.assign(t, post)
}
