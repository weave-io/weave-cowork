<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { Hash, Volume2 } from 'lucide-vue-next'
import { post, ApiError } from '../../api'
import { closeModal, loadBootstrap } from '../../store'
import FormField from '../FormField.vue'
import { Button } from '@/components/ui/button'
import { Switch } from '@/components/ui/switch'
import { ErrorNote } from '@/components/ui/error-note'
import { Spinner } from '@/components/ui/spinner'

const router = useRouter()
const name = ref(''), topic = ref(''), isPrivate = ref(false), kind = ref<'text' | 'voice'>('text'), error = ref(''), busy = ref(false)
async function create() {
  error.value = ''; busy.value = true
  try {
    const { id } = await post('/channels', { name: name.value, topic: topic.value, isPrivate: isPrivate.value, kind: kind.value })
    await loadBootstrap(); closeModal(); router.push(`/c/${id}`)
  } catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' } finally { busy.value = false }
}
</script>
<template>
  <form @submit.prevent="create">
    <div class="mb-3 grid grid-cols-2 gap-2" role="radiogroup" aria-label="Channel type">
      <Button type="button" role="radio" :aria-checked="kind === 'text'" :variant="kind === 'text' ? 'secondary' : 'outline'" class="justify-start" :class="kind === 'text' && 'border border-accent'" data-testid="kind-text" @click="kind = 'text'"><Hash :size="16" />Text</Button>
      <Button type="button" role="radio" :aria-checked="kind === 'voice'" :variant="kind === 'voice' ? 'secondary' : 'outline'" class="justify-start" :class="kind === 'voice' && 'border border-accent'" data-testid="kind-voice" @click="kind = 'voice'"><Volume2 :size="16" />Voice</Button>
    </div>
    <FormField name="name" label="Name" v-model="name" placeholder="checkout-app" />
    <FormField name="topic" label="Topic (optional)" v-model="topic" placeholder="What is this channel for?" />
    <label class="mb-4 flex cursor-pointer items-start gap-3 text-[13px]">
      <Switch v-model="isPrivate" class="mt-0.5" name="private" data-testid="private-switch" aria-label="Private" />
      <span><b class="font-medium">Private</b><br><span class="text-xs text-muted">Only the people and agents you add can see it. Public channels are open to everyone in the server.</span></span>
    </label>
    <ErrorNote v-if="error" :message="error" class="mb-3" data-testid="form-error" />
    <div class="flex justify-end gap-2"><Button variant="outline" @click="closeModal">Cancel</Button><Button type="submit" :disabled="busy || !name.trim()"><Spinner v-if="busy" size="sm" />Create channel</Button></div>
  </form>
</template>
