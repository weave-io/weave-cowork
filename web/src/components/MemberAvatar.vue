<script setup lang="ts">
import { computed } from 'vue'
import type { Member } from '../types'
import { cn } from '@/lib/utils'
import { avatarColor } from '../avatarColors'

// A person is a circle, an agent a rounded square (so you can tell them apart without colour). Everyone has their own stable
// colour (avatarColors.ts); the dot on an agent is its status, in Fleet's status colours.
const props = defineProps<{ member?: Member | null; size?: 'xs' | 'sm' | 'md' | 'lg'; status?: boolean; class?: string }>()
const bg = computed(() => avatarColor(props.member?.id ?? 0))
const initials = computed(() => (props.member?.displayName ?? '?').split(/\s+/).map(w => w[0]).slice(0, 2).join('').toUpperCase())
const px = computed(() => ({ xs: 20, sm: 26, md: 32, lg: 56 }[props.size ?? 'md']))
</script>
<template>
  <span :class="cn('relative grid shrink-0 select-none place-items-center font-semibold text-white', member?.kind === 'agent' ? (size === 'xs' ? 'rounded-[6px]' : 'rounded-btn') : 'rounded-full', props.class)"
    :style="{ background: bg, width: px + 'px', height: px + 'px', fontSize: Math.round(px * 0.4) + 'px' }">
    {{ initials }}
    <span v-if="status && member?.kind === 'agent'" class="absolute -right-[3px] -bottom-[3px] size-[11px] rounded-full border-2 border-panel-bg"
      :class="member.online ? 'bg-running' : 'bg-muted/60'" />
  </span>
</template>
