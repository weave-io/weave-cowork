<script setup lang="ts">
import { computed } from 'vue'
import type { Member } from '../../types'
const props = defineProps<{ member?: Member | null; size?: 'xs' | 'sm' | 'md'; status?: boolean }>()
const colours = ['#D95A3A', '#0f9b8e', '#b4479a', '#0e7490', '#7c3aed', '#c27c0e', '#4f7f2a', '#2563eb']
const bg = computed(() => props.member?.kind === 'agent' ? 'var(--accent)' : colours[(props.member?.id ?? 0) % colours.length])
const initials = computed(() => (props.member?.displayName ?? '?').split(/\s+/).map(w => w[0]).slice(0, 2).join('').toUpperCase())
const px = computed(() => ({ xs: 20, sm: 26, md: 34 }[props.size ?? 'md']))
</script>
<template>
  <span class="relative grid place-items-center text-white font-bold shrink-0 select-none"
    :class="member?.kind === 'agent' ? (size === 'xs' ? 'rounded-md' : 'rounded-[10px]') : 'rounded-full'"
    :style="{ background: bg, width: px + 'px', height: px + 'px', fontSize: Math.round(px * 0.38) + 'px' }">
    {{ initials }}
    <span v-if="status && member?.kind === 'agent'" class="absolute -right-0.5 -bottom-0.5 w-[11px] h-[11px] rounded-full border-[2.5px] border-side"
      :class="member.online ? 'bg-online' : 'bg-offline'" />
  </span>
</template>
