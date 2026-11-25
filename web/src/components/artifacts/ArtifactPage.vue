<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ChevronLeft, Download, Link2, Check, FileQuestion, Menu } from 'lucide-vue-next'
import { memberById, store } from '@/store'
import { isPhone, navOpen } from '@/layout'
import { artifactById, artifacts, loadArtifact } from '@/artifacts'
import { threadPath } from '@/threads'
import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'
import { EmptyState } from '@/components/ui/empty-state'
import { KindChip } from '@/components/ui/kind-chip'
import ArtifactView from './ArtifactView.vue'

// "Open full view": an artifact on its own page (/artifacts/:id?v=n), the whole width, with its versions, a way back to its thread, the link and
// the download. Only for people who see its thread's channel; anyone else gets "No such artifact".
const props = defineProps<{ id: number }>()
const route = useRoute(), router = useRouter()
watch(() => props.id, id => { void loadArtifact(id, true) }, { immediate: true })
const a = computed(() => artifactById(props.id))
const missing = computed(() => artifacts.byId[props.id] && !artifacts.byId[props.id].data)
const wanted = computed(() => typeof route.query.v === 'string' ? Number(route.query.v) : null)
const v = computed(() => a.value ? (wanted.value != null ? a.value.versions.find(x => x.version === wanted.value) : a.value.versions.find(x => !x.redacted)) ?? a.value.versions[0] : null)
const pick = (n: number) => router.replace({ query: n === a.value?.versions.find(x => !x.redacted)?.version ? {} : { v: String(n) } })
const at = (ms: number) => new Date(ms).toLocaleString([], { day: 'numeric', month: 'short', hour: 'numeric', minute: '2-digit' })
const back = () => { if (a.value) router.push({ path: threadPath(a.value.channelId, a.value.threadId), query: { a: String(a.value.id), ...(wanted.value != null ? { v: String(wanted.value) } : {}) } }) }
const channel = computed(() => store.channels.find(c => c.id === a.value?.channelId))
const copied = ref(false)
async function copy() { try { await navigator.clipboard.writeText(location.href) } catch { /* shown in the address bar anyway */ } copied.value = true; setTimeout(() => { copied.value = false }, 1600) }
const refresh = setInterval(() => { void loadArtifact(props.id) }, 60_000)
onBeforeUnmount(() => clearInterval(refresh))
</script>
<template>
  <main class="flex min-h-0 min-w-0 flex-1 flex-col" data-testid="artifact-page">
    <template v-if="a && v">
      <header class="flex min-h-[52px] shrink-0 flex-wrap items-center gap-2 border-b border-border px-3 py-2 sm:px-4">
        <Button v-if="isPhone" variant="toolbar-icon" size="toolbar-lg" aria-label="Open menu" @click="navOpen = true"><Menu :size="18" /></Button>
        <Button variant="ghost" size="sm" class="h-8 gap-1 px-2 text-[13px] text-muted" data-testid="artifact-back" @click="back"><ChevronLeft :size="15" />Back to the thread<template v-if="channel && !isPhone"> in #{{ channel.name }}</template></Button>
        <h2 class="m-0 flex min-w-0 items-center gap-2 text-[15px] font-semibold"><KindChip :kind="v.kind" /><span class="truncate" data-testid="artifact-page-title">{{ v.title }}</span></h2>
        <select :value="v.version" class="h-8 rounded-[7px] border border-border bg-main-bg px-2 text-[12.5px] text-text" aria-label="Version" data-testid="artifact-page-version" @change="pick(Number(($event.target as HTMLSelectElement).value))">
          <option v-for="x in a.versions" :key="x.version" :value="x.version">v{{ x.version }} · {{ at(x.at) }}{{ x.redacted ? ' · redacted' : '' }}</option>
        </select>
        <span class="text-[12.5px] whitespace-nowrap text-muted">by {{ memberById(v.publishedBy)?.displayName ?? 'an agent' }}</span>
        <span class="ml-auto flex gap-1">
          <Button variant="outline" size="toolbar-lg" :aria-label="copied ? 'Link copied' : 'Copy link'" :title="copied ? 'Link copied' : 'Copy link'" @click="copy"><Check v-if="copied" :size="15" class="text-running-text" /><Link2 v-else :size="15" /></Button>
          <Button v-if="!v.redacted" as="a" :href="`/api/artifacts/${a.id}/versions/${v.version}/download`" variant="outline" size="toolbar-lg" aria-label="Download" title="Download"><Download :size="15" /></Button>
        </span>
      </header>
      <div class="artifact-stage-bg min-h-0 flex-1 p-3 sm:p-4"><ArtifactView :artifact="a" :version="v" /></div>
    </template>
    <EmptyState v-else-if="missing" :icon="FileQuestion" title="No such artifact" description="It doesn't exist, or it's in a channel you can't see." class="flex-1" />
    <div v-else class="grid flex-1 place-items-center"><Spinner /></div>
  </main>
</template>
