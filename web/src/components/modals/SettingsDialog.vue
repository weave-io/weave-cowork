<script setup lang="ts">
import { Check, Monitor } from 'lucide-vue-next'
import { closeModal, signOut, me } from '../../store'
import { selection, setTheme, themes } from '../../theme'
import AppDialog from '../AppDialog.vue'
import { Button } from '@/components/ui/button'
</script>
<template>
  <AppDialog title="Settings" description="Theme is remembered in this browser." @close="closeModal">
    <h3 class="m-0 mb-2 text-[11px] font-semibold tracking-[0.05em] text-muted uppercase">Theme</h3>
    <div class="grid grid-cols-2 gap-2" role="radiogroup" aria-label="Theme">
      <button type="button" role="radio" :aria-checked="selection === 'system'" class="theme-card" :class="{ on: selection === 'system' }" data-theme-card="system" @click="setTheme('system')">
        <Monitor :size="16" class="text-muted" aria-hidden="true" /><span class="flex-1 text-left text-[13px]">System</span><Check v-if="selection === 'system'" :size="14" class="text-accent-text" />
      </button>
      <button v-for="t in themes" :key="t.id" type="button" role="radio" :aria-checked="selection === t.id" class="theme-card" :class="{ on: selection === t.id }" :data-theme-card="t.id" @click="setTheme(t.id)">
        <span class="flex shrink-0 overflow-hidden rounded-full border border-border"><i v-for="c in t.swatches" :key="c" class="block size-3.5" :style="{ background: c }" /></span>
        <span class="flex-1 text-left text-[13px]">{{ t.label }}</span><Check v-if="selection === t.id" :size="14" class="text-accent-text" />
      </button>
    </div>
    <div class="mt-5 flex items-center justify-between border-t border-border pt-4 text-[13px]">
      <span class="text-muted">Signed in as <b class="font-medium text-text">{{ me?.displayName }}</b></span>
      <Button variant="outline" size="sm" @click="closeModal(); signOut()">Sign out</Button>
    </div>
  </AppDialog>
</template>
<style scoped>
.theme-card { display: flex; align-items: center; gap: 10px; padding: 8px 10px; border: 1px solid var(--border); border-radius: var(--radius-btn); background: var(--card-bg); color: var(--text); cursor: pointer; transition: border-color var(--transition), background var(--transition); }
.theme-card:hover { background: color-mix(in srgb, var(--text) 5%, var(--card-bg)); }
.theme-card.on { border-color: var(--accent); background: var(--accent-dim); }
</style>
