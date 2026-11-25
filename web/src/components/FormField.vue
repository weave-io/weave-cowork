<script setup lang="ts">
import { useId } from 'vue'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
defineProps<{ label: string; modelValue: string; type?: string; placeholder?: string; autocomplete?: string; hint?: string; textarea?: boolean; mono?: boolean; name?: string }>()
const emit = defineEmits<{ 'update:modelValue': [v: string] }>()
const id = useId()
</script>
<template>
  <div class="mb-3 text-left">
    <label :for="id" class="mb-1 block text-xs font-medium text-muted">{{ label }}</label>
    <Textarea v-if="textarea" :id="id" :name="name" :model-value="modelValue" rows="3" :placeholder="placeholder" class="resize-y text-sm" :class="mono && 'font-mono text-[12.5px]'"
      @update:model-value="emit('update:modelValue', String($event))" />
    <Input v-else :id="id" :name="name" :model-value="modelValue" :type="type ?? 'text'" :placeholder="placeholder" :autocomplete="autocomplete" class="text-sm" :class="mono && 'font-mono text-[12.5px]'"
      @update:model-value="emit('update:modelValue', String($event))" />
    <p v-if="hint" class="m-0 mt-1 text-xs text-muted">{{ hint }}</p>
  </div>
</template>
