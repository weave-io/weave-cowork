<script setup lang="ts">
import { cn } from '@/lib/utils'
// Fleet's status colours: running (green) for online, working pulses, waiting is amber, offline is quiet.
defineProps<{ state: 'online' | 'working' | 'waiting' | 'offline'; class?: string }>()
</script>
<template>
  <i :class="cn('inline-block size-2 shrink-0 rounded-full', state === 'online' && 'bg-running', state === 'working' && 'bg-running pulse', state === 'waiting' && 'bg-idle pulse', state === 'offline' && 'bg-muted/50', $props.class)" />
</template>
