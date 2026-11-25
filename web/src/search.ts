import { ref } from 'vue'

// Search helpers: the server marks matching words in a snippet with \u0002 … \u0003 (never HTML), and filters are words in the box.

export interface Segment { text: string; hit: boolean }

/** "a \u0002refund\u0003 drifts" → [{a }, {refund, hit}, { drifts}]. Rendered as text and <mark>, so nothing in a message is HTML. */
export function highlight(snippet: string): Segment[] {
  const out: Segment[] = []
  for (const [i, part] of snippet.split(/[\u0002\u0003]/).entries()) if (part) out.push({ text: part, hit: i % 2 === 1 })
  return out
}

const words = (q: string) => q.split(/\s+/).filter(Boolean)
const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase()

/** Whether the box holds this filter (`in:#checkout-app`, `from:@reviewer`, `is:thread`), in any case. */
export const hasFilter = (q: string, filter: string) => words(q).some(w => same(w, filter))

/** A chip under the box: adds its filter to what's typed, or takes it out again. */
export function toggleFilter(q: string, filter: string): string {
  if (hasFilter(q, filter)) return words(q).filter(w => !same(w, filter)).join(' ')
  const t = q.trimEnd()
  return (t ? `${t} ${filter}` : filter) + ' '
}

/** Where a result opens: the channel at the post, or the thread with the reply in view. */
export function resultPath(r: { channelId: number; threadId?: number | null; postId: number }): string {
  return r.threadId ? `/c/${r.channelId}/t/${r.threadId}?post=${r.postId}` : `/c/${r.channelId}?post=${r.postId}`
}

/** Ctrl+K / ⌘K counts this up: an open Search view takes the focus. */
export const searchFocus = ref(0)
/** "⌘K" on a Mac, "Ctrl K" elsewhere. */
export const searchShortcut = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform) ? '⌘K' : 'Ctrl K'
