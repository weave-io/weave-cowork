<script setup lang="ts">
import { kindLabel } from '@/artifacts'
// An artifact's kind as a small chip (HTML, MD, CSV …): on its card, its canvas tab and its own page.
defineProps<{ kind: string }>()
</script>
<template><span class="kind-chip" data-testid="kind-chip">{{ kindLabel(kind) }}</span></template>
