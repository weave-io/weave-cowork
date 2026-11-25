<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { get, ApiError } from '../../api'
import { store, closeModal, memberById } from '../../store'
import AppDialog from '../AppDialog.vue'
import StatusDot from '../StatusDot.vue'
import AgentBadge from '../AgentBadge.vue'
import { Badge } from '@/components/ui/badge'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorNote } from '@/components/ui/error-note'
import { RelativeTime } from '@/components/ui/relative-time'
import { formatDuration } from '@/lib/format'

interface Turn { turn: string; threadId: number | null; channelName: string | null; thread: string | null; forId: number | null; startedAt: number; endedAt: number | null; outcome: string; reason: string | null; toolCalls: number; host: string | null }
const props = defineProps<{ agentId: number }>()
const data = ref<any>(null), error = ref('')
const member = computed(() => memberById(props.agentId))
async function load() { try { data.value = await get(`/agents/${props.agentId}/activity`) } catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' } }
onMounted(load)
watch(() => store.viewsVersion, load)
const where = (t: Turn) => t.channelName ? `#${t.channelName}${t.thread ? ` · “${t.thread}”` : ''}` : 'a private thread'
const outcomeLabel: Record<string, string> = { running: 'working', finished: 'answered', failed: 'failed', interrupted: 'cut off' }
const tone = (o: string) => o === 'failed' || o === 'interrupted' ? 'text-error-text' : o === 'running' ? 'text-running-text' : 'text-muted'
</script>
<template>
  <AppDialog :title="`${member?.displayName ?? 'Agent'} · activity`" wide @close="closeModal">
    <ErrorNote v-if="error" :message="error" />
    <template v-else-if="data">
      <div class="mb-4 flex flex-wrap items-center gap-x-4 gap-y-1 text-[13px]" data-testid="activity-summary">
        <span class="flex items-center gap-1.5"><StatusDot :state="data.agent.online ? 'online' : 'offline'" /><b class="font-medium">{{ data.agent.online ? 'online' : 'offline' }}</b><AgentBadge /></span>
        <span class="text-muted">owned by {{ memberById(data.agent.ownerId ?? 0)?.displayName }}<template v-if="data.agent.host"> · runs on {{ data.agent.host }}</template></span>
        <span class="ml-auto text-muted"><b class="font-medium text-text" data-testid="total-turns">{{ data.totals.turns }}</b> turn{{ data.totals.turns === 1 ? '' : 's' }} · <b class="font-medium text-text">{{ data.totals.toolCalls }}</b> tool call{{ data.totals.toolCalls === 1 ? '' : 's' }} · <b class="font-medium" :class="data.totals.failed ? 'text-error-text' : 'text-text'" data-testid="total-failed">{{ data.totals.failed }}</b> failed</span>
      </div>

      <h3 class="m-0 mb-1 text-[11px] font-semibold tracking-[0.05em] text-muted uppercase">Busy in — {{ data.busy.length }}</h3>
      <EmptyState v-if="!data.busy.length" compact bordered title="Idle" description="It isn't working in any thread right now." class="mb-4" />
      <ul v-else class="m-0 mb-4 list-none p-0" data-testid="busy-list">
        <li v-for="t in data.busy" :key="t.turn" class="flex items-center gap-2.5 border-b border-border py-2 text-[13.5px]"><StatusDot state="working" /><span class="min-w-0 flex-1 truncate">{{ where(t) }}</span>
          <span class="shrink-0 text-xs text-muted">for {{ memberById(t.forId ?? 0)?.displayName ?? 'someone' }} · since <RelativeTime :at="t.startedAt" compact /></span></li>
      </ul>

      <h3 class="m-0 mb-1 text-[11px] font-semibold tracking-[0.05em] text-muted uppercase">Recent turns</h3>
      <EmptyState v-if="!data.turns.length" compact bordered title="No turns yet" description="Mention it in a channel to start one." class="mb-4" />
      <ul v-else class="m-0 mb-4 list-none p-0" data-testid="turns-list">
        <li v-for="t in data.turns" :key="t.turn" class="flex items-center gap-2.5 border-b border-border py-2 text-[13.5px]" :data-outcome="t.outcome" data-testid="turn">
          <Badge variant="status" class="w-[72px] justify-center text-[11px]" :class="tone(t.outcome)">{{ outcomeLabel[t.outcome] ?? t.outcome }}</Badge>
          <span class="min-w-0 flex-1 truncate">{{ where(t) }}</span>
          <span class="shrink-0 text-xs text-muted">{{ memberById(t.forId ?? 0)?.displayName ?? '' }} · {{ t.toolCalls }} tool call{{ t.toolCalls === 1 ? '' : 's' }}<template v-if="t.endedAt"> · {{ formatDuration(t.endedAt - t.startedAt) }}</template> · <RelativeTime :at="t.startedAt" compact /></span>
        </li>
      </ul>

      <template v-if="data.failures.length">
        <h3 class="m-0 mb-1 text-[11px] font-semibold tracking-[0.05em] text-muted uppercase">Failures — {{ data.totals.failed }}</h3>
        <ul class="m-0 mb-4 list-none p-0" data-testid="failures-list">
          <li v-for="t in data.failures" :key="t.turn" class="border-b border-border py-2 text-[13.5px]"><span class="text-error-text">{{ t.reason ?? 'It failed.' }}</span><span class="block text-xs text-muted">{{ where(t) }} · <RelativeTime :at="t.startedAt" /></span></li>
        </ul>
      </template>

      <h3 class="m-0 mb-1 text-[11px] font-semibold tracking-[0.05em] text-muted uppercase">Runner connections</h3>
      <EmptyState v-if="!data.connections.length" compact bordered title="Never connected" description="Its runner hasn't connected yet." />
      <ul v-else class="m-0 list-none p-0" data-testid="connections-list">
        <li v-for="(k, i) in data.connections" :key="i" class="flex items-center gap-2.5 border-b border-border py-2 text-[13px]"><StatusDot :state="k.disconnectedAt ? 'offline' : 'online'" /><span class="flex-1">{{ k.host ?? 'a runner' }}</span>
          <span class="text-xs text-muted">connected <RelativeTime :at="k.connectedAt" /><template v-if="k.disconnectedAt"> · left <RelativeTime :at="k.disconnectedAt" /></template></span></li>
      </ul>
    </template>
  </AppDialog>
</template>
