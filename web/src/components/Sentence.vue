<script setup lang="ts">
import { computed } from 'vue'
// An audit sentence: words, with `code` between backticks (a command, a file) set in the monospace face.
const props = defineProps<{ text: string }>()
const parts = computed(() => props.text.split('`').map((t, i) => ({ t, code: i % 2 === 1 })))
</script>
<template><span><template v-for="(p, i) in parts" :key="i"><code v-if="p.code" class="rounded-[5px] bg-text/8 px-1 py-px font-mono text-[12.5px]">{{ p.t }}</code><template v-else>{{ p.t }}</template></template></span></template>
