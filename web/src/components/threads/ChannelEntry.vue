<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { ChevronDown, ChevronRight, Hash, Lock } from 'lucide-vue-next'
import { store, memberById, foldThreads } from '../../store'
import { navOpen } from '../../layout'
import { threadPath, threadsPath, threadState } from '../../threads'
import type { Channel, ThreadSummary } from '../../types'
import MemberAvatar from '../MemberAvatar.vue'
import ThreadDot from './ThreadDot.vue'

// A text channel in the list, with its threads under it (brief 8): the most recently active first, each with its title, the agent in it and
// its status as a dot (and in words on hover), unread replies in bold with a count. At most five, then "All threads". The chevron folds
// them away, remembered per channel.
const props = defineProps<{ channel: Channel; active: boolean; activeThread: number | null }>()
const emit = defineEmits<{ go: [id: number] }>()
const router = useRouter()
const entry = computed(() => store.threadList[props.channel.id])
const threads = computed(() => entry.value?.threads ?? [])
const folded = computed(() => store.folded.includes(props.channel.id))
const hidden = computed(() => (entry.value?.total ?? 0) - threads.value.length)
const unread = computed(() => store.unread.channels[String(props.channel.id)] ?? 0)
const who = (t: ThreadSummary) => memberById(t.agents[0]?.id ?? t.startedBy)
const go = (path: string) => { navOpen.value = false; router.push(path) }
const hover = (t: ThreadSummary) => `${t.title} · ${threadState(t.status, store.meId).label}${t.agents.length ? ` · ${t.agents.map(a => '@' + a.handle).join(', ')}` : ''}`
</script>
<template>
  <div class="relative">
    <button type="button" class="chan" :class="{ active: active && activeThread == null, unread: unread > 0, 'has-fold': entry?.total }" :data-channel="channel.name" :data-kind="channel.kind"
      :aria-current="active ? 'true' : undefined" @click="emit('go', channel.id)">
      <Lock v-if="channel.isPrivate" :size="14" class="shrink-0" aria-label="Private" /><Hash v-else :size="14" class="shrink-0" aria-hidden="true" />
      <span class="truncate">{{ channel.name }}</span>
      <span v-if="unread > 0" class="count" data-testid="unread-badge" :aria-label="`${unread} unread`">{{ unread > 99 ? '99+' : unread }}</span>
    </button>
    <button v-if="entry?.total" type="button" class="fold" :aria-expanded="!folded" :aria-label="`${folded ? 'Show' : 'Hide'} threads in ${channel.name}`" :title="folded ? 'Show threads' : 'Hide threads'"
      :data-fold="channel.name" @click="foldThreads(channel.id)">
      <ChevronRight v-if="folded" :size="14" aria-hidden="true" /><ChevronDown v-else :size="14" aria-hidden="true" />
    </button>
  </div>
  <ul v-if="entry?.total && !folded" class="m-0 mb-1 list-none p-0" :data-threads="channel.name">
    <li v-for="t in threads" :key="t.rootId">
      <button type="button" class="thr" :class="{ active: activeThread === t.rootId, unread: t.unread > 0 }" :title="hover(t)" :data-thread="t.rootId" :data-thread-title="t.title"
        :aria-current="activeThread === t.rootId ? 'page' : undefined" data-testid="thread-row" @click="go(threadPath(channel.id, t.rootId))">
        <MemberAvatar :member="who(t)" size="xs" class="scale-[.8]" />
        <span class="min-w-0 flex-1 truncate" data-testid="thread-row-title">{{ t.title }}</span>
        <span v-if="t.unread > 0" class="tcount" data-testid="thread-unread" :aria-label="`${t.unread} unread`">{{ t.unread > 99 ? '99+' : t.unread }}</span>
        <ThreadDot :status="t.status" />
      </button>
    </li>
    <li>
      <button type="button" class="thr more" data-testid="all-threads" @click="go(threadsPath(channel.id))">
        All threads<span v-if="hidden > 0" class="text-muted"> · {{ hidden }} more</span>
      </button>
    </li>
  </ul>
</template>
<style scoped>
.chan { width: 100%; min-width: 0; min-height: 32px; display: flex; align-items: center; gap: 9px; padding: 0 10px; margin: 1px 0; border: 0; border-radius: var(--radius-btn); background: transparent; color: color-mix(in srgb, var(--text) 86%, transparent); text-align: left; cursor: pointer; font-size: 13.5px; transition: background var(--transition), color var(--transition); }
.chan:hover { background: color-mix(in srgb, var(--text) 5%, transparent); color: var(--text); }
.chan.active { background: var(--accent-dim); color: var(--text); font-weight: 600; }
.chan svg { color: var(--muted); }
.chan.unread { color: var(--text); font-weight: 600; }
.chan.active svg { color: var(--accent-text); }
.chan.has-fold { padding-right: 32px; }
.count { margin-left: auto; min-width: 18px; height: 18px; padding: 0 6px; border-radius: 9px; background: var(--accent); color: var(--accent-foreground, #fff); font-size: 11px; font-weight: 600; line-height: 18px; text-align: center; }
.fold { position: absolute; right: 4px; top: 50%; transform: translateY(-50%); display: grid; place-items: center; width: 24px; height: 24px; border: 0; border-radius: 6px; background: transparent; color: var(--muted); cursor: pointer; }
.fold:hover { background: color-mix(in srgb, var(--text) 7%, transparent); color: var(--text); }
.thr { width: 100%; min-width: 0; min-height: 28px; display: flex; align-items: center; gap: 7px; padding: 0 10px 0 22px; margin: 0; border: 0; border-radius: var(--radius-btn); background: transparent; color: var(--muted); text-align: left; cursor: pointer; font-size: 12.5px; transition: background var(--transition), color var(--transition); }
.thr:hover { background: color-mix(in srgb, var(--text) 5%, transparent); color: var(--text); }
.thr.active { background: var(--accent-dim); color: var(--text); font-weight: 600; }
.thr.unread { color: var(--text); font-weight: 600; }
.thr.more { padding-left: 49px; font-size: 12px; color: var(--accent-text); }
.tcount { min-width: 16px; height: 16px; padding: 0 5px; border-radius: 8px; background: var(--accent); color: var(--accent-foreground, #fff); font-size: 10.5px; font-weight: 600; line-height: 16px; text-align: center; }
@media (max-width: 767px) { .thr { min-height: 34px; } .chan { min-height: 38px; } }
</style>
