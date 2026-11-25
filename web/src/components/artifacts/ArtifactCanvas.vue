<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { X, Maximize2, Minimize2, Link2, Download, Check, ExternalLink, ChevronLeft, Trash2 } from 'lucide-vue-next'
import { memberById } from '@/store'
import { post } from '@/api'
import { isPhone } from '@/layout'
import { artifactById, canvas, canvasLink, closeCanvas, closeTab, loadArtifact, pickVersion, shownVersion } from '@/artifacts'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Spinner } from '@/components/ui/spinner'
import { KindChip } from '@/components/ui/kind-chip'
import { confirmAction } from '@/components/ui/confirm'
import ArtifactView from './ArtifactView.vue'

// The canvas beside a thread's full view (mockup A): a tab per open artifact, a version picker, full screen, its own page, copy link, download
// and close; and "Ask … to change this…", which posts the person's reply in the thread quoting the artifact and the version they're looking at.
// On a phone it fills the screen, with a back button.
const props = defineProps<{ channelId: number; threadId: number }>()
const router = useRouter()
const tab = computed(() => canvas.tabs.find(t => t.id === canvas.active))
const a = computed(() => artifactById(canvas.active))
const v = computed(() => shownVersion(a.value, tab.value))
const agent = computed(() => memberById(a.value?.agentId ?? 0)?.displayName ?? 'the agent')
const by = computed(() => memberById(v.value?.publishedBy ?? 0)?.displayName ?? agent.value)
const titleOf = (id: number) => artifactById(id)?.title ?? '…'
const kindOf = (id: number) => artifactById(id)?.kind ?? 'file'
const at = (ms: number) => new Date(ms).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })
const picked = computed({
  get: () => v.value?.version ?? 0,
  set: (n: number) => { if (canvas.active != null) pickVersion(canvas.active, n === a.value?.versions.find(x => !x.redacted)?.version ? null : n) },
})
// a signed address lasts 30 minutes: ask again while the canvas is open
const refresh = setInterval(() => { for (const t of canvas.tabs) void loadArtifact(t.id) }, 60_000)
onBeforeUnmount(() => clearInterval(refresh))

const copied = ref(false)
async function copy() {
  if (!a.value || !v.value) return
  const link = canvasLink(location.origin, props.channelId, props.threadId, a.value.id, tab.value?.version ?? null)
  try { await navigator.clipboard.writeText(link) } catch { /* no clipboard: the link is still shown in the title */ }
  copied.value = true; setTimeout(() => { copied.value = false }, 1600)
}
const download = computed(() => a.value && v.value ? `/api/artifacts/${a.value.id}/versions/${v.value.version}/download` : undefined)
const page = () => { if (a.value && v.value) router.push({ path: `/artifacts/${a.value.id}`, query: tab.value?.version != null ? { v: String(v.value.version) } : {} }) }
async function redact() {
  if (!a.value || !v.value) return
  const id = a.value.id, version = v.value.version
  await confirmAction({ title: 'Redact this version?', subject: `${v.value.title} · v${version}`, body: 'Its files are deleted for everyone. The card and the audit keep the fact that it was shown.',
    confirmLabel: 'Redact version', destructive: true, run: async () => { await post(`/artifacts/${id}/redact`, { version }); await loadArtifact(id, true) } })
}

const text = ref(''), sending = ref(false), error = ref('')
async function ask() {
  const body = text.value.trim()
  if (!body || !a.value || !v.value || sending.value) return
  sending.value = true; error.value = ''
  try { await post(`/channels/${props.channelId}/posts`, { body, threadId: props.threadId, quote: { artifactId: a.value.id, version: v.value.version } }); text.value = '' }
  catch (e: any) { error.value = e?.message ?? 'Couldn\'t send it. Try again.' }
  finally { sending.value = false }
}
function onKey(e: KeyboardEvent) { if (e.key === 'Escape' && canvas.full) canvas.full = false }
onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))
watch(() => canvas.active, id => { if (id != null) void loadArtifact(id) }, { immediate: true })
</script>
<template>
  <aside class="flex min-h-0 min-w-0 flex-col bg-panel-bg" aria-label="Canvas" data-testid="canvas" :data-artifact="canvas.active" :data-version="v?.version">
    <div class="thin-scrollbar flex shrink-0 items-end gap-0.5 overflow-x-auto border-b border-border px-2.5 pt-2" role="tablist">
      <Button v-if="isPhone" variant="toolbar-icon" size="toolbar" class="mb-1 mr-1" aria-label="Back to the thread" data-testid="canvas-back" @click="closeCanvas"><ChevronLeft :size="18" /></Button>
      <div v-for="t in canvas.tabs" :key="t.id" role="tab" :aria-selected="t.id === canvas.active" tabindex="0" data-testid="canvas-tab" :data-artifact="t.id"
        class="-mb-px flex max-w-[240px] cursor-pointer items-center gap-1.5 rounded-t-[8px] border border-b-0 px-2.5 py-1.5 text-[12.5px] whitespace-nowrap"
        :class="t.id === canvas.active ? 'border-border bg-card-bg text-text' : 'border-transparent text-muted hover:text-text'"
        @click="canvas.active = t.id" @keydown.enter="canvas.active = t.id">
        <KindChip :kind="kindOf(t.id)" /><span class="min-w-0 truncate">{{ titleOf(t.id) }}</span>
        <button type="button" class="ml-0.5 grid size-4 cursor-pointer place-items-center rounded-[4px] border-0 bg-transparent p-0 text-muted opacity-70 hover:bg-text/8 hover:opacity-100" :aria-label="`Close ${titleOf(t.id)}`" data-testid="canvas-tab-close" @click.stop="closeTab(t.id)"><X :size="12" /></button>
      </div>
    </div>
    <template v-if="a && v">
      <div class="flex shrink-0 flex-wrap items-center gap-2 border-b border-border px-3 py-2 text-[12.5px] text-muted">
        <b class="min-w-0 truncate text-[13.5px] font-semibold text-text" data-testid="canvas-title">{{ v.title }}</b>
        <select v-model.number="picked" class="h-7 rounded-[7px] border border-border bg-main-bg px-2 text-[12px] text-text" aria-label="Version" data-testid="canvas-version">
          <option v-for="x in a.versions" :key="x.version" :value="x.version">v{{ x.version }} · {{ at(x.at) }}{{ x.redacted ? ' · redacted' : '' }}</option>
        </select>
        <span class="whitespace-nowrap">by {{ by }}</span>
        <span class="ml-auto flex items-center gap-1">
          <Button v-if="!isPhone" variant="outline" size="toolbar" :aria-label="canvas.full ? 'Leave full screen' : 'Full screen'" :title="canvas.full ? 'Leave full screen' : 'Full screen'" data-testid="canvas-full" @click="canvas.full = !canvas.full">
            <Minimize2 v-if="canvas.full" :size="14" /><Maximize2 v-else :size="14" /></Button>
          <Button variant="outline" size="toolbar" aria-label="Open full view" title="Open full view: the artifact on its own page" data-testid="canvas-page" @click="page"><ExternalLink :size="14" /></Button>
          <Button variant="outline" size="toolbar" :aria-label="copied ? 'Link copied' : 'Copy link'" :title="copied ? 'Link copied' : 'Copy link'" data-testid="canvas-copy" :data-copied="copied || undefined" @click="copy">
            <Check v-if="copied" :size="14" class="text-running-text" /><Link2 v-else :size="14" /></Button>
          <Button v-if="!v.redacted" as="a" :href="download" variant="outline" size="toolbar" aria-label="Download" title="Download" data-testid="canvas-download"><Download :size="14" /></Button>
          <Button v-if="a.canRedact && !v.redacted" variant="outline" size="toolbar" aria-label="Redact this version" title="Redact this version" data-testid="canvas-redact" @click="redact"><Trash2 :size="14" /></Button>
          <Button variant="outline" size="toolbar" aria-label="Close the canvas" title="Close" data-testid="canvas-close" @click="closeCanvas"><X :size="14" /></Button>
        </span>
      </div>
      <div class="artifact-stage-bg min-h-0 flex-1 p-3"><ArtifactView :artifact="a" :version="v" /></div>
      <form class="grid shrink-0 gap-1.5 border-t border-border px-3 py-2.5" @submit.prevent="ask">
        <div class="flex gap-2">
          <Input v-model="text" class="h-9 flex-1" :placeholder="`Ask ${agent} to change this…`" :aria-label="`Ask ${agent} to change this`" data-testid="canvas-ask" />
          <Button type="submit" size="sm" class="h-9" :disabled="!text.trim() || sending" data-testid="canvas-send">Send</Button>
        </div>
        <div class="text-[12px] text-muted" :class="error && 'text-error-text'">{{ error || `Goes into the thread as your reply, quoting “${v.title}” v${v.version}.` }}</div>
      </form>
    </template>
    <div v-else class="grid flex-1 place-items-center"><Spinner /></div>
  </aside>
</template>
