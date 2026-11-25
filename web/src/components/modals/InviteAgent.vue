<script setup lang="ts">
import { computed, ref } from 'vue'
import { post, put, ApiError } from '../../api'
import { store, closeModal, loadBootstrap } from '../../store'
import { Copy, Check, Hash, Lock } from 'lucide-vue-next'
import AppDialog from '../AppDialog.vue'
import Field from '../FormField.vue'
import StatusDot from '../StatusDot.vue'
import { Button } from '@/components/ui/button'
import { ErrorNote } from '@/components/ui/error-note'
import { Spinner } from '@/components/ui/spinner'

const handle = ref(''), about = ref(''), instructions = ref(''), harness = ref('opencode acp'), workspace = ref('')
// where it runs: a runner its owner connects (pinned), or a pool (a team template: a sandbox per thread; admins only, it spends the server's models)
const placement = ref<'runner' | 'pool'>('runner'), image = ref('cowork-sandbox-opencode:2.4.1'), model = ref('claude-haiku-4.5')
const error = ref(''), busy = ref(false)
const created = ref<{ agentId: number; handle: string; code?: string; command?: string; template?: boolean } | null>(null)
const ready = computed(() => !!handle.value.trim() && (placement.value === 'pool' ? !!model.value.trim() && !!image.value.trim() : !!workspace.value.trim()))
const agent = computed(() => store.members.find(m => m.id === created.value?.agentId))
const online = computed(() => !!agent.value?.online)
const picked = ref<number[]>([])
const joined = ref(false)
const copied = ref(false)

async function create() {
  error.value = ''; busy.value = true
  try {
    created.value = await post('/agents', placement.value === 'pool'
      ? { handle: handle.value, about: about.value, instructions: instructions.value, harnessCommand: harness.value, placement: 'pool', image: image.value, model: model.value }
      : { handle: handle.value, about: about.value, instructions: instructions.value, harnessCommand: harness.value, workspace: workspace.value })
    if (created.value?.template) await loadBootstrap()
  }
  catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' } finally { busy.value = false }
}
async function newCode() { if (created.value) created.value = await post(`/agents/${created.value.agentId}/code`) }
async function copy() { try { await navigator.clipboard.writeText(created.value!.command ?? ''); copied.value = true } catch { /* http */ } }
async function join() {
  for (const id of picked.value) await put(`/channels/${id}/members/${created.value!.agentId}`)
  await loadBootstrap(); joined.value = true
}
</script>
<template>
  <AppDialog :title="placement === 'pool' ? 'Make a team template' : 'Invite an agent'" wide @close="closeModal">
    <form v-if="!created" @submit.prevent="create">
      <div v-if="store.isAdmin" class="mb-3">
        <div class="mb-1 text-xs font-medium text-muted">Runs on</div>
        <div class="inline-flex gap-0.5 rounded-btn border border-border p-0.5" role="radiogroup" aria-label="Runs on">
          <button type="button" role="radio" :aria-checked="placement === 'runner'" class="seg" :class="{ on: placement === 'runner' }" data-testid="runs-on-runner" @click="placement = 'runner'">A runner I connect</button>
          <button type="button" role="radio" :aria-checked="placement === 'pool'" class="seg" :class="{ on: placement === 'pool' }" data-testid="runs-on-pool" @click="placement = 'pool'">A pool</button>
        </div>
      </div>
      <p v-if="placement === 'runner'" class="mt-0 text-[13px] text-muted">An agent is a harness (like OpenCode) running on a machine you control. It takes part in threads, and each thread is one harness session. You own it: its permission requests come to you.</p>
      <p v-else class="mt-0 text-[13px] text-muted" data-testid="template-explainer">A team template. Nobody connects it: when someone asks it in a project where it's enabled, a pool starts a sandbox for that thread. It uses the server's models through CoWork's gateway, and whoever started the thread answers its asks.</p>
      <Field name="handle" label="Name" v-model="handle" :placeholder="placement === 'pool' ? 'researcher' : 'reviewer'" hint="People mention it with @name." />
      <Field name="about" label="About" v-model="about" placeholder="Reviews pull requests for the checkout app" />
      <Field name="instructions" label="Instructions" v-model="instructions" textarea placeholder="You review code. Be concise, and say what you checked." />
      <Field name="harness" label="Harness command" v-model="harness" mono hint="Any ACP agent: opencode acp, copilot --acp, …" />
      <template v-if="placement === 'pool'">
        <Field name="image" label="Sandbox image" v-model="image" mono hint="What each thread's sandbox starts from: the runner and the harness at a pinned version." />
        <Field name="model" label="Model" v-model="model" mono hint="What it asks for through CoWork's gateway (Server settings → Models)." />
      </template>
      <Field v-else name="workspace" label="Workspace folder" v-model="workspace" mono placeholder="/home/alex/src/checkout-app" hint="A folder on the runner's machine." />
      <ErrorNote v-if="error" :message="error" class="mb-3" />
      <div class="flex justify-end"><Button type="submit" data-testid="create-agent" :disabled="busy || !ready"><Spinner v-if="busy" size="sm" />{{ placement === 'pool' ? 'Create template' : 'Create agent' }}</Button></div>
    </form>

    <div v-else-if="created.template" data-testid="template-created">
      <p class="mt-0 flex items-center gap-2 font-semibold"><StatusDot :state="online ? 'online' : 'offline'" /> {{ created.handle }} is a team template</p>
      <p class="text-[13px] text-muted">Enable it in a project's settings (Add an agent). From then on, mentioning <b>@{{ created.handle }}</b> in that project starts a sandbox for the thread on {{ online ? 'a pool' : 'a pool, once one is connected (Where agents run → Add a pool)' }}.</p>
      <div class="flex justify-end"><Button @click="closeModal">Close</Button></div>
    </div>

    <template v-else>
      <div v-if="!online">
        <p class="mt-0 text-[13px]">On the machine where <b>{{ created.handle }}</b> should work, run this. The code works once and expires in 10 minutes.</p>
        <div class="flex items-center gap-2 rounded-btn border border-border bg-card-bg p-2"><code class="flex-1 bg-transparent" data-testid="runner-command">{{ created.command }}</code><Button size="sm" variant="outline" @click="copy"><Check v-if="copied" :size="14" /><Copy v-else :size="14" />{{ copied ? 'Copied' : 'Copy' }}</Button></div>
        <p class="mt-3 mb-2 flex items-start gap-2 text-[13px] text-muted"><StatusDot state="waiting" class="mt-1.5" /><span>Waiting for the runner to connect… It stays connected while it runs. Next time, <code>cowork-runner run</code> serves every agent saved on that machine.</span></p>
        <Button size="sm" variant="outline" @click="newCode">Get a new code</Button>
      </div>
      <div v-else>
        <p class="mt-0 flex items-center gap-2 font-semibold" data-testid="agent-connected"><StatusDot state="online" /> {{ agent?.handle }} is connected</p>
        <template v-if="!joined">
          <p class="text-[13px] text-muted">Which channels should {{ agent?.handle }} join?</p>
          <label v-for="c in store.channels" :key="c.id" class="flex cursor-pointer items-center gap-2 py-1 text-[13px]">
            <input type="checkbox" class="size-4 accent-(--accent)" :value="c.id" v-model="picked" :data-pick="c.name" />
            <Lock v-if="c.isPrivate" :size="13" class="text-muted" aria-label="Private" /><Hash v-else :size="13" class="text-muted" aria-hidden="true" />{{ c.name }}
          </label>
          <div class="mt-3 flex justify-end gap-2"><Button variant="outline" @click="closeModal">Skip</Button><Button data-testid="join-channels" @click="join">Add to channels</Button></div>
        </template>
        <template v-else>
          <p class="text-[13px] text-muted">Done. Mention <b>@{{ agent?.handle }}</b> in one of those channels to start a thread with it.</p>
          <div class="flex justify-end"><Button @click="closeModal">Close</Button></div>
        </template>
      </div>
    </template>
  </AppDialog>
</template>
<style scoped>
.seg { border-radius: 6px; padding: 4px 10px; font-size: 12.5px; color: var(--muted); }
.seg:hover { color: var(--text); }
.seg.on { background: var(--panel-bg); color: var(--text); font-weight: 500; box-shadow: 0 1px 2px rgb(0 0 0 / 0.12); }
</style>
