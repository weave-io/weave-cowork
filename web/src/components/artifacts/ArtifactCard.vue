<script setup lang="ts">
import { computed, onMounted, watch } from 'vue'
import { useRouter } from 'vue-router'
import { FileText, Table2, Braces, File, Image as ImageIcon, FileType } from 'lucide-vue-next'
import type { Component } from 'vue'
import type { Post } from '@/types'
import { artifactById, canvas, formatSize, kindWords, loadArtifact, openInCanvas, withScheme } from '@/artifacts'
import { scheme } from '@/theme'
import { threadPath } from '@/threads'
import { Button } from '@/components/ui/button'
import { KindChip } from '@/components/ui/kind-chip'

// An artifact's card in the thread (mockup A): a thumbnail, its title, kind, version, size and when, and Open. Every card of an artifact shows its
// latest version. In the full view, Open shows it in the canvas beside the thread; from the quick look it opens the full view with the canvas.
const props = defineProps<{ post: Post }>()
const router = useRouter()
const m = computed(() => props.post.meta ?? {})
const latest = computed(() => m.value.latest ?? {})
const id = computed(() => Number(m.value.artifactId))
const a = computed(() => artifactById(id.value))
const thumb = computed(() => a.value?.versions.find(v => v.version === latest.value.version) ?? null)
const redacted = computed(() => (latest.value.redacted ?? []).includes(latest.value.version))
const on = computed(() => canvas.threadId === props.post.threadId && canvas.active === id.value && canvas.tabs.length > 0)
const when = computed(() => latest.value.at ? new Date(latest.value.at).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' }) : '')
const icons: Record<string, Component> = { markdown: FileText, csv: Table2, json: Braces, image: ImageIcon, svg: ImageIcon, pdf: FileType }
const icon = computed(() => icons[latest.value.kind] ?? File)
onMounted(() => { void loadArtifact(id.value) })
watch(() => latest.value.version, () => { void loadArtifact(id.value) })

function open() {
  const root = props.post.threadId!
  if (canvas.host === root) openInCanvas(root, id.value)
  else router.push({ path: threadPath(props.post.channelId, root), query: { a: String(id.value) } })
}
</script>
<template>
  <div class="my-1.5 ml-[44px] grid max-w-[560px] cursor-pointer grid-cols-[74px_minmax(0,1fr)_auto] items-center gap-3 rounded-card border bg-card-bg px-2.5 py-2 transition-colors"
    :class="on ? 'border-accent shadow-[0_0_0_1px_var(--accent)]' : 'border-border hover:border-[color-mix(in_srgb,var(--accent)_55%,var(--border))]'"
    role="button" tabindex="0" :aria-label="`Open ${post.body}`" data-testid="artifact-card" :data-artifact="id" :data-version="latest.version"
    @click="open" @keydown.enter.prevent="open" @keydown.space.prevent="open">
    <div class="relative h-[52px] w-[74px] overflow-hidden rounded-[6px] border border-border bg-white" aria-hidden="true">
      <iframe v-if="thumb?.url && latest.kind === 'html' && !redacted" :src="withScheme(thumb.url, scheme)" sandbox="allow-scripts" referrerpolicy="no-referrer" loading="lazy" tabindex="-1" title=""
        class="pointer-events-none absolute top-0 left-0 h-[520px] w-[740px] origin-top-left scale-[0.1] border-0" data-testid="artifact-thumb" />
      <img v-else-if="thumb?.url && (latest.kind === 'image' || latest.kind === 'svg') && !redacted" :src="thumb.url" alt="" class="h-full w-full object-cover" referrerpolicy="no-referrer" />
      <div v-else class="grid h-full w-full place-items-center bg-[color-mix(in_srgb,var(--text)_6%,var(--card-bg))] text-muted"><component :is="icon" :size="18" /></div>
    </div>
    <div class="min-w-0">
      <b class="block truncate text-[13.5px] font-semibold" data-testid="artifact-title">{{ latest.title ?? post.body }}</b>
      <div class="mt-0.5 flex flex-wrap items-center gap-1.5 text-[12px] text-muted">
        <KindChip :kind="latest.kind" />
        <span data-testid="artifact-meta">{{ kindWords(latest.kind) }} · v{{ latest.version }}<template v-if="redacted"> · redacted</template><template v-else> · {{ formatSize(latest.size ?? 0) }}</template><template v-if="when"> · {{ latest.version > 1 ? 'updated' : 'shown' }} {{ when }}</template></span>
      </div>
    </div>
    <Button variant="outline" size="sm" class="h-7 px-2.5 text-[12.5px]" tabindex="-1" data-testid="artifact-open" @click.stop="open">Open</Button>
  </div>
</template>
