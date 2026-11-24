<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { get, put, ApiError } from '../../api'
import { closeModal, loadBootstrap } from '../../store'
import Modal from '../ui/Modal.vue'
import Field from '../ui/Field.vue'
import Button from '../ui/Button.vue'

const props = defineProps<{ agentId: number }>()
const handle = ref(''), displayName = ref(''), about = ref(''), instructions = ref(''), harness = ref(''), workspace = ref('')
const error = ref(''), busy = ref(false), loaded = ref(false), saved = ref(false)

onMounted(async () => {
  try {
    const a = await get(`/agents/${props.agentId}`)
    handle.value = a.handle; displayName.value = a.displayName; about.value = a.about
    instructions.value = a.instructions; harness.value = a.harnessCommand; workspace.value = a.workspace ?? ''
    loaded.value = true
  } catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' }
})
async function save() {
  error.value = ''; busy.value = true
  try {
    await put(`/agents/${props.agentId}`, { displayName: displayName.value, about: about.value, instructions: instructions.value, harnessCommand: harness.value, workspace: workspace.value })
    await loadBootstrap(); saved.value = true
  } catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' } finally { busy.value = false }
}
</script>
<template>
  <Modal :title="`Edit @${handle || 'agent'}`" wide @close="closeModal">
    <form v-if="loaded" @submit.prevent="save">
      <p class="mt-0 text-[13px] text-muted">Changes apply to new threads. Threads that already have a session keep the settings they started with.</p>
      <Field name="displayName" label="Display name" v-model="displayName" />
      <Field name="about" label="About" v-model="about" />
      <Field name="instructions" label="Instructions" v-model="instructions" textarea />
      <Field name="harness" label="Harness command" v-model="harness" mono hint="Any ACP agent: opencode acp, copilot --acp, …" />
      <Field name="workspace" label="Workspace folder" v-model="workspace" mono hint="A folder on the runner's machine." />
      <p v-if="error" role="alert" class="text-danger-ink text-[13px] mt-0">{{ error }}</p>
      <p v-if="saved" class="text-[13px] mt-0" data-testid="agent-saved">Saved. New threads will use these settings.</p>
      <div class="flex justify-end gap-2"><Button @click="closeModal">{{ saved ? 'Close' : 'Cancel' }}</Button><Button type="submit" variant="primary" data-testid="save-agent" :disabled="busy || !harness.trim() || !workspace.trim()">Save</Button></div>
    </form>
    <p v-else-if="error" role="alert" class="text-danger-ink text-[13px]">{{ error }}</p>
  </Modal>
</template>
