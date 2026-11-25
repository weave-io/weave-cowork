<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { Copy, Check, Plus, Server, Boxes, Hash } from 'lucide-vue-next'
import { get, post, ApiError } from '../../api'
import { store, closeModal } from '../../store'
import AppDialog from '../AppDialog.vue'
import StatusDot from '../StatusDot.vue'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorNote } from '@/components/ui/error-note'
import { Spinner } from '@/components/ui/spinner'

// Where agents run: the runners people connected (one per pinned agent) and the pools that start a sandbox per thread for team templates.
// Admins add a pool here: a one-time code and the command to run on the pool's machine.
interface Where {
  runners: { agentId: number; handle: string; host: string | null; online: boolean; connected: boolean; owner: string | null }[]
  pools: { id: number; name: string; host: string | null; slots: number; memoryMb: number | null; online: boolean; enrolled: boolean; busy: number; waiting: number
    sandboxes: { id: number; threadId: number; state: string; agent: string; channel: string | null }[] }[]
}
const w = ref<Where | null>(null), error = ref('')
const adding = ref<{ poolId: number; name: string; command: string } | null>(null), copied = ref(false), busy = ref(false)
async function load() { try { w.value = await get('/where') } catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' } }
onMounted(load)
watch(() => store.members, load)                 // a pool or a runner came or went (the server says bootstrap-changed)
watch(() => store.viewsVersion, load)
const added = computed(() => adding.value ? w.value?.pools.find(p => p.id === adding.value!.poolId && p.online) ?? null : null)
async function addPool() {
  error.value = ''; busy.value = true
  try { adding.value = await post('/pools', {}); await load() } catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' } finally { busy.value = false }
}
async function copy() { try { await navigator.clipboard.writeText(adding.value!.command); copied.value = true } catch { /* http */ } }
const memory = (mb: number | null) => mb == null ? '' : mb >= 1024 && mb % 1024 === 0 ? `${mb / 1024} GB` : `${mb} MB`
</script>
<template>
  <AppDialog title="Where agents run" description="Runners people connect for their own agents, and pools that start a sandbox per thread for team templates." wide @close="closeModal">
    <Spinner v-if="!w && !error" />
    <template v-if="w">
      <div class="mb-1 flex items-center gap-2">
        <h3 class="m-0 text-[11px] font-semibold tracking-[0.05em] text-muted uppercase">Pools — {{ w.pools.filter(p => p.enrolled).length }}</h3>
        <Button v-if="!adding" size="sm" variant="outline" class="ml-auto" data-testid="add-pool" :disabled="busy" @click="addPool"><Plus :size="14" />Add a pool</Button>
      </div>
      <div v-if="adding && !added" class="mb-3 rounded-card border border-border bg-card-bg p-3" data-testid="pool-code">
        <p class="mt-0 text-[13px]">On a Linux machine with rootless podman and the sandbox image, run this. The code works once and expires in 10 minutes. <b>--slots</b> is how many sandboxes it runs at once, <b>--memory</b> each one's limit.</p>
        <div class="flex items-center gap-2 rounded-btn border border-border bg-panel-bg p-2"><code class="flex-1 bg-transparent font-mono text-[12.5px] break-all" data-testid="pool-command">{{ adding.command }}</code><Button size="sm" variant="outline" @click="copy"><Check v-if="copied" :size="14" /><Copy v-else :size="14" />{{ copied ? 'Copied' : 'Copy' }}</Button></div>
        <p class="mb-0 mt-2 flex items-center gap-2 text-[13px] text-muted"><StatusDot state="waiting" />Waiting for {{ adding.name }} to connect…</p>
      </div>
      <p v-if="added" class="mt-0 mb-3 flex items-center gap-2 text-[13px] font-semibold" data-testid="pool-connected"><StatusDot state="online" />{{ added.name }} is connected with {{ added.slots }} slots</p>
      <EmptyState v-if="!w.pools.some(p => p.enrolled)" compact bordered title="No pools yet" description="A pool starts a sandbox for each thread of a team template. Add one to use templates." />
      <ul class="m-0 mb-5 list-none p-0" data-testid="pools">
        <li v-for="p in w.pools.filter(x => x.enrolled)" :key="p.id" class="border-b border-border py-2.5" :data-pool="p.name">
          <div class="flex flex-wrap items-center gap-2.5">
            <Boxes :size="16" class="text-muted" aria-hidden="true" />
            <b class="font-semibold">{{ p.name }}</b>
            <span class="text-xs text-muted">{{ p.host }}</span>
            <span class="flex items-center gap-1.5 text-xs" :class="p.online ? 'text-running-text' : 'text-muted'" data-testid="pool-state"><StatusDot :state="p.online ? 'online' : 'offline'" />{{ p.online ? 'online' : 'offline' }}</span>
            <span class="ml-auto text-[12.5px] text-muted" data-testid="pool-slots">{{ p.busy }} of {{ p.slots }} slots busy · {{ memory(p.memoryMb) }} each<template v-if="p.waiting"> · {{ p.waiting }} waiting</template></span>
          </div>
          <ul v-if="p.sandboxes.length" class="m-0 mt-1.5 list-none pl-[26px]">
            <li v-for="s in p.sandboxes" :key="s.id" class="flex items-center gap-2 text-[12.5px] text-muted" :data-sandbox="s.id">
              <StatusDot :state="s.state === 'running' ? 'online' : 'waiting'" /><b class="font-medium text-text">@{{ s.agent }}</b> in a thread in <Hash :size="11" class="-mr-1" aria-hidden="true" />{{ s.channel }}<span>· {{ s.state }}</span>
            </li>
          </ul>
        </li>
      </ul>

      <h3 class="m-0 mb-1 text-[11px] font-semibold tracking-[0.05em] text-muted uppercase">Runners — {{ w.runners.length }}</h3>
      <EmptyState v-if="!w.runners.length" compact bordered title="No runners" description="A runner is what someone connects for an agent of their own (Invite an agent)." />
      <ul class="m-0 list-none p-0" data-testid="runners">
        <li v-for="r in w.runners" :key="r.agentId" class="flex items-center gap-2.5 border-b border-border py-2" :data-runner="r.handle">
          <Server :size="15" class="text-muted" aria-hidden="true" />
          <span class="font-medium">{{ r.host ?? 'not connected yet' }}</span>
          <span class="text-xs text-muted">serves @{{ r.handle }} · {{ r.owner }}'s</span>
          <span class="ml-auto flex items-center gap-1.5 text-xs text-muted"><StatusDot :state="r.online ? 'online' : 'offline'" />{{ r.online ? 'online' : r.connected ? 'offline' : 'never connected' }}</span>
        </li>
      </ul>
    </template>
    <ErrorNote v-if="error" :message="error" class="mt-3" />
  </AppDialog>
</template>
