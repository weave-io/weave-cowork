<script setup lang="ts">
withDefaults(defineProps<{ variant?: 'default' | 'primary' | 'danger'; small?: boolean; type?: 'button' | 'submit'; disabled?: boolean }>(), { variant: 'default', type: 'button' })
</script>
<template>
  <button :type="type" :disabled="disabled"
    class="inline-flex items-center justify-center gap-1.5 border rounded-btn font-medium transition-colors disabled:opacity-50 disabled:cursor-not-allowed cursor-pointer"
    :class="[
      small ? 'px-2.5 py-0.5 text-[12.5px]' : 'px-3 py-1.5 text-[13px]',
      variant === 'primary' ? 'bg-accent border-accent text-white hover:brightness-110'
        : variant === 'danger' ? 'bg-raised border-line text-danger-ink hover:bg-hover'
        : 'bg-raised border-line hover:bg-hover']">
    <slot />
  </button>
</template>
