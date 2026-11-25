<script setup lang="ts">
import { computed, ref } from 'vue'
import { put, del, post, ApiError } from '../../api'
import { store, closeModal, loadBootstrap, memberById, openModal } from '../../store'
import { confirmAction, type ConfirmOptions } from '@/components/ui/confirm'
import AppDialog from '../AppDialog.vue'
import MemberAvatar from '../MemberAvatar.vue'
import AgentBadge from '../AgentBadge.vue'
import StatusDot from '../StatusDot.vue'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorNote } from '@/components/ui/error-note'

const people = computed(() => store.members.filter(m => m.kind === 'person' && !m.removed))
const agents = computed(() => store.members.filter(m => m.kind === 'agent' && !m.removed))
const error = ref('')
async function run(fn: () => Promise<unknown>, confirm?: ConfirmOptions) {
  if (confirm && !(await confirmAction({ destructive: true, ...confirm }))) return
  error.value = ''
  try { await fn(); await loadBootstrap() } catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' }
}
const canAdminAgent = (ownerId: number | null) => store.isAdmin || ownerId === store.meId
</script>
<template>
  <AppDialog title="People & agents" wide @close="closeModal">
    <h3 class="m-0 mb-1 text-[11px] font-semibold tracking-[0.05em] text-muted uppercase">People — {{ people.length }}</h3>
    <ul class="m-0 mb-5 list-none p-0" data-testid="people-list">
      <li v-for="m in people" :key="m.id" class="flex items-center gap-2.5 border-b border-border py-2" :data-person="m.handle">
        <MemberAvatar :member="m" size="sm" /><span class="font-medium">{{ m.displayName }}</span><span class="text-xs text-muted">@{{ m.handle }}</span>
        <Badge variant="outline" class="rounded-[5px] border-border px-1.5 py-0 text-[11px] font-normal" :class="m.role === 'admin' && 'border-accent text-accent-text'">{{ m.role }}</Badge>
        <span v-if="store.isAdmin && m.id !== store.meId" class="ml-auto flex gap-1.5">
          <Button size="sm" variant="outline" data-testid="toggle-admin" @click="run(() => put(`/people/${m.id}/role`, { role: m.role === 'admin' ? 'member' : 'admin' }))">{{ m.role === 'admin' ? 'Make member' : 'Make admin' }}</Button>
          <Button size="sm" variant="outline" class="text-error-text" data-testid="remove-person" @click="run(() => del(`/people/${m.id}`), { title: `Remove ${m.displayName}?`, body: `They leave ${store.serverName}, and their agents go too.`, confirmLabel: 'Remove person' })">Remove</Button>
        </span>
      </li>
    </ul>
    <h3 class="m-0 mb-1 text-[11px] font-semibold tracking-[0.05em] text-muted uppercase">Agents — {{ agents.length }}</h3>
    <EmptyState v-if="!agents.length" compact bordered title="No agents yet" description="Use “Invite an agent” in the server menu." />
    <ul class="m-0 list-none p-0" data-testid="agents-list">
      <li v-for="m in agents" :key="m.id" class="flex items-center gap-2.5 border-b border-border py-2" :data-agent="m.handle">
        <MemberAvatar :member="m" size="sm" status /><span class="font-medium">{{ m.displayName }}</span><AgentBadge />
        <span class="flex items-center gap-1.5 text-xs text-muted"><StatusDot :state="m.online ? 'online' : 'offline'" />owned by {{ memberById(m.ownerId ?? 0)?.displayName }} · {{ m.online ? 'online' : m.connected ? 'offline' : 'not connected' }}</span>
        <span v-if="canAdminAgent(m.ownerId)" class="ml-auto flex gap-1.5">
          <Button size="sm" variant="outline" data-testid="edit-agent" @click="openModal('edit-agent', { id: m.id })">Edit</Button>
          <Button v-if="m.connected" size="sm" variant="outline" data-testid="disconnect-agent" @click="run(() => post(`/agents/${m.id}/disconnect`), { title: `Disconnect ${m.displayName}?`, body: 'Its runner’s token stops working.', confirmLabel: 'Disconnect' })">Disconnect</Button>
          <Button size="sm" variant="outline" class="text-error-text" data-testid="remove-agent" @click="run(() => del(`/agents/${m.id}`), { title: `Remove ${m.displayName}?`, body: 'The agent is removed from the server.', confirmLabel: 'Remove agent' })">Remove</Button>
        </span>
      </li>
    </ul>
    <ErrorNote v-if="error" :message="error" class="mt-3" />
  </AppDialog>
</template>
