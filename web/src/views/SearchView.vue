<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { Search, Menu, MessageSquare, X, Hash } from 'lucide-vue-next'
import { get, ApiError } from '../api'
import { store, memberById, me } from '../store'
import { isPhone, navOpen } from '../layout'
import { hasFilter, toggleFilter, resultPath, searchFocus } from '../search'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorNote } from '@/components/ui/error-note'
import { Spinner } from '@/components/ui/spinner'
import { RelativeTime } from '@/components/ui/relative-time'
import MemberAvatar from '../components/MemberAvatar.vue'
import AgentBadge from '../components/AgentBadge.vue'
import Highlighted from '../components/Highlighted.vue'

interface ThreadHit {
  rootId: number; channelId: number; channelName: string; authorId: number; at: number; title: string; replyCount: number; matches: number
  agents: string[]; match: { postId: number; authorId: number; snippet: string } | null
}
interface MessageHit { postId: number; channelId: number; channelName: string; threadId: number | null; authorId: number; at: number; snippet: string; threadTitle: string | null }
interface Results { empty: boolean; threads: { total: number; items: ThreadHit[] }; messages: { total: number; items: MessageHit[] } }

const route = useRoute(), router = useRouter()
const q = ref(typeof route.query.q === 'string' ? route.query.q : '')
const results = ref<Results | null>(null), loading = ref(false), error = ref(''), more = ref(false)
const active = ref(0)
const input = ref<HTMLInputElement | null>(null)
const list = ref<HTMLElement | null>(null)
const pageSize = 20

// what the chips offer: the channels the results are in (most first) and the one you came from, the agents, you, threads only
const lastIn = ref<string[]>([])
watch(results, r => { if (r && !r.empty) lastIn.value = channelsOf(r) })
function channelsOf(r: Results) {
  const n = new Map<string, number>()
  for (const name of [...r.threads.items.map(t => t.channelName), ...r.messages.items.map(m => m.channelName)]) n.set(name, (n.get(name) ?? 0) + 1)
  return [...n.entries()].sort((a, b) => b[1] - a[1]).map(e => e[0])
}
const chips = computed(() => {
  const came = store.channels.find(c => c.id === store.feed.channelId && c.kind === 'text') ?? store.channels.find(c => c.kind === 'text')
  const ins = [...new Set([...lastIn.value, ...(came ? [came.name] : [])])].slice(0, 3)
  const agents = store.members.filter(m => m.kind === 'agent' && !m.removed).sort((a, b) => a.handle.localeCompare(b.handle)).slice(0, 3)
  return [
    ...ins.map(name => `in:#${name}`),
    ...agents.map(a => `from:@${a.handle}`),
    ...(me.value ? [`from:@${me.value.handle}`] : []),
    'is:thread',
  ]
})

type Flat = { kind: 'thread'; hit: ThreadHit } | { kind: 'message'; hit: MessageHit }
const flat = computed<Flat[]>(() => results.value ? [
  ...results.value.threads.items.map(hit => ({ kind: 'thread' as const, hit })),
  ...results.value.messages.items.map(hit => ({ kind: 'message' as const, hit })),
] : [])
const total = computed(() => results.value ? results.value.threads.total + results.value.messages.total : 0)

let seq = 0, timer: ReturnType<typeof setTimeout> | null = null
async function run(offset = 0) {
  const text = q.value, mine = ++seq
  if (offset === 0) router.replace({ path: '/search', query: text.trim() ? { q: text } : {} })
  if (!text.trim()) { results.value = null; loading.value = false; return }
  loading.value = offset === 0; more.value = offset > 0; error.value = ''
  try {
    const r: Results = await get(`/search?q=${encodeURIComponent(text)}&limit=${pageSize}&offset=${offset}`)
    if (mine !== seq) return                       // an older answer that arrived late: the box has moved on
    if (offset > 0 && results.value) results.value.messages.items.push(...r.messages.items)
    else { results.value = r; active.value = 0 }
  } catch (e) { if (mine === seq) error.value = e instanceof ApiError ? e.message : 'Search didn’t work. Try again.' }
  finally { if (mine === seq) { loading.value = false; more.value = false } }
}
// results arrive as you type
watch(q, () => { if (timer) clearTimeout(timer); timer = setTimeout(() => run(), 120) })
onMounted(() => { focus(); if (q.value.trim()) run() })
watch(searchFocus, focus)
function focus() { nextTick(() => { input.value?.focus(); input.value?.select() }) }

function chip(f: string) { q.value = toggleFilter(q.value, f); input.value?.focus() }
function clear() { q.value = ''; input.value?.focus() }

function open(item: Flat | undefined) {
  if (!item) return
  navOpen.value = false
  if (item.kind === 'thread') router.push(resultPath({ channelId: item.hit.channelId, threadId: item.hit.rootId, postId: item.hit.match?.postId ?? item.hit.rootId }))
  else router.push(resultPath(item.hit))
}
function key(e: KeyboardEvent) {
  const n = flat.value.length
  if (e.key === 'ArrowDown' && n) { e.preventDefault(); active.value = (active.value + 1) % n; reveal() }
  else if (e.key === 'ArrowUp' && n) { e.preventDefault(); active.value = (active.value - 1 + n) % n; reveal() }
  else if (e.key === 'Enter') { e.preventDefault(); open(flat.value[active.value]) }
  else if (e.key === 'Escape' && q.value) { e.preventDefault(); clear() }
}
function reveal() { nextTick(() => list.value?.querySelector('[data-active=true]')?.scrollIntoView({ block: 'nearest' })) }
const name = (id: number) => memberById(id)?.displayName ?? 'Someone'
const isAgent = (id: number) => memberById(id)?.kind === 'agent'
</script>
<template>
  <main class="flex min-h-0 min-w-0 flex-1 flex-col" data-testid="search-view">
    <header class="flex h-[52px] shrink-0 items-center gap-2.5 border-b border-border px-3 sm:px-4">
      <Button v-if="isPhone" variant="toolbar-icon" size="toolbar-lg" aria-label="Open menu" data-testid="phone-menu" @click="navOpen = true"><Menu :size="18" /></Button>
      <h2 class="m-0 flex items-center gap-1.5 text-[15px] font-semibold whitespace-nowrap"><Search :size="16" class="text-muted" aria-hidden="true" />Search</h2>
      <span v-if="!isPhone" class="truncate border-l border-border pl-2.5 text-[13px] text-muted">Everything you can see in {{ store.serverName }}</span>
    </header>

    <div ref="list" class="thin-scrollbar min-h-0 flex-1 overflow-auto px-3 py-4 sm:px-6">
      <div class="mx-auto max-w-[760px]">
        <div class="flex items-center gap-2 rounded-card border border-accent bg-main-bg px-3 py-2 focus-within:ring-2 focus-within:ring-accent/25">
          <Search :size="16" class="shrink-0 text-muted" aria-hidden="true" />
          <input ref="input" v-model="q" type="search" class="min-w-0 flex-1 border-0 bg-transparent text-[15px] text-text outline-none placeholder:text-muted [&::-webkit-search-cancel-button]:hidden"
            placeholder="Search messages, threads and answers…" aria-label="Search" autocomplete="off" spellcheck="false" data-testid="search-input"
            role="combobox" aria-autocomplete="list" aria-controls="search-results" :aria-expanded="flat.length > 0" :aria-activedescendant="flat.length ? `search-hit-${active}` : undefined"
            @keydown="key">
          <Spinner v-if="loading" size="xs" />
          <span v-else-if="results && !results.empty" class="shrink-0 text-xs text-muted" data-testid="search-count">{{ total === 1 ? '1 result' : `${total} results` }}</span>
          <Button v-if="q" variant="toolbar-icon" size="toolbar" class="size-6" aria-label="Clear search" @click="clear"><X :size="14" /></Button>
        </div>
        <div class="mt-2.5 mb-1 flex flex-wrap gap-1.5" data-testid="search-chips">
          <button v-for="f in chips" :key="f" type="button" class="chip" :class="{ on: hasFilter(q, f) }" :aria-pressed="hasFilter(q, f)" :data-chip="f" @click="chip(f)">{{ f }}</button>
        </div>

        <ErrorNote v-if="error" :message="error" class="mt-3" />

        <EmptyState v-if="!q.trim()" :icon="Search" title="Search everything you can see" class="mt-8" data-testid="search-empty">
          Messages, threads and agents’ answers. Narrow it with <code>in:#channel</code>, <code>from:@someone</code> or <code>is:thread</code>, or press ↑ ↓ and Enter to open a result.
        </EmptyState>
        <EmptyState v-else-if="results && !loading && total === 0" :icon="Search" :title="`No results for “${q.trim()}”`" description="Try fewer words, or take a filter out." class="mt-8" data-testid="search-none" />

        <div v-if="results" id="search-results" role="listbox" aria-label="Results">
          <section v-if="results.threads.items.length" class="mt-4" data-testid="search-threads">
            <h3 class="group-h">Threads · {{ results.threads.total }}</h3>
            <button v-for="(t, i) in results.threads.items" :id="`search-hit-${i}`" :key="'t' + t.rootId" type="button" role="option" :aria-selected="active === i"
              class="hit" :data-active="active === i" data-testid="search-hit" data-kind="thread" :data-root="t.rootId" @click="open(flat[i])" @mousemove="active = i">
              <span class="flex items-start gap-2 font-semibold"><MessageSquare :size="14" class="mt-[3px] shrink-0 text-accent-text" aria-hidden="true" /><Highlighted :text="t.title" class="min-w-0" /></span>
              <span v-if="t.match" class="block text-[13px] text-muted"><b class="font-semibold text-text">{{ name(t.match.authorId) }}</b>: <Highlighted :text="t.match.snippet" class="text-text" /></span>
              <span class="meta">
                <MemberAvatar :member="memberById(t.authorId)" size="xs" />{{ name(t.authorId) }}
                <span class="flex items-center"><Hash :size="11" aria-hidden="true" />{{ t.channelName }}</span>
                <span class="font-semibold text-accent-text">{{ t.replyCount === 1 ? '1 reply' : `${t.replyCount} replies` }}</span>
                <span v-for="h in t.agents" :key="h" class="rounded-[5px] bg-accent-dim px-1.5 font-mono text-[11px] text-accent-text">@{{ h }}</span>
                <RelativeTime :at="t.at" compact class="ml-auto" />
              </span>
            </button>
            <p v-if="results.threads.total > results.threads.items.length" class="m-0 mt-1.5 text-xs text-muted">The best {{ results.threads.items.length }} of {{ results.threads.total }}: add words or a filter to narrow it.</p>
          </section>

          <section v-if="results.messages.items.length" class="mt-4" data-testid="search-messages">
            <h3 class="group-h">Messages · {{ results.messages.total }}</h3>
            <button v-for="(m, j) in results.messages.items" :id="`search-hit-${results.threads.items.length + j}`" :key="'m' + m.postId" type="button" role="option"
              :aria-selected="active === results.threads.items.length + j" class="hit" :data-active="active === results.threads.items.length + j"
              data-testid="search-hit" data-kind="message" :data-post-id="m.postId" @click="open(flat[results.threads.items.length + j])" @mousemove="active = results.threads.items.length + j">
              <Highlighted :text="m.snippet" class="block text-[13.5px] break-words" />
              <span class="meta">
                <MemberAvatar :member="memberById(m.authorId)" size="xs" />{{ name(m.authorId) }}<AgentBadge v-if="isAgent(m.authorId)" />
                <span class="flex items-center"><Hash :size="11" aria-hidden="true" />{{ m.channelName }}</span>
                <span v-if="m.threadTitle" class="max-w-full min-w-0 truncate">· thread “{{ m.threadTitle }}”</span>
                <RelativeTime :at="m.at" compact class="ml-auto" />
              </span>
            </button>
            <Button v-if="results.messages.total > results.messages.items.length" variant="outline" size="sm" class="mt-2" :disabled="more" data-testid="search-more" @click="run(results.messages.items.length)">
              <Spinner v-if="more" size="xs" />Show more ({{ results.messages.total - results.messages.items.length }})
            </Button>
          </section>
        </div>
      </div>
    </div>
  </main>
</template>
<style scoped>
.chip { font-family: var(--font-mono-stack); font-size: 11.5px; border: 1px solid var(--border); border-radius: 6px; padding: 2px 8px; color: var(--muted); background: transparent; cursor: pointer; transition: color var(--transition), border-color var(--transition), background var(--transition); }
.chip:hover { color: var(--text); border-color: color-mix(in srgb, var(--accent) 40%, var(--border)); }
.chip.on { color: var(--accent-text); border-color: color-mix(in srgb, var(--accent) 50%, var(--border)); background: var(--accent-dim); }
.group-h { margin: 0 0 6px; font-size: 11px; color: var(--muted); text-transform: uppercase; letter-spacing: .05em; font-weight: 600; }
.hit { display: grid; gap: 5px; width: 100%; min-width: 0; overflow: hidden; text-align: left; color: var(--text); border: 1px solid var(--border); background: var(--card-bg); border-radius: var(--radius-card); padding: 10px 12px; cursor: pointer; font-size: 13.5px; transition: border-color var(--transition); }
.hit + .hit { margin-top: 8px; }
.hit[data-active=true] { border-color: var(--accent); box-shadow: 0 0 0 1px var(--accent); }
.meta { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; font-size: 12px; color: var(--muted); }
</style>
