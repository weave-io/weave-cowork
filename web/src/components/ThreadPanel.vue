<script setup lang="ts">
import { computed, onBeforeUnmount, watch } from 'vue'
import { useRouter } from 'vue-router'
import { store } from '../store'
import { post } from '../api'
import { X, Hash, Maximize2 } from 'lucide-vue-next'
import { Button } from '@/components/ui/button'
import Composer from './Composer.vue'
import ThreadStatusPill from './ThreadStatusPill.vue'
import SessionLines from './threads/SessionLines.vue'
import ThreadConversation from './threads/ThreadConversation.vue'
import { threadPath } from '../threads'
import ThreadTitle from './threads/ThreadTitle.vue'
import { canvas } from '../artifacts'

// A thread's quick look beside the channel (from a post's "N replies"); "Open full view" gives it the main area.
const emit = defineEmits<{ close: [] }>()
const router = useRouter()
const t = computed(() => store.feed.thread)
const channelName = computed(() => store.channels.find(c => c.id === store.feed.channelId)?.name)
const full = () => { if (t.value && store.feed.channelId != null) router.push(threadPath(store.feed.channelId, t.value.rootId)) }
// the canvas opens by itself for whoever started the thread: from the quick look, that means the full view, with the artifact in the canvas
watch(() => t.value?.rootId ?? null, id => { canvas.panel = id }, { immediate: true })
onBeforeUnmount(() => { canvas.panel = null })
watch(() => canvas.request, req => {
  if (!req || store.feed.channelId == null || req.threadId !== t.value?.rootId) return
  canvas.request = null
  router.push({ path: threadPath(store.feed.channelId, req.threadId), query: { a: String(req.id) } })
})
async function send(text: string) {
  if (!t.value) return
  await post(`/channels/${store.feed.channelId}/posts`, { body: text, threadId: t.value.rootId })
}
</script>
<template>
  <aside v-if="t" class="flex min-h-0 min-w-0 flex-col bg-panel-bg" aria-label="Thread" data-testid="thread-panel">
    <div class="border-b border-border px-3.5 py-2.5">
      <div class="flex items-center gap-2">
        <ThreadTitle :thread="t" />
        <ThreadStatusPill :status="t.root.threadStatus" class="shrink-0" />
        <span class="flex shrink-0 items-center gap-0.5">
          <Button variant="toolbar-icon" size="toolbar" aria-label="Open full view" title="Open full view" data-testid="open-full-view" @click="full"><Maximize2 :size="14" /></Button>
          <Button variant="toolbar-icon" size="toolbar" aria-label="Close thread" @click="emit('close')"><X :size="16" /></Button>
        </span>
      </div>
      <div class="mt-0.5 flex items-center gap-0.5 text-[12.5px] text-muted">Thread in <Hash :size="12" aria-hidden="true" class="ml-0.5" />{{ channelName }}</div>
      <SessionLines :agents="t.agents" :started-by="t.startedBy" />
    </div>
    <ThreadConversation :thread="t" />
    <Composer placeholder="Reply in thread… use @ to mention an agent" @send="send" />
  </aside>
</template>
