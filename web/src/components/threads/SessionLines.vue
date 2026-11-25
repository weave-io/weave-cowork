<script setup lang="ts">
import { computed } from 'vue'
import { GitBranch } from 'lucide-vue-next'
import { memberById } from '../../store'
import type { ThreadAgent } from '../../types'
import StatusDot from '../StatusDot.vue'

// The session behind a thread, one line per agent: its harness (or team template), where it works, its branch, who started it, what it used,
// and whether it's working. Shared by the thread's quick look and its full view.
const props = defineProps<{ agents: ThreadAgent[]; startedBy?: number | null }>()
const starter = computed(() => memberById(props.startedBy ?? 0))

/** "/home/alex/.bin/opencode acp" → "opencode acp": the executable's name, then its arguments. */
function shortCommand(cmd: string[]) {
  const [exe = '', ...rest] = cmd
  return [exe.split(/[\\/]/).pop(), ...rest].join(' ')
}
// a team template is online in a thread while its sandbox's runner is connected; a pinned agent while its runner is
const isOnline = (a: ThreadAgent) => a.placement === 'pool' ? a.online : memberById(a.id)?.online ?? a.online
function dot(a: ThreadAgent) {
  if (a.pausedBy) return 'offline'
  if (a.placement === 'pool' && !a.online) return ['waiting', 'starting', 'resuming'].includes(a.sandbox?.state ?? '') ? 'waiting' : 'offline'
  return !isOnline(a) ? (a.queued > 0 ? 'waiting' : 'offline') : a.busy ? 'working' : 'online'
}
function status(a: ThreadAgent) {
  if (a.pausedBy) return `paused by ${a.pausedBy}`
  if (a.placement === 'pool' && !a.online)
    return ({ waiting: 'waiting for room', starting: 'starting…', resuming: 'resuming…', suspending: 'stopping…', suspended: 'stopped, volume kept', failed: "couldn't start", deleting: 'deleting its volume', kept: 'stopped, volume kept', deleted: 'done, volume deleted' } as Record<string, string>)[a.sandbox?.state ?? ''] ?? 'starting…'
  if (!isOnline(a)) return a.queued > 0 ? 'waiting for the runner' : 'offline'
  if (a.busy) return 'working…'
  return a.queued > 0 ? 'queued' : 'idle'
}
</script>
<template>
  <div v-for="a in agents" :key="a.id" class="mt-1.5 flex flex-wrap items-center gap-1.5 text-xs text-muted" data-testid="session-line">
    <span class="rounded-[5px] border border-border px-1.5 font-mono text-[11.5px]">session</span>
    <b class="text-text">@{{ a.handle }}</b>
    <span v-if="a.placement === 'pool'" data-testid="session-template">team template</span>
    <span v-else class="font-mono text-[11.5px]" :title="a.harnessCommand.join(' ')" data-testid="session-command">{{ shortCommand(a.harnessCommand) }}</span>
    <span v-if="a.placement === 'pool' && a.sandbox?.pool" data-testid="session-where">· sandbox on {{ a.sandbox.pool }}</span>
    <span v-if="a.workspace?.branch" class="flex items-center gap-1" data-testid="session-branch">· <GitBranch :size="12" aria-hidden="true" /><code class="font-mono text-[11.5px]">{{ a.workspace.branch }}</code></span>
    <span v-if="a.placement !== 'pool' && (a.workspace?.host ?? a.runnerHost)" :class="!(a.workspace?.sandboxed) && 'text-idle-text'" data-testid="session-where">· {{ a.workspace?.sandboxed ? 'sandboxed' : 'unsandboxed' }} on {{ a.workspace?.host ?? a.runnerHost }}</span>
    <span v-if="starter">· started by {{ starter.displayName }}</span>
    <span v-if="a.usage?.tokens" data-testid="session-usage">· {{ a.usage.tokens.toLocaleString() }} tokens<template v-if="a.usage.cost != null"> · ${{ a.usage.cost.toFixed(2) }}</template></span>
    <span class="ml-auto flex items-center gap-1.5" data-testid="agent-status">
      <StatusDot :state="dot(a)" />
      {{ status(a) }}
    </span>
  </div>
</template>
