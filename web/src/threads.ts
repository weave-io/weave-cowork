// Threads you can find (docs/build/brief-8.md): what the channel list, All threads and the full view share. Pure of the browser's APIs
// except localStorage (guarded), so it's testable.
import type { ThreadStatus, ThreadSummary } from './types'

/** Where the URL says we are in a channel: its feed, a thread's quick look beside it, All threads, or a thread's full view. */
export type ChannelView = 'feed' | 'panel' | 'threads' | 'full'
export function parseChannelPath(path: string): { channel: number | null; thread: number | null; view: ChannelView } {
  const m = /^\/c\/(\d+)(?:\/(t|threads)(?:\/(\d+))?)?\/?$/.exec(path)
  if (!m) return { channel: null, thread: null, view: 'feed' }
  const channel = Number(m[1]), thread = m[3] ? Number(m[3]) : null
  if (m[2] === 't') return thread != null ? { channel, thread, view: 'panel' } : { channel, thread: null, view: 'feed' }
  if (m[2] === 'threads') return { channel, thread, view: thread != null ? 'full' : 'threads' }
  return { channel, thread: null, view: 'feed' }
}
export const threadPath = (channelId: number, rootId: number) => `/c/${channelId}/threads/${rootId}`
export const threadsPath = (channelId: number) => `/c/${channelId}/threads`

/** A thread's status as a dot and words: starting, working, waiting for you, waiting for someone (by name), done, or idle when nothing's going on. */
export type ThreadDotState = 'starting' | 'working' | 'waiting-you' | 'waiting' | 'done' | 'idle'
export function threadState(s: ThreadStatus | null | undefined, meId: number): { dot: ThreadDotState; label: string } {
  if (!s) return { dot: 'idle', label: 'idle' }
  if (s.state === 'waiting') return s.waitingForId === meId ? { dot: 'waiting-you', label: 'waiting for you' } : { dot: 'waiting', label: `waiting for ${s.waitingFor ?? 'someone'}` }
  return { dot: s.state, label: s.state }
}

/** Threads in "All threads", by the filter's own words. */
export const filters = [
  { id: 'all', label: 'All' }, { id: 'open', label: 'Open' }, { id: 'waiting', label: 'Waiting for me' }, { id: 'done', label: 'Done' },
] as const
export type ThreadFilter = typeof filters[number]['id']

/** A summary the list already shows, changed by a live update (title, status, agents, last activity), keeping the person's own unread count. */
export function patchSummary(list: ThreadSummary[], next: ThreadSummary): boolean {
  const i = list.findIndex(t => t.rootId === next.rootId)
  if (i === -1) return false
  list[i] = { ...next, unread: list[i].unread }
  return true
}

// A channel's threads fold away with its chevron, remembered per channel in this browser.
const FOLD_KEY = 'cowork:folded-threads'
function storage(): Storage | null { try { return typeof localStorage === 'undefined' ? null : localStorage } catch { return null } }
export function loadFolded(): number[] {
  try { const v = JSON.parse(storage()?.getItem(FOLD_KEY) ?? '[]'); return Array.isArray(v) ? v.filter(x => typeof x === 'number') : [] } catch { return [] }
}
export function saveFolded(ids: number[]) { try { storage()?.setItem(FOLD_KEY, JSON.stringify([...new Set(ids)])) } catch { /* private mode: not remembered */ } }
export function toggleFolded(ids: number[], channelId: number): number[] {
  const next = ids.includes(channelId) ? ids.filter(x => x !== channelId) : [...ids, channelId]
  saveFolded(next)
  return next
}
