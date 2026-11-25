<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { get, put, ApiError } from '../../api'
import { store, closeModal, loadBootstrap, memberById, projectById } from '../../store'
import type { Boundary } from '../../types'
import AppDialog from '../AppDialog.vue'
import AgentBoundary from '../AgentBoundary.vue'
import MemberAvatar from '../MemberAvatar.vue'
import { Button } from '@/components/ui/button'
import { ErrorNote } from '@/components/ui/error-note'
import { EmptyState } from '@/components/ui/empty-state'
import { Spinner } from '@/components/ui/spinner'

// Adding an agent to a project shows what it may do there, in sentences, before it joins (the design's "boundary of an agent").
const props = defineProps<{ projectId: number }>()
const project = computed(() => projectById(props.projectId))
const inProject = ref<number[]>([])
const candidates = computed(() => store.members.filter(m => m.kind === 'agent' && !m.removed && (m.ownerId === store.meId || store.isAdmin) && !inProject.value.includes(m.id)))
const pickedId = ref<number | null>(null)
const grant = ref<'members' | 'owner'>('members')
const boundary = ref<Boundary | null>(null)
const error = ref(''), busy = ref(false), loading = ref(true)
const picked = computed(() => memberById(pickedId.value ?? 0))
const ownerName = (id: number | null) => id === store.meId ? `${memberById(id)?.displayName}'s own` : `${memberById(id ?? 0)?.displayName ?? 'someone'}'s`
const shortCommand = (cmd: string[] | null) => { const [exe = '', ...rest] = cmd ?? []; return [exe.split(/[\\/]/).pop(), ...rest].join(' ') }

onMounted(async () => {
  try { inProject.value = (await get(`/projects/${props.projectId}`)).agents.map((a: any) => a.id) } catch { /* the list shows what it can */ }
  pickedId.value = candidates.value[0]?.id ?? null
  loading.value = false
})
watch([pickedId, grant], async () => {
  if (pickedId.value == null) { boundary.value = null; return }
  try { boundary.value = await get(`/projects/${props.projectId}/agents/${pickedId.value}/boundary?takesWorkFrom=${grant.value}`) }
  catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' }
})
async function add() {
  if (pickedId.value == null) return
  error.value = ''; busy.value = true
  try { await put(`/projects/${props.projectId}/agents/${pickedId.value}`, { takesWorkFrom: grant.value }); await loadBootstrap(); closeModal() }
  catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' } finally { busy.value = false }
}
</script>
<template>
  <AppDialog :title="`Add an agent to ${project?.name ?? 'the project'}`" wide @close="closeModal">
    <Spinner v-if="loading" />
    <EmptyState v-else-if="!candidates.length" compact bordered title="No agents to add" description="Invite an agent from the server menu first, or every agent you own is here already." />
    <template v-else>
      <div v-if="candidates.length > 1" class="mb-3 inline-flex flex-wrap gap-0.5 rounded-btn border border-border p-0.5" role="tablist" aria-label="Which agent">
        <button v-for="m in candidates" :key="m.id" type="button" role="tab" :aria-selected="m.id === pickedId" class="rounded-[6px] px-2.5 py-1 text-[12.5px]" :class="m.id === pickedId ? 'bg-panel-bg font-medium shadow-sm' : 'text-muted hover:text-text'" :data-pick-agent="m.handle" @click="pickedId = m.id">
          {{ m.handle }} · {{ ownerName(m.ownerId) }}
        </button>
      </div>
      <div v-if="picked" class="mb-4 flex items-start gap-3" data-testid="boundary-agent">
        <MemberAvatar :member="picked" status />
        <div class="min-w-0">
          <div class="flex items-center gap-2"><b class="text-[15px]">{{ picked.displayName }}</b><span class="rounded-[5px] bg-accent-dim px-1.5 text-[11px] font-medium text-accent-text">{{ ownerName(picked.ownerId) }}</span></div>
          <p v-if="picked.about" class="m-0 text-[13px] text-muted">{{ picked.about }}</p>
          <p class="m-0 text-[12.5px] text-muted"><code class="font-mono text-[12px]">{{ shortCommand(picked.harnessCommand) }}</code> · {{ picked.runnerHost ? `runs on ${picked.runnerHost}` : 'its runner hasn\'t connected yet' }} · {{ memberById(picked.ownerId ?? 0)?.displayName }}'s subscription</p>
        </div>
      </div>
      <AgentBoundary v-if="boundary && boundary.agent.id === pickedId" :boundary="boundary">
        <template #takesWorkFrom>
          <span class="mt-1 flex gap-1" role="radiogroup" aria-label="Takes work from">
            <Button size="sm" type="button" role="radio" :aria-checked="grant === 'members'" class="h-6 px-2 text-xs" :variant="grant === 'members' ? 'secondary' : 'ghost'" data-testid="grant-members" @click="grant = 'members'">Members</Button>
            <Button size="sm" type="button" role="radio" :aria-checked="grant === 'owner'" class="h-6 px-2 text-xs" :variant="grant === 'owner' ? 'secondary' : 'ghost'" data-testid="grant-owner" @click="grant = 'owner'">Only {{ boundary.agent.ownerName }}</Button>
          </span>
        </template>
      </AgentBoundary>
      <Spinner v-else class="my-6" />
    </template>
    <ErrorNote v-if="error" :message="error" class="mt-3" />
    <template #footer>
      <Button variant="outline" @click="closeModal">Cancel</Button>
      <Button :disabled="busy || pickedId == null" data-testid="add-agent-confirm" @click="add"><Spinner v-if="busy" size="sm" />Add {{ picked?.handle ?? 'agent' }}</Button>
    </template>
  </AppDialog>
</template>
