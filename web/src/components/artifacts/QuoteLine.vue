<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { CornerDownRight } from 'lucide-vue-next'
import type { Post } from '@/types'
import { canvas, openInCanvas } from '@/artifacts'
import { threadPath } from '@/threads'

// A reply written under the canvas quotes the artifact and the version its author was looking at; clicking it shows that version.
const props = defineProps<{ post: Post }>()
const router = useRouter()
const q = computed(() => props.post.meta?.quote ?? {})
function open() {
  const root = props.post.threadId
  if (root == null) return
  if (canvas.host === root) openInCanvas(root, Number(q.value.artifactId), Number(q.value.version))
  else router.push({ path: threadPath(props.post.channelId, root), query: { a: String(q.value.artifactId), v: String(q.value.version) } })
}
</script>
<template>
  <button type="button" class="mt-0.5 mb-0.5 flex max-w-full cursor-pointer items-center gap-1 rounded-[6px] border-0 bg-transparent p-0 text-left text-[12.5px] text-muted hover:text-accent-text" data-testid="post-quote" @click="open">
    <CornerDownRight :size="13" class="shrink-0" aria-hidden="true" />
    <span class="min-w-0 truncate">About <b class="font-semibold text-text">{{ q.title }}</b> · v{{ q.version }}</span>
  </button>
</template>
