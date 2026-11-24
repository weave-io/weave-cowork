<script setup lang="ts">
import { computed } from 'vue'
import { store, currentChannel, channelMembers, memberById, openModal } from '../store'
import Avatar from './ui/Avatar.vue'
import AgentBadge from './ui/AgentBadge.vue'

const members = computed(() => channelMembers(currentChannel.value))
const people = computed(() => members.value.filter(m => m.kind === 'person'))
const agents = computed(() => members.value.filter(m => m.kind === 'agent'))
</script>
<template>
  <aside class="bg-side border-l border-line overflow-auto px-2 py-3" aria-label="Members" data-testid="member-list">
    <div class="text-[11px] font-bold text-muted px-2 pb-1 tracking-wide">PEOPLE — {{ people.length }}</div>
    <div v-for="m in people" :key="m.id" class="flex items-center gap-2.5 px-2 py-1.5 rounded-btn">
      <Avatar :member="m" />
      <span class="min-w-0"><span class="font-medium text-[13.5px] block">{{ m.displayName }}<span v-if="m.id === store.meId" class="text-muted font-normal"> (you)</span></span>
        <span class="text-[11.5px] text-muted block">@{{ m.handle }}{{ m.role === 'admin' ? ' · admin' : '' }}</span></span>
    </div>
    <div class="text-[11px] font-bold text-muted px-2 pt-4 pb-1 tracking-wide">AGENTS — {{ agents.length }}</div>
    <div v-for="m in agents" :key="m.id" class="flex items-center gap-2.5 px-2 py-1.5 rounded-btn" :class="!m.online && 'opacity-60'" :data-agent="m.handle">
      <Avatar :member="m" status />
      <span class="min-w-0"><span class="font-medium text-[13.5px] flex items-center gap-1.5">{{ m.displayName }} <AgentBadge /></span>
        <span class="text-[11.5px] text-muted block truncate" data-testid="agent-sub">{{ m.online ? 'online' : 'offline' }} · owned by {{ memberById(m.ownerId ?? 0)?.displayName }}</span></span>
    </div>
    <p v-if="!agents.length" class="text-xs text-muted px-2 m-0">No agents here yet.</p>
    <button class="mt-3 mx-2 text-[12.5px] text-accent-ink bg-transparent border-0 cursor-pointer p-0 hover:underline" @click="openModal('invite-agent')">+ Invite an agent</button>
  </aside>
</template>
