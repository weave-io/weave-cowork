<script setup lang="ts">
import { computed } from 'vue'
import { Activity, Pencil, Plus } from 'lucide-vue-next'
import { store, currentChannel, channelMembers, memberById, openModal } from '../store'
import { Button } from '@/components/ui/button'
import MemberAvatar from './MemberAvatar.vue'
import AgentBadge from './AgentBadge.vue'

const members = computed(() => channelMembers(currentChannel.value))
const people = computed(() => members.value.filter(m => m.kind === 'person'))
const agents = computed(() => members.value.filter(m => m.kind === 'agent'))
</script>
<template>
  <aside class="thin-scrollbar min-h-0 overflow-auto bg-panel-bg px-2 py-3" aria-label="Members" data-testid="member-list">
    <div class="px-2 pb-1 text-[11px] font-semibold tracking-[0.05em] text-muted uppercase">People — {{ people.length }}</div>
    <div v-for="m in people" :key="m.id" class="flex items-center gap-2.5 rounded-btn px-2 py-1.5">
      <MemberAvatar :member="m" />
      <span class="min-w-0"><span class="block truncate text-[13.5px] font-medium">{{ m.displayName }}<span v-if="m.id === store.meId" class="font-normal text-muted"> (you)</span></span>
        <span class="block truncate text-[11.5px] text-muted">@{{ m.handle }}{{ m.role === 'admin' ? ' · admin' : '' }}</span></span>
    </div>
    <div class="px-2 pt-4 pb-1 text-[11px] font-semibold tracking-[0.05em] text-muted uppercase">Agents — {{ agents.length }}</div>
    <div v-for="m in agents" :key="m.id" class="agent-row relative flex items-center gap-2.5 rounded-btn px-2 py-1.5" :data-agent="m.handle">
      <MemberAvatar :member="m" status />
      <span class="min-w-0 flex-1"><span class="flex min-w-0 items-center gap-1.5 text-[13.5px] font-medium"><span class="truncate">{{ m.displayName }}</span> <AgentBadge class="shrink-0" /></span>
        <span class="block truncate text-[11.5px] text-muted" data-testid="agent-sub">{{ m.online ? 'online' : 'offline' }} · owned by {{ memberById(m.ownerId ?? 0)?.displayName }}</span></span>
      <span class="row-actions flex shrink-0 items-center">
      <Button variant="toolbar-icon" size="toolbar" class="shrink-0" data-testid="agent-activity-row" :aria-label="`${m.displayName}'s activity`" title="Activity" @click="openModal('agent-activity', { id: m.id })"><Activity :size="14" /></Button>
      <Button v-if="store.isAdmin || m.ownerId === store.meId" variant="toolbar-icon" size="toolbar" class="shrink-0" data-testid="edit-agent-row" :aria-label="`Edit ${m.displayName}`" title="Edit" @click="openModal('edit-agent', { id: m.id })"><Pencil :size="14" /></Button>
      </span>
    </div>
    <p v-if="!agents.length" class="m-0 px-2 text-xs text-muted">No agents here yet.</p>
    <Button variant="ghost" size="sm" class="mx-1 mt-3 h-8 gap-1 text-[12.5px] text-accent-text" @click="openModal('invite-agent')"><Plus :size="14" />Invite an agent</Button>
  </aside>
</template>
<style scoped>
/* With a mouse, an agent's Activity and Edit show over the end of its row on hover or keyboard focus, so its name and badge have the
   row's width (they used to run under the buttons). On a touch screen they stay in the row. */
@media (hover: hover) {
  .row-actions { position: absolute; top: 50%; right: 4px; transform: translateY(-50%); opacity: 0; border-radius: var(--radius-btn);
    background: var(--panel-bg); box-shadow: -10px 0 10px -2px var(--panel-bg); transition: opacity .12s; }
  .agent-row:hover .row-actions, .agent-row:focus-within .row-actions { opacity: 1; }
}
</style>
