<script setup lang="ts">
import { computed, ref } from 'vue'
import { post, put, ApiError } from '../../api'
import { store, closeModal, loadBootstrap } from '../../store'
import Modal from '../ui/Modal.vue'
import Field from '../ui/Field.vue'
import Button from '../ui/Button.vue'

const handle = ref(''), about = ref(''), instructions = ref(''), harness = ref('opencode acp'), workspace = ref('')
const error = ref(''), busy = ref(false)
const created = ref<{ agentId: number; code: string; command: string } | null>(null)
const agent = computed(() => store.members.find(m => m.id === created.value?.agentId))
const online = computed(() => !!agent.value?.online)
const picked = ref<number[]>([])
const joined = ref(false)
const copied = ref(false)

async function create() {
  error.value = ''; busy.value = true
  try { created.value = await post('/agents', { handle: handle.value, about: about.value, instructions: instructions.value, harnessCommand: harness.value, workspace: workspace.value }) }
  catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' } finally { busy.value = false }
}
async function newCode() { if (created.value) created.value = await post(`/agents/${created.value.agentId}/code`) }
async function copy() { try { await navigator.clipboard.writeText(created.value!.command); copied.value = true } catch { /* http */ } }
async function join() {
  for (const id of picked.value) await put(`/channels/${id}/members/${created.value!.agentId}`)
  await loadBootstrap(); joined.value = true
}
</script>
<template>
  <Modal title="Invite an agent" wide @close="closeModal">
    <form v-if="!created" @submit.prevent="create">
      <p class="mt-0 text-[13px] text-muted">An agent is a harness (like OpenCode) running on a machine you control. It takes part in threads, and each thread is one harness session. You own it: its permission requests come to you.</p>
      <Field name="handle" label="Name" v-model="handle" placeholder="reviewer" hint="People mention it with @name." />
      <Field name="about" label="About" v-model="about" placeholder="Reviews pull requests for the checkout app" />
      <Field name="instructions" label="Instructions" v-model="instructions" textarea placeholder="You review code. Be concise, and say what you checked." />
      <Field name="harness" label="Harness command" v-model="harness" mono hint="Any ACP agent: opencode acp, copilot --acp, …" />
      <Field name="workspace" label="Workspace folder" v-model="workspace" mono placeholder="/home/alex/src/checkout-app" hint="A folder on the runner's machine." />
      <p v-if="error" role="alert" class="text-danger-ink text-[13px] mt-0">{{ error }}</p>
      <div class="flex justify-end"><Button type="submit" variant="primary" :disabled="busy || !handle.trim() || !workspace.trim()">Create agent</Button></div>
    </form>

    <template v-else>
      <div v-if="!online">
        <p class="mt-0 text-[13px]">On the machine where <b>{{ agent?.handle }}</b> should work, run this. The code works once and expires in 10 minutes.</p>
        <div class="flex gap-2 items-center bg-card border border-line rounded-btn p-2"><code class="flex-1" data-testid="runner-command">{{ created.command }}</code><Button small @click="copy">{{ copied ? 'Copied' : 'Copy' }}</Button></div>
        <p class="text-[13px] text-muted mt-3 flex items-center gap-2"><i class="w-2 h-2 rounded-full bg-away pulse inline-block" /> Waiting for the runner to connect… It stays connected while it runs; next time, <code>cowork-runner run</code> serves every agent saved on that machine.</p>
        <Button small @click="newCode">Get a new code</Button>
      </div>
      <div v-else>
        <p class="mt-0 flex items-center gap-2 font-semibold" data-testid="agent-connected"><i class="w-2 h-2 rounded-full bg-online inline-block" /> {{ agent?.handle }} is connected</p>
        <template v-if="!joined">
          <p class="text-[13px] text-muted">Which channels should {{ agent?.handle }} join?</p>
          <label v-for="c in store.channels" :key="c.id" class="flex items-center gap-2 py-1 cursor-pointer text-[13px]">
            <input type="checkbox" :value="c.id" v-model="picked" :data-pick="c.name" /> {{ c.isPrivate ? '🔒' : '#' }}{{ c.name }}
          </label>
          <div class="flex justify-end gap-2 mt-3"><Button @click="closeModal">Skip</Button><Button variant="primary" data-testid="join-channels" @click="join">Add to channels</Button></div>
        </template>
        <template v-else>
          <p class="text-[13px] text-muted">Done. Mention <b>@{{ agent?.handle }}</b> in one of those channels to start a thread with it.</p>
          <div class="flex justify-end"><Button variant="primary" @click="closeModal">Close</Button></div>
        </template>
      </div>
    </template>
  </Modal>
</template>
