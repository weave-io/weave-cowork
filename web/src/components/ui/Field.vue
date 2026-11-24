<script setup lang="ts">
defineProps<{ label: string; modelValue: string; type?: string; placeholder?: string; autocomplete?: string; hint?: string; textarea?: boolean; mono?: boolean; name?: string }>()
const emit = defineEmits<{ 'update:modelValue': [v: string] }>()
</script>
<template>
  <div class="mb-3 text-left">
    <label class="block text-xs font-semibold text-muted mb-1">{{ label }}
      <textarea v-if="textarea" :name="name" :value="modelValue" rows="3" :placeholder="placeholder"
        class="mt-1 w-full border border-line bg-card rounded-btn px-3 py-2 text-ink font-normal text-sm resize-y" :class="mono && 'font-mono text-[12.5px]'"
        @input="emit('update:modelValue', ($event.target as HTMLTextAreaElement).value)" />
      <input v-else :name="name" :value="modelValue" :type="type ?? 'text'" :placeholder="placeholder" :autocomplete="autocomplete"
        class="mt-1 w-full border border-line bg-card rounded-btn px-3 py-2 text-ink font-normal text-sm" :class="mono && 'font-mono text-[12.5px]'"
        @input="emit('update:modelValue', ($event.target as HTMLInputElement).value)" />
    </label>
    <p v-if="hint" class="text-xs text-muted mt-1 mb-0">{{ hint }}</p>
  </div>
</template>
