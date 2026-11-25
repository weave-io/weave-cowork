<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { Hash, Lock, Menu, MessagesSquare } from 'lucide-vue-next'
import { get, ApiError } from '../../api'
import { store, memberById } from '../../store'
import { isPhone, navOpen } from '../../layout'
import { filters, threadPath, type ThreadFilter } from '../../threads'
import type { ThreadSummary } from '../../types'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorNote } from '@/components/ui/error-note'
import { Spinner } from '@/components/ui/spinner'
import { RelativeTime } from '@/components/ui/relative-time'
import MemberAvatar from '../MemberAvatar.vue'
import ThreadStatusPill from '../ThreadStatusPill.vue'
import ThreadDot from './ThreadDot.vue'

// All threads in a channel: every thread, most recent activity first, with filters (open, waiting for me, done). Each shows its title, the
// agents in it, who started it, its status and when something last happened. It follows the threads live.
const props = defineProps<{ channelId: number }>()
const router = useRouter()
const channel = computed(() => store.channels.find(c => c.id === props.channelId) ?? null)
const filter = ref<ThreadFilter>('all')
const threads = ref<ThreadSummary[] | null>(null), loading = ref(false), error = ref('')
let seq = 0
async function load() {
  const mine = ++seq
  loading.value = threads.value == null; error.value = ''
  try {
    const list: ThreadSummary[] = await get(`/channels/${props.channelId}/threads${filter.value === 'all' ? '' : `?filter=${filter.value}`}`)
    if (mine === seq) threads.value = list
  } catch (e) { if (mine === seq) error.value = e instanceof ApiError ? e.message : 'The threads didn’t load. Try again.' }
  finally { if (mine === seq) loading.value = false }
}
watch(() => [props.channelId, filter.value], () => { threads.value = null; load() }, { immediate: true })
let timer: ReturnType<typeof setTimeout> | null = null
watch(() => store.threadsVersion, () => { if (timer) return; timer = setTimeout(() => { timer = null; load() }, 200) })

const name = (id: number) => memberById(id)?.displayName ?? 'Someone'
const empty = computed(() => ({
  all: ['No threads here yet', 'Mention an agent in a post, or reply to one, to start a thread.'],
  open: ['Nothing open', 'Every thread here is done.'],
  waiting: ['Nothing is waiting for you', 'When an agent asks you something, its thread shows here.'],
  done: ['Nothing done yet', 'A thread is done when its agent resolves it with an outcome.'],
}[filter.value]))
const open = (t: ThreadSummary) => { navOpen.value = false; router.push(threadPath(t.channelId, t.rootId)) }
</script>
<template>
  <main class="flex min-h-0 min-w-0 flex-1 flex-col" data-testid="threads-view">
    <header class="flex h-[52px] shrink-0 items-center gap-2.5 border-b border-border px-3 sm:px-4">
      <Button v-if="isPhone" variant="toolbar-icon" size="toolbar-lg" aria-label="Open menu" data-testid="phone-menu" @click="navOpen = true"><Menu :size="18" /></Button>
      <h2 class="m-0 flex min-w-0 items-center gap-1.5 text-[15px] font-semibold whitespace-nowrap">
        <MessagesSquare :size="16" class="shrink-0 text-muted" aria-hidden="true" />Threads
        <button v-if="channel" type="button" class="flex min-w-0 cursor-pointer items-center gap-0.5 border-0 bg-transparent p-0 text-[13px] font-normal text-muted hover:text-accent-text hover:underline" data-testid="threads-channel-link" @click="router.push(`/c/${channel.id}`)">
          in <Lock v-if="channel.isPrivate" :size="12" class="ml-0.5" aria-hidden="true" /><Hash v-else :size="12" class="ml-0.5" aria-hidden="true" /><span class="truncate">{{ channel.name }}</span>
        </button>
      </h2>
    </header>
    <div class="flex shrink-0 flex-wrap gap-1 border-b border-border px-3 py-2 sm:px-4" role="tablist" aria-label="Show" data-testid="thread-filters">
      <Button v-for="f in filters" :key="f.id" variant="filter" size="sm" class="h-7 px-2.5 text-[12.5px]" role="tab" :aria-selected="filter === f.id" :data-active="filter === f.id" :data-filter="f.id" @click="filter = f.id">{{ f.label }}</Button>
    </div>
    <div class="thin-scrollbar min-h-0 flex-1 overflow-auto px-3 py-3 sm:px-4">
      <ErrorNote v-if="error" :message="error" />
      <div v-if="loading" class="grid place-items-center py-10"><Spinner /></div>
      <EmptyState v-else-if="threads && !threads.length" :icon="MessagesSquare" :title="empty[0]" :description="empty[1]" class="mt-8" data-testid="threads-empty" />
      <ul v-else-if="threads" class="m-0 grid list-none gap-2 p-0" data-testid="threads-list">
        <li v-for="t in threads" :key="t.rootId">
          <button type="button" class="row" :class="{ unread: t.unread > 0 }" :data-thread="t.rootId" data-testid="threads-row" @click="open(t)">
            <span class="flex min-w-0 items-center gap-2">
              <ThreadDot :status="t.status" />
              <span class="min-w-0 truncate text-[14px]" :class="t.unread > 0 ? 'font-semibold' : 'font-medium'" data-testid="threads-row-title">{{ t.title }}</span>
              <span v-if="t.unread > 0" class="count" :aria-label="`${t.unread} unread`">{{ t.unread }}</span>
              <ThreadStatusPill :status="t.status" class="ml-auto shrink-0" />
            </span>
            <span class="meta">
              <span v-for="a in t.agents" :key="a.id" class="flex items-center gap-1"><MemberAvatar :member="memberById(a.id)" size="xs" /><span class="font-mono text-[11.5px] text-accent-text">@{{ a.handle }}</span></span>
              <span>started by {{ name(t.startedBy) }}</span>
              <span>{{ t.replies === 1 ? '1 reply' : `${t.replies} replies` }}</span>
              <span class="ml-auto">last activity <RelativeTime :at="t.lastAt" /></span>
            </span>
          </button>
        </li>
      </ul>
    </div>
  </main>
</template>
<style scoped>
.row { display: grid; gap: 4px; width: 100%; min-width: 0; text-align: left; color: var(--text); border: 1px solid var(--border); background: var(--card-bg); border-radius: var(--radius-card); padding: 9px 12px; cursor: pointer; transition: border-color var(--transition); }
.row:hover { border-color: var(--accent); }
.meta { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 10px; font-size: 12px; color: var(--muted); padding-left: 16px; }
.count { min-width: 18px; height: 18px; padding: 0 6px; border-radius: 9px; background: var(--accent); color: var(--accent-foreground, #fff); font-size: 11px; font-weight: 600; line-height: 18px; text-align: center; }
</style>
