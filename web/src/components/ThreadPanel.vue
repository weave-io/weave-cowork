<script setup lang="ts">
import { computed, nextTick, watch, ref } from 'vue'
import { store, memberById } from '../store'
import { post } from '../api'
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

function status(a: { online: boolean; busy: boolean; queued: number }) {
  if (!a.online) return a.queued > 0 ? 'waiting for the runner' : 'offline'
  if (a.busy) return 'working…'
  return a.queued > 0 ? 'queued' : 'idle'
}
async function send(text: string) {
  if (!t.value) return
  await post(`/channels/${store.feed.channelId}/posts`, { body: text, threadId: t.value.rootId })
}
</script>
<template>
  <aside v-if="t" class="bg-side border-l border-line flex flex-col min-w-0 min-h-0" aria-label="Thread" data-testid="thread-panel">
    <div class="px-3.5 py-2.5 border-b border-line">
      <div class="flex items-center gap-2">
        <h2 class="m-0 text-[15px] font-semibold">Thread</h2>
        <span class="text-muted text-[13px]">in #{{ channelName }}</span>
        <button class="ml-auto w-7 h-7 grid place-items-center rounded-btn text-muted hover:bg-hover cursor-pointer border-0 bg-transparent" aria-label="Close thread" @click="emit('close')">✕</button>
      </div>
      <div v-for="a in t.agents" :key="a.id" class="mt-1.5 flex flex-wrap items-center gap-1.5 text-xs text-muted" data-testid="session-line">
        <span class="font-mono text-[11.5px] border border-line rounded-full px-2 bg-panel">session</span>
        <b class="text-ink">@{{ a.handle }}</b>
        <span class="font-mono text-[11.5px]">{{ a.harnessCommand.join(' ') }}</span>
        <span v-if="a.runnerHost">· on {{ a.runnerHost }}</span>
        <span v-if="startedBy">· started by {{ startedBy.displayName }}</span>
        <span class="ml-auto flex items-center gap-1.5" data-testid="agent-status">
          <i class="w-[7px] h-[7px] rounded-full inline-block" :class="[a.online ? 'bg-online' : 'bg-offline', a.busy && 'pulse']" />
          {{ status(a) }}
        </span>
      </div>
    </div>
    <div ref="scroller" class="flex-1 overflow-auto px-2 py-2.5">
      <PostItem :post="t.root" in-thread />
      <div class="flex items-center gap-2.5 text-faint text-xs mx-2.5 my-2"><span class="flex-1 h-px bg-line" />{{ t.replies.filter(r => r.kind === 'text').length }} {{ t.replies.filter(r => r.kind === 'text').length === 1 ? 'reply' : 'replies' }}<span class="flex-1 h-px bg-line" /></div>
      <PostItem v-for="r in t.replies" :key="r.id" :post="r" in-thread />
      <template v-for="a in t.agents" :key="'w' + a.id">
        <div v-if="!a.online && a.queued > 0" class="mx-2.5 my-1.5 border border-dashed border-line rounded-btn px-2.5 py-1.5 text-[12.5px] text-muted" data-testid="waiting-runner">
          Waiting for {{ a.displayName }}’s runner. It'll answer when the runner is back.
        </div>
      </template>
    </div>
    <Composer placeholder="Reply in thread… use @ to mention an agent" @send="send" />
  </aside>
</template>
