<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { Download, FileQuestion } from 'lucide-vue-next'
import type { Artifact, ArtifactVersion } from '@/artifacts'
import { jsonTable, parseCsv, withScheme } from '@/artifacts'
import { scheme } from '@/theme'
import { renderDocument } from '@/render'
import { handles } from '@/store'
import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'
import { EmptyState } from '@/components/ui/empty-state'

// One artifact version, shown by its kind. A page (and anything it loads) runs in a frame with sandbox="allow-scripts" and no
// allow-same-origin, from a short-lived signed address served with a CSP sandbox and connect-src 'none': an opaque origin, no cookies, no
// CoWork API, nowhere else to reach. A markdown document renders here with the posts' sanitiser, its images from its own address; images and
// SVG are images; CSV and JSON are tables; a PDF uses the browser's viewer; anything else downloads.
const props = defineProps<{ artifact: Artifact; version: ArtifactVersion }>()
const text = ref<string | null>(null), failed = ref(false), loading = ref(false)
const raw = computed(() => `/api/artifacts/${props.artifact.id}/versions/${props.version.version}/raw/${props.version.entry.split('/').map(encodeURIComponent).join('/')}`)
const download = computed(() => `/api/artifacts/${props.artifact.id}/versions/${props.version.version}/download`)
const needsText = computed(() => ['markdown', 'csv', 'json'].includes(props.version.kind) && !props.version.redacted)
watch(() => [raw.value, needsText.value] as const, async ([url, need]) => {
  text.value = null; failed.value = false
  if (!need) return
  loading.value = true
  try { const r = await fetch(url, { credentials: 'same-origin' }); if (!r.ok) throw new Error(); if (url === raw.value) text.value = await r.text() }
  catch { failed.value = true } finally { loading.value = false }
}, { immediate: true })
const doc = computed(() => text.value != null && props.version.kind === 'markdown' ? renderDocument(text.value, props.version.base, handles.value) : '')
const csv = computed(() => text.value != null && props.version.kind === 'csv' ? parseCsv(text.value) : null)
const json = computed(() => {
  if (text.value == null || props.version.kind !== 'json') return null
  try { const v = JSON.parse(text.value); return { table: jsonTable(v), pretty: JSON.stringify(v, null, 2) } } catch { return { table: null, pretty: text.value } }
})
const cell = (v: unknown) => v == null ? '' : typeof v === 'object' ? JSON.stringify(v) : String(v)
</script>
<template>
  <div class="artifact-stage h-full min-h-0 overflow-auto" data-testid="artifact-view" :data-kind="version.kind" :data-version="version.version">
    <EmptyState v-if="version.redacted" :icon="FileQuestion" title="This version was redacted" description="Its files are gone. That it was shown stays in the thread and the audit." class="mt-10" />
    <iframe v-else-if="version.kind === 'html' && version.url" :key="version.url" :src="withScheme(version.url, scheme)" sandbox="allow-scripts" referrerpolicy="no-referrer" :title="version.title"
      class="block h-full min-h-[440px] w-full rounded-[8px] border-0 bg-white shadow-[0_1px_3px_rgba(0,0,0,.2)]" data-testid="artifact-frame" />
    <iframe v-else-if="version.kind === 'pdf' && version.url" :key="version.url" :src="version.url" referrerpolicy="no-referrer" :title="version.title"
      class="block h-full min-h-[440px] w-full rounded-[8px] border-0 bg-white" data-testid="artifact-frame" />
    <div v-else-if="(version.kind === 'image' || version.kind === 'svg') && version.url" class="grid min-h-full place-items-center">
      <img :src="version.url" :alt="version.title" referrerpolicy="no-referrer" class="max-h-full max-w-full rounded-[6px] bg-white shadow-[0_1px_3px_rgba(0,0,0,.2)]" data-testid="artifact-image" />
    </div>
    <div v-else-if="loading" class="grid h-40 place-items-center"><Spinner /></div>
    <EmptyState v-else-if="failed" :icon="FileQuestion" title="Couldn't load it" description="Try again in a moment." class="mt-10" />
    <article v-else-if="version.kind === 'markdown'" class="doc mx-auto max-w-[760px] rounded-[8px] bg-card-bg px-7 py-6 shadow-[0_1px_3px_rgba(0,0,0,.2)]" data-testid="artifact-doc" v-html="doc" />
    <div v-else-if="csv" class="rounded-[8px] bg-card-bg p-3 shadow-[0_1px_3px_rgba(0,0,0,.2)]">
      <div class="doc"><div class="table-wrap"><table data-testid="artifact-table">
        <thead><tr><th v-for="(h, i) in csv.rows[0] ?? []" :key="i">{{ h }}</th></tr></thead>
        <tbody><tr v-for="(r, i) in csv.rows.slice(1)" :key="i"><td v-for="(c, j) in r" :key="j">{{ c }}</td></tr></tbody>
      </table></div></div>
      <p class="mt-2 text-[12px] text-muted">{{ Math.max(0, csv.rows.length - 1) }} rows<template v-if="csv.more">, the first 2,000 shown. Download it for the rest.</template></p>
    </div>
    <div v-else-if="json" class="rounded-[8px] bg-card-bg p-3 shadow-[0_1px_3px_rgba(0,0,0,.2)]">
      <div v-if="json.table" class="doc"><div class="table-wrap"><table data-testid="artifact-table">
        <thead><tr><th v-for="c in json.table.columns" :key="c">{{ c }}</th></tr></thead>
        <tbody><tr v-for="(r, i) in json.table.rows" :key="i"><td v-for="(c, j) in r" :key="j">{{ cell(c) }}</td></tr></tbody>
      </table></div></div>
      <pre v-else class="m-0 border-0" data-testid="artifact-json">{{ json.pretty }}</pre>
    </div>
    <EmptyState v-else :icon="FileQuestion" title="This kind of file can't be shown here" :description="`${version.entry} · download it to open it.`" class="mt-10">
      <template #action><Button as="a" :href="download" variant="outline" size="sm" class="mt-3 gap-1.5"><Download :size="14" />Download</Button></template>
    </EmptyState>
  </div>
</template>
