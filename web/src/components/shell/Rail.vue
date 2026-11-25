<script setup lang="ts">
import { computed } from 'vue'
import { Server, Users, Settings } from 'lucide-vue-next'
import weaveLogo from '@/assets/weave_logo.png'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { store, openModal } from '../../store'
import { navOpen } from '../../layout'
import type { Component } from 'vue'


// Fleet's icon rail: the logo, icon buttons with tooltips, Settings at the bottom.
const items = computed<{ id: string; label: string; icon: Component; active?: boolean; run: () => void }[]>(() => [
  { id: 'server', label: store.serverName, icon: Server, active: true, run: () => { navOpen.value = !navOpen.value } },
  { id: 'people', label: store.isAdmin ? 'People & agents' : 'People', icon: Users, run: () => openModal('people') },
])
</script>
<template>
  <aside class="flex w-12 min-w-12 flex-col items-center py-2" aria-label="Primary navigation">
    <img :src="weaveLogo" alt="Weave" class="mb-1 size-8 object-contain" data-testid="rail-logo">
    <nav class="flex w-full flex-1 flex-col items-center" aria-label="App sections">
      <Tooltip v-for="i in items" :key="i.id">
        <TooltipTrigger as-child>
          <button type="button" :aria-label="i.label" :data-testid="`rail-${i.id}`" class="rail-item" :class="{ active: i.active }" @click="i.run">
            <component :is="i.icon" :size="18" aria-hidden="true" />
          </button>
        </TooltipTrigger>
        <TooltipContent side="right">{{ i.label }}</TooltipContent>
      </Tooltip>
    </nav>
    <Tooltip>
      <TooltipTrigger as-child>
        <button type="button" aria-label="Settings" data-testid="rail-settings" class="rail-item" @click="openModal('settings')"><Settings :size="18" aria-hidden="true" /></button>
      </TooltipTrigger>
      <TooltipContent side="right">Settings</TooltipContent>
    </Tooltip>
  </aside>
</template>
<style>
.rail-item { width: 40px; height: 40px; display: flex; align-items: center; justify-content: center; border-radius: var(--radius-btn); color: var(--muted); cursor: pointer; margin-bottom: 2px; border: 1px solid transparent; background: transparent; padding: 0; transition: background var(--transition), color var(--transition); }
.rail-item:hover, .rail-item:focus-visible { background: color-mix(in srgb, var(--text) 5%, transparent); color: var(--text); }
.rail-item.active { color: var(--text); background: color-mix(in srgb, var(--text) 9%, transparent); }
</style>
