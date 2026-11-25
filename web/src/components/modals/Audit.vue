<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { Loader2, RotateCw } from 'lucide-vue-next'
import { get, post, ApiError } from '../../api'
import { store, closeModal } from '../../store'
import AppDialog from '../AppDialog.vue'
import Sentence from '../Sentence.vue'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorNote } from '@/components/ui/error-note'
import { RelativeTime } from '@/components/ui/relative-time'
import { formatCount } from '@/lib/format'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'

interface Entry { id: number; kind: string; at: number; sentence: string; channelId: number | null }
interface Named { id: number; name: string }
interface Projection { name: string; title: string; position: number; head: number; rebuiltAt: number | null }

const entries = ref<Entry[]>([]), hasMore = ref(false), loading = ref(true), error = ref('')
const people = ref<Named[]>([]), agents = ref<Named[]>([]), channels = ref<Named[]>([])
const person = ref('all'), agent = ref('all'), channel = ref('all')
const projections = ref<Projection[]>([]), rebuilding = ref(''), rebuilt = ref('')

function query(before?: number) {
  const q = new URLSearchParams()
  if (person.value !== 'all') q.set('person', person.value)
  if (agent.value !== 'all') q.set('agent', agent.value)
  if (channel.value !== 'all') q.set('channel', channel.value)
  if (before) q.set('before', String(before))
  return q.toString() ? `?${q}` : ''
}
async function load(more = false) {
  error.value = ''
  try {
    const r = await get(`/audit${query(more ? entries.value[entries.value.length - 1]?.id : undefined)}`)
    entries.value = more ? [...entries.value, ...r.entries] : r.entries
    hasMore.value = r.hasMore; people.value = r.people; agents.value = r.agents; channels.value = r.channels
    projections.value = await get('/admin/projections')
  } catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' } finally { loading.value = false }
}
async function rebuild(p: Projection) {
  rebuilding.value = p.name; rebuilt.value = ''; error.value = ''
  try { const r = await post(`/admin/projections/${p.name}/rebuild`); rebuilt.value = `${p.title} rebuilt from ${formatCount(r.replayed)} events.`; await load() }
  catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' } finally { rebuilding.value = '' }
}
onMounted(load)
watch([person, agent, channel], () => load())
watch(() => store.viewsVersion, () => load())
</script>
<template>
  <AppDialog title="Audit" description="What people and agents did, in plain words, from the event log." wide @close="closeModal">
    <div class="mb-3 grid grid-cols-3 gap-2" data-testid="audit-filters">
      <Select v-model="person"><SelectTrigger aria-label="Person" data-testid="audit-person"><SelectValue /></SelectTrigger>
        <SelectContent><SelectItem value="all">Everyone</SelectItem><SelectItem v-for="p in people" :key="p.id" :value="String(p.id)">{{ p.name }}</SelectItem></SelectContent></Select>
      <Select v-model="agent"><SelectTrigger aria-label="Agent" data-testid="audit-agent"><SelectValue /></SelectTrigger>
        <SelectContent><SelectItem value="all">All agents</SelectItem><SelectItem v-for="a in agents" :key="a.id" :value="String(a.id)">{{ a.name }}</SelectItem></SelectContent></Select>
      <Select v-model="channel"><SelectTrigger aria-label="Channel" data-testid="audit-channel"><SelectValue /></SelectTrigger>
        <SelectContent><SelectItem value="all">All channels</SelectItem><SelectItem v-for="c in channels" :key="c.id" :value="String(c.id)">#{{ c.name }}</SelectItem></SelectContent></Select>
    </div>
    <ErrorNote v-if="error" :message="error" class="mb-3" />
    <div v-if="loading" class="flex justify-center py-8 text-muted"><Loader2 class="animate-spin" :size="18" /></div>
    <EmptyState v-else-if="!entries.length" compact bordered title="Nothing here" description="No events match these filters." />
    <ul v-else class="m-0 list-none p-0" data-testid="audit-list">
      <li v-for="e in entries" :key="e.id" class="flex gap-3 border-b border-border py-2 text-[13.5px]" :data-kind="e.kind" data-testid="audit-entry">
        <span class="min-w-0 flex-1"><Sentence :text="e.sentence" /></span>
        <span class="shrink-0 text-xs text-muted"><RelativeTime :at="e.at" compact /></span>
      </li>
    </ul>
    <div v-if="hasMore" class="mt-3 flex justify-center"><Button variant="outline" size="sm" data-testid="audit-more" @click="load(true)">Show older</Button></div>

    <h3 class="mt-6 mb-1 text-[11px] font-semibold tracking-[0.05em] text-muted uppercase">Views built from the log</h3>
    <p class="mt-0 mb-2 text-xs text-muted">Each view keeps its place in the log. If one looks wrong, rebuild it: it is emptied and built again from the first event.</p>
    <ul class="m-0 list-none p-0" data-testid="projections">
      <li v-for="p in projections" :key="p.name" class="flex items-center gap-3 border-b border-border py-2 text-[13px]" :data-projection="p.name">
        <span class="flex-1"><span class="font-medium">{{ p.title }}</span>
          <span class="block text-xs text-muted" data-testid="checkpoint">at event {{ formatCount(p.position) }} of {{ formatCount(p.head) }}<template v-if="p.rebuiltAt"> · rebuilt <RelativeTime :at="p.rebuiltAt" /></template></span></span>
        <Button size="sm" variant="outline" class="gap-1.5" :disabled="rebuilding !== ''" :data-rebuild="p.name" @click="rebuild(p)"><Loader2 v-if="rebuilding === p.name" :size="13" class="animate-spin" /><RotateCw v-else :size="13" />Rebuild</Button>
      </li>
    </ul>
    <p v-if="rebuilt" class="mt-2 mb-0 text-[13px] text-running-text" role="status" data-testid="rebuilt">{{ rebuilt }}</p>
  </AppDialog>
</template>
