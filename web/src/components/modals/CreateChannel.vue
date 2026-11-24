<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { post, ApiError } from '../../api'
import { closeModal, loadBootstrap } from '../../store'
import Field from '../ui/Field.vue'
import Button from '../ui/Button.vue'

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
    <div class="flex gap-4 mb-3 text-[13px]" role="radiogroup" aria-label="Channel type">
      <label class="flex items-center gap-1.5 cursor-pointer"><input type="radio" v-model="kind" value="text" name="kind" /> # Text</label>
      <label class="flex items-center gap-1.5 cursor-pointer"><input type="radio" v-model="kind" value="voice" name="kind" data-testid="kind-voice" /> 🔊 Voice</label>
    </div>
    <Field name="name" label="Name" v-model="name" placeholder="checkout-app" />
    <Field name="topic" label="Topic (optional)" v-model="topic" placeholder="What is this channel for?" />
    <label class="flex items-start gap-2 text-[13px] mb-3 cursor-pointer"><input type="checkbox" v-model="isPrivate" class="mt-1" name="private" />
      <span><b>Private</b><br><span class="text-muted text-xs">Only the people and agents you add can see it. Public channels are open to everyone in the server.</span></span></label>
    <p v-if="error" role="alert" class="text-danger-ink text-[13px] mt-0">{{ error }}</p>
    <div class="flex justify-end gap-2"><Button @click="closeModal">Cancel</Button><Button type="submit" variant="primary" :disabled="busy || !name.trim()">Create channel</Button></div>
  </form>
</template>
