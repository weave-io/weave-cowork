<script setup lang="ts">
import { computed, ref } from 'vue'
import { put, del, post, ApiError } from '../../api'
import { store, closeModal, loadBootstrap, memberById, openModal } from '../../store'
import Modal from '../ui/Modal.vue'
import Button from '../ui/Button.vue'
import Avatar from '../ui/Avatar.vue'
import AgentBadge from '../ui/AgentBadge.vue'

const people = computed(() => store.members.filter(m => m.kind === 'person' && !m.removed))
const agents = computed(() => store.members.filter(m => m.kind === 'agent' && !m.removed))
const error = ref('')
async function run(fn: () => Promise<unknown>, confirmText?: string) {
  if (confirmText && !confirm(confirmText)) return
  error.value = ''
  try { await fn(); await loadBootstrap() } catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' }
}
const canAdminAgent = (ownerId: number | null) => store.isAdmin || ownerId === store.meId
</script>
<template>
  <Modal title="People and agents" wide @close="closeModal">
    <h3 class="text-[13px] font-semibold mt-0 mb-1">People — {{ people.length }}</h3>
    <ul class="list-none p-0 m-0 mb-4" data-testid="people-list">
      <li v-for="m in people" :key="m.id" class="flex items-center gap-2.5 py-1.5 border-b border-line" :data-person="m.handle">
        <Avatar :member="m" size="sm" /><span class="font-medium">{{ m.displayName }}</span><span class="text-muted text-xs">@{{ m.handle }}</span>
        <span class="text-xs px-1.5 rounded border border-line" :class="m.role === 'admin' && 'text-accent-ink border-accent'">{{ m.role }}</span>
        <span v-if="store.isAdmin && m.id !== store.meId" class="ml-auto flex gap-1.5">
          <Button small data-testid="toggle-admin" @click="run(() => put(`/people/${m.id}/role`, { role: m.role === 'admin' ? 'member' : 'admin' }))">{{ m.role === 'admin' ? 'Make member' : 'Make admin' }}</Button>
          <Button small variant="danger" data-testid="remove-person" @click="run(() => del(`/people/${m.id}`), `Remove ${m.displayName} from ${store.serverName}? Their agents go too.`)">Remove</Button>
        </span>
      </li>
    </ul>
    <h3 class="text-[13px] font-semibold mb-1">Agents — {{ agents.length }}</h3>
    <p v-if="!agents.length" class="text-xs text-muted m-0">No agents yet. Use “Invite an agent” in the server menu.</p>
    <ul class="list-none p-0 m-0" data-testid="agents-list">
      <li v-for="m in agents" :key="m.id" class="flex items-center gap-2.5 py-1.5 border-b border-line" :data-agent="m.handle">
        <Avatar :member="m" size="sm" status /><span class="font-medium">{{ m.displayName }}</span><AgentBadge />
        <span class="text-muted text-xs">owned by {{ memberById(m.ownerId ?? 0)?.displayName }} · {{ m.online ? 'online' : m.connected ? 'offline' : 'not connected' }}</span>
        <span v-if="canAdminAgent(m.ownerId)" class="ml-auto flex gap-1.5">
          <Button small data-testid="edit-agent" @click="openModal('edit-agent', { id: m.id })">Edit</Button>
          <Button v-if="m.connected" small data-testid="disconnect-agent" @click="run(() => post(`/agents/${m.id}/disconnect`), `Disconnect ${m.displayName}? Its runner's token stops working.`)">Disconnect</Button>
          <Button small variant="danger" data-testid="remove-agent" @click="run(() => del(`/agents/${m.id}`), `Remove ${m.displayName} entirely?`)">Remove</Button>
        </span>
      </li>
    </ul>
    <p v-if="error" role="alert" class="text-danger-ink text-[13px] mt-3 mb-0">{{ error }}</p>
  </Modal>
</template>
