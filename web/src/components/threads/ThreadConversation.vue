<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import { memberById } from '../../store'
import type { ThreadState, ThreadAgent } from '../../types'
import PostItem from '../PostItem.vue'

// A thread's conversation: the opening post, every reply, tool rows, asks, cards and the outcome. It follows the end while the reader is
// there: whether they were is measured before the new rows grow it (a sandbox's card is tall).
const props = defineProps<{ thread: ThreadState; wide?: boolean }>()
const scroller = ref<HTMLElement | null>(null)
defineExpose({ scroller })

let atEnd = true
const last = () => [props.thread.replies.length, props.thread.replies.at(-1)?.body.length, props.thread.replies.at(-1)?.meta?.state]
watch(last, () => {
  const s = scroller.value
  atEnd = !s || s.scrollHeight - s.scrollTop - s.clientHeight < 160
}, { flush: 'pre' })
watch(last, async () => {
  await nextTick()
  const s = scroller.value
  if (s && atEnd) s.scrollTop = s.scrollHeight
}, { flush: 'post' })
watch(() => props.thread.rootId, async () => { await nextTick(); if (scroller.value) scroller.value.scrollTop = scroller.value.scrollHeight }, { immediate: true })

const isOnline = (a: ThreadAgent) => a.placement === 'pool' ? a.online : memberById(a.id)?.online ?? a.online
const replies = () => props.thread.replies.filter(r => r.kind === 'text').length
</script>
<template>
  <div ref="scroller" class="thin-scrollbar min-h-0 flex-1 overflow-auto px-1.5 py-2.5">
    <div :class="wide && 'mx-auto max-w-[920px]'">
      <PostItem :post="thread.root" in-thread />
      <div class="mx-2.5 my-2 flex items-center gap-2.5 text-xs text-muted"><span class="h-px flex-1 bg-border" />{{ replies() }} {{ replies() === 1 ? 'reply' : 'replies' }}<span class="h-px flex-1 bg-border" /></div>
      <PostItem v-for="r in thread.replies" :key="r.id" :post="r" in-thread />
      <template v-for="a in thread.agents" :key="'w' + a.id">
        <div v-if="a.placement !== 'pool' && !isOnline(a) && a.queued > 0" class="mx-2.5 my-1.5 rounded-card border border-dashed border-border px-2.5 py-1.5 text-[12.5px] text-muted" data-testid="waiting-runner">
          Waiting for {{ a.displayName }}’s runner. It'll answer when the runner is back.
        </div>
      </template>
    </div>
  </div>
</template>
