<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { get, put, ApiError } from '../../api'
import { closeModal, loadBootstrap } from '../../store'
import AppDialog from '../AppDialog.vue'
import Field from '../FormField.vue'
import { Button } from '@/components/ui/button'
import { Switch } from '@/components/ui/switch'
import { ErrorNote } from '@/components/ui/error-note'

const props = defineProps<{ agentId: number }>()
const handle = ref(''), displayName = ref(''), about = ref(''), instructions = ref(''), harness = ref(''), workspace = ref('')
const recordOutput = ref(false)
const error = ref(''), busy = ref(false), loaded = ref(false), saved = ref(false)

onMounted(async () => {
  try {
    const a = await get(`/agents/${props.agentId}`)
    handle.value = a.handle; displayName.value = a.displayName; about.value = a.about
    instructions.value = a.instructions; harness.value = a.harnessCommand; workspace.value = a.workspace ?? ''; recordOutput.value = !!a.recordToolOutput
    loaded.value = true
  } catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' }
})
async function save() {
  error.value = ''; busy.value = true
  try {
    await put(`/agents/${props.agentId}`, { displayName: displayName.value, about: about.value, instructions: instructions.value, harnessCommand: harness.value, workspace: workspace.value, recordToolOutput: recordOutput.value })
    await loadBootstrap(); saved.value = true
  } catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' } finally { busy.value = false }
}
</script>
<template>
  <AppDialog :title="`Edit @${handle || 'agent'}`" wide @close="closeModal">
    <form v-if="loaded" @submit.prevent="save">
      <p class="mt-0 text-[13px] text-muted">Changes apply to new threads. Threads that already have a session keep the settings they started with.</p>
      <Field name="displayName" label="Display name" v-model="displayName" />
      <Field name="about" label="About" v-model="about" />
      <Field name="instructions" label="Instructions" v-model="instructions" textarea />
      <Field name="harness" label="Harness command" v-model="harness" mono hint="Any ACP agent: opencode acp, copilot --acp, …" />
      <Field name="workspace" label="Workspace folder" v-model="workspace" mono hint="A folder on the runner's machine." />
      <div class="mb-4 flex items-start gap-3">
        <Switch :model-value="recordOutput" data-testid="record-tool-output" aria-label="Record full tool output" @update:model-value="recordOutput = $event" />
        <div class="text-[13px]">
          <div class="font-medium text-foreground">Record full tool output</div>
          <div class="text-muted">Off: CoWork keeps what each tool was asked to do, how much it printed and a fingerprint of it. On: it also keeps the text (up to 64 kB per call), which can include secrets from the runner's machine.</div>
        </div>
      </div>
      <ErrorNote v-if="error" :message="error" class="mb-3" />
      <p v-if="saved" class="mt-0 text-[13px] text-running-text" data-testid="agent-saved">Saved. New threads will use these settings.</p>
      <div class="flex justify-end gap-2"><Button variant="outline" @click="closeModal">{{ saved ? 'Close' : 'Cancel' }}</Button><Button type="submit" data-testid="save-agent" :disabled="busy || !harness.trim() || !workspace.trim()">Save</Button></div>
    </form>
    <ErrorNote v-else-if="error" :message="error" />
  </AppDialog>
</template>
