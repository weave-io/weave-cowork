<script setup lang="ts">
import { computed, nextTick, watch, ref } from 'vue'
import { store, memberById } from '../store'
import { post } from '../api'
import { X, Hash, GitBranch } from 'lucide-vue-next'
import { Button } from '@/components/ui/button'
import StatusDot from './StatusDot.vue'
import PostItem from './PostItem.vue'
import Composer from './Composer.vue'

const emit = defineEmits<{ close: [] }>()
const t = computed(() => store.feed.thread)
const scroller = ref<HTMLElement | null>(null)
const startedBy = computed(() => memberById(t.value?.startedBy ?? 0))
const channelName = computed(() => store.channels.find(c => c.id === store.feed.channelId)?.name)

watch(() => [t.value?.replies.length, t.value?.replies.at(-1)?.body.length], async () => {
  await nextTick()
  const s = scroller.value
  if (s && s.scrollHeight - s.scrollTop - s.clientHeight < 160) s.scrollTop = s.scrollHeight
}, { flush: 'post' })
watch(() => t.value?.rootId, async () => { await nextTick(); if (scroller.value) scroller.value.scrollTop = scroller.value.scrollHeight })

/** "/home/alex/.bin/opencode acp" → "opencode acp": the executable's name, then its arguments. */
function shortCommand(cmd: string[]) {
  const [exe = '', ...rest] = cmd
  return [exe.split(/[\\/]/).pop(), ...rest].join(' ')
}
const dot = (a: { id: number; online: boolean; busy: boolean; queued: number }) => !isOnline(a) ? (a.queued > 0 ? 'waiting' : 'offline') : a.busy ? 'working' : 'online'
const isOnline = (a: { id: number; online: boolean }) => memberById(a.id)?.online ?? a.online
function status(a: { id: number; online: boolean; busy: boolean; queued: number }) {
  if (!isOnline(a)) return a.queued > 0 ? 'waiting for the runner' : 'offline'
  if (a.busy) return 'working…'
  return a.queued > 0 ? 'queued' : 'idle'
}
async function send(text: string) {
  if (!t.value) return
  await post(`/channels/${store.feed.channelId}/posts`, { body: text, threadId: t.value.rootId })
}
</script>
<template>
  <aside v-if="t" class="flex min-h-0 min-w-0 flex-col bg-panel-bg" aria-label="Thread" data-testid="thread-panel">
    <div class="border-b border-border px-3.5 py-2.5">
      <div class="flex items-center gap-2">
        <h2 class="m-0 text-[15px] font-semibold">Thread</h2>
        <span class="flex items-center gap-0.5 text-[13px] text-muted">in <Hash :size="12" aria-hidden="true" class="ml-0.5" />{{ channelName }}</span>
        <Button variant="toolbar-icon" size="toolbar" class="ml-auto" aria-label="Close thread" @click="emit('close')"><X :size="16" /></Button>
      </div>
      <div v-for="a in t.agents" :key="a.id" class="mt-1.5 flex flex-wrap items-center gap-1.5 text-xs text-muted" data-testid="session-line">
        <span class="rounded-[5px] border border-border px-1.5 font-mono text-[11.5px]">session</span>
        <b class="text-text">@{{ a.handle }}</b>
        <span class="font-mono text-[11.5px]" :title="a.harnessCommand.join(' ')" data-testid="session-command">{{ shortCommand(a.harnessCommand) }}</span>
        <span v-if="a.workspace?.branch" class="flex items-center gap-1" data-testid="session-branch">· <GitBranch :size="12" aria-hidden="true" /><code class="font-mono text-[11.5px]">{{ a.workspace.branch }}</code></span>
        <span v-if="a.workspace?.host ?? a.runnerHost" :class="!(a.workspace?.sandboxed) && 'text-idle-text'" data-testid="session-where">· {{ a.workspace?.sandboxed ? 'sandboxed' : 'unsandboxed' }} on {{ a.workspace?.host ?? a.runnerHost }}</span>
        <span v-if="startedBy">· started by {{ startedBy.displayName }}</span>
        <span class="ml-auto flex items-center gap-1.5" data-testid="agent-status">
          <StatusDot :state="dot(a)" />
          {{ status(a) }}
        </span>
      </div>
    </div>
    <div ref="scroller" class="thin-scrollbar flex-1 overflow-auto px-1.5 py-2.5">
      <PostItem :post="t.root" in-thread />
      <div class="mx-2.5 my-2 flex items-center gap-2.5 text-xs text-muted"><span class="h-px flex-1 bg-border" />{{ t.replies.filter(r => r.kind === 'text').length }} {{ t.replies.filter(r => r.kind === 'text').length === 1 ? 'reply' : 'replies' }}<span class="h-px flex-1 bg-border" /></div>
      <PostItem v-for="r in t.replies" :key="r.id" :post="r" in-thread />
      <template v-for="a in t.agents" :key="'w' + a.id">
        <div v-if="!isOnline(a) && a.queued > 0" class="mx-2.5 my-1.5 rounded-card border border-dashed border-border px-2.5 py-1.5 text-[12.5px] text-muted" data-testid="waiting-runner">
          Waiting for {{ a.displayName }}’s runner. It'll answer when the runner is back.
        </div>
      </template>
    </div>
    <Composer placeholder="Reply in thread… use @ to mention an agent" @send="send" />
  </aside>
</template>
