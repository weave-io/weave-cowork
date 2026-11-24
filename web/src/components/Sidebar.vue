<script setup lang="ts">
import { ref } from 'vue'
import { store, me, openModal, signOut } from '../store'
import Avatar from './ui/Avatar.vue'

defineProps<{ channelId: number | null }>()
const emit = defineEmits<{ go: [id: number] }>()
const menu = ref(false)
function toggleTheme() {
  const dark = document.documentElement.dataset.theme === 'dark' || (!document.documentElement.dataset.theme && matchMedia('(prefers-color-scheme: dark)').matches)
  document.documentElement.dataset.theme = dark ? 'light' : 'dark'
  localStorage.setItem('theme', document.documentElement.dataset.theme!)
}
const saved = localStorage.getItem('theme'); if (saved) document.documentElement.dataset.theme = saved
function pick(name: string) { menu.value = false; openModal(name) }
</script>
<template>
  <nav class="bg-side border-r border-line flex flex-col min-w-0 min-h-0" aria-label="Channels">
    <div class="relative">
      <button class="w-full px-3.5 py-3.5 border-0 border-b border-line bg-transparent flex items-center justify-between font-bold cursor-pointer text-left" data-testid="server-menu" @click="menu = !menu">
        <span class="truncate">{{ store.serverName }}</span><span class="text-muted text-xs">▾</span>
      </button>
      <div v-if="menu" class="absolute z-20 left-2 right-2 top-[52px] bg-raised border border-line rounded-card shadow-lg p-1" @click="menu = false">
        <button v-if="store.isAdmin" class="menu-item" data-testid="menu-invite-people" @click="pick('invite-people')">Invite people</button>
        <button class="menu-item" data-testid="menu-invite-agent" @click="pick('invite-agent')">Invite an agent</button>
        <button class="menu-item" data-testid="menu-people" @click="pick('people')">{{ store.isAdmin ? 'People and agents' : 'People' }}</button>
      </div>
    </div>
    <div class="flex-1 overflow-auto p-2">
      <div class="flex justify-between items-center text-[11px] font-bold text-muted px-2 pt-3 pb-1 tracking-wide">
        CHANNELS
        <button class="w-5 h-5 grid place-items-center rounded text-muted hover:bg-hover hover:text-ink bg-transparent border-0 cursor-pointer text-base leading-none" aria-label="Create channel" data-testid="create-channel" @click="openModal('create-channel')">+</button>
      </div>
      <button v-for="c in store.channels" :key="c.id" class="flex items-center gap-2 w-full border-0 text-left px-2 py-1.5 rounded-btn cursor-pointer transition-colors"
        :class="c.id === channelId ? 'bg-accent-dim text-ink font-semibold' : 'bg-transparent text-muted hover:bg-hover hover:text-ink'" :data-channel="c.name" @click="emit('go', c.id)">
        <span class="w-4 text-center opacity-80">{{ c.isPrivate ? '🔒' : '#' }}</span><span class="truncate">{{ c.name }}</span>
      </button>
    </div>
    <div class="border-t border-line px-3 py-2.5 flex items-center gap-2.5 text-[13px]">
      <Avatar :member="me" size="sm" />
      <span class="min-w-0 truncate font-medium" data-testid="me-name">{{ me?.displayName }}</span>
      <span class="ml-auto flex gap-0.5">
        <button class="w-7 h-7 grid place-items-center rounded-btn text-muted hover:bg-hover bg-transparent border-0 cursor-pointer" aria-label="Toggle theme" title="Toggle theme" @click="toggleTheme">◐</button>
        <button class="px-2 h-7 rounded-btn text-muted hover:bg-hover bg-transparent border-0 cursor-pointer text-xs" data-testid="sign-out" @click="signOut">Sign out</button>
      </span>
    </div>
  </nav>
</template>
<style scoped>
.menu-item { display: block; width: 100%; text-align: left; background: none; border: 0; padding: 7px 10px; border-radius: 8px; cursor: pointer; font-size: 13px; }
.menu-item:hover { background: var(--hover); }
</style>
