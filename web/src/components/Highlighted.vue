<script setup lang="ts">
import { computed } from 'vue'
import { highlight } from '../search'

// A search snippet: the words that matched in <mark>, the rest as text (never HTML).
const props = defineProps<{ text: string }>()
const parts = computed(() => highlight(props.text))
</script>
<template><span><template v-for="(p, i) in parts" :key="i"><mark v-if="p.hit">{{ p.text }}</mark><template v-else>{{ p.text }}</template></template></span></template>
