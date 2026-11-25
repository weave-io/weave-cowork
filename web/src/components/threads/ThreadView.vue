<script setup lang="ts">
import { computed, onBeforeUnmount, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { Hash, Lock, Menu, Layers } from 'lucide-vue-next'
import { store } from '../../store'
import { post } from '../../api'
import { isPhone, navOpen } from '../../layout'
import { canvas, canvasOpenFor, openInCanvas, threadArtifacts } from '../../artifacts'
import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'
import Composer from '../Composer.vue'
import ThreadStatusPill from '../ThreadStatusPill.vue'
import SessionLines from './SessionLines.vue'
import ThreadConversation from './ThreadConversation.vue'
import ThreadTitle from './ThreadTitle.vue'
import ArtifactCanvas from '../artifacts/ArtifactCanvas.vue'

// A thread as a full conversation in the main area (its URL is its own, so it can be shared and reloaded): its title as the heading (edited in
// place), its status, the channel it hangs off (a link back to the post), the session line, every reply, tool row, ask and the outcome, and its
// own composer. Brief 9: the canvas sits beside it (mockup A), with the thread's artifacts in tabs; ?a=<id>&v=<n> opens one, so a link can be
// copied and a reload keeps it open. Full screen gives the canvas the whole area; on a phone the canvas fills the screen.
const route = useRoute(), router = useRouter()
const t = computed(() => store.feed.thread)
const channel = computed(() => store.channels.find(c => c.id === store.feed.channelId) ?? null)
const toPost = () => { if (channel.value && t.value) router.push({ path: `/c/${channel.value.id}`, query: { post: String(t.value.rootId) } }) }
async function send(text: string) {
  if (!t.value) return
  await post(`/channels/${store.feed.channelId}/posts`, { body: text, threadId: t.value.rootId })
}

// this thread's full view is on screen: the canvas may open here by itself (for whoever started it)
watch(() => t.value?.rootId ?? null, id => { canvas.host = id }, { immediate: true })
onBeforeUnmount(() => { canvas.host = null })

// the URL says which artifact is open (a copied link, a reload); the canvas keeps the URL in step
watch(() => [t.value?.rootId, route.query.a, route.query.v] as const, ([root, a, v]) => {
  if (root == null || typeof a !== 'string' || !Number(a)) return
  const tab = canvas.threadId === root ? canvas.tabs.find(x => x.id === Number(a)) : undefined
  const version = typeof v === 'string' && Number(v) ? Number(v) : null
  if (!tab || tab.version !== version || canvas.active !== Number(a)) openInCanvas(root, Number(a), version)
}, { immediate: true })
const open = computed(() => canvasOpenFor(t.value?.rootId))
watch(() => [open.value, canvas.active, canvas.tabs.find(x => x.id === canvas.active)?.version ?? null] as const, ([isOpen, a, v]) => {
  if (!t.value) return
  const want: Record<string, string> = isOpen && a != null ? { a: String(a), ...(v != null ? { v: String(v) } : {}) } : {}
  const { a: _a, v: _v, ...rest } = route.query
  if (String(route.query.a ?? '') !== String(want.a ?? '') || String(route.query.v ?? '') !== String(want.v ?? ''))
    router.replace({ query: { ...rest, ...want } })
})

const shown = computed(() => t.value ? threadArtifacts(t.value.replies) : [])
// "2 artifacts": every artifact the thread shows, in tabs, the first one in front
function showAll() {
  const root = t.value?.rootId
  if (root == null || !shown.value.length) return
  for (const id of shown.value) if (!(canvas.threadId === root && canvas.tabs.some(x => x.id === id))) openInCanvas(root, id)
  canvas.active = shown.value[0]
}
</script>
<template>
  <div class="flex min-h-0 min-w-0 flex-1" data-testid="thread-view-wrap">
    <main v-show="!(open && (canvas.full || isPhone))" class="flex min-h-0 min-w-0 flex-1 flex-col" data-testid="thread-view">
      <template v-if="t">
        <header class="shrink-0 border-b border-border px-3 py-2.5 sm:px-4">
          <div class="flex min-h-9 items-center gap-2">
            <Button v-if="isPhone" variant="toolbar-icon" size="toolbar-lg" aria-label="Open menu" data-testid="phone-menu" @click="navOpen = true"><Menu :size="18" /></Button>
            <ThreadTitle :thread="t" size="lg" />
            <ThreadStatusPill :status="t.root.threadStatus" class="shrink-0" />
            <button v-if="shown.length" type="button" class="flex shrink-0 cursor-pointer items-center gap-1 rounded-full border-0 bg-accent-dim px-2 py-px text-[12px] font-medium text-accent-text hover:brightness-110" data-testid="thread-artifacts" @click="showAll">
              <Layers :size="12" aria-hidden="true" />{{ shown.length === 1 ? '1 artifact' : `${shown.length} artifacts` }}</button>
            <button v-if="channel && !isPhone" type="button" class="ml-2 flex shrink-0 cursor-pointer items-center gap-0.5 border-0 bg-transparent p-0 text-[13px] text-muted hover:text-accent-text hover:underline" data-testid="thread-channel-link" @click="toPost">
              in <Lock v-if="channel.isPrivate" :size="12" class="ml-0.5" aria-hidden="true" /><Hash v-else :size="12" class="ml-0.5" aria-hidden="true" />{{ channel.name }}
            </button>
          </div>
          <button v-if="channel && isPhone" type="button" class="mt-0.5 flex cursor-pointer items-center gap-0.5 border-0 bg-transparent p-0 text-[12.5px] text-muted" data-testid="thread-channel-link" @click="toPost">
            in <Hash :size="11" class="ml-0.5" aria-hidden="true" />{{ channel.name }}
          </button>
          <SessionLines :agents="t.agents" :started-by="t.startedBy" />
        </header>
        <ThreadConversation :thread="t" :wide="!open" />
        <div :class="!isPhone && !open && 'mx-auto w-full max-w-[944px]'"><Composer placeholder="Reply in thread… use @ to mention an agent" @send="send" /></div>
      </template>
      <div v-else class="grid flex-1 place-items-center"><Spinner /></div>
    </main>
    <ArtifactCanvas v-if="open && t && channel" :channel-id="channel.id" :thread-id="t.rootId"
      class="border-border" :class="isPhone ? 'fixed inset-0 z-40 w-full' : canvas.full ? 'min-w-0 flex-1' : 'w-[48%] max-w-[860px] min-w-[420px] shrink-0 border-l'" />
  </div>
</template>
