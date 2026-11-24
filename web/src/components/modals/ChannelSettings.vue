<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { patch, put, del, ApiError } from '../../api'
import { store, closeModal, loadBootstrap } from '../../store'
import Modal from '../ui/Modal.vue'
import Field from '../ui/Field.vue'
import Button from '../ui/Button.vue'
import Avatar from '../ui/Avatar.vue'
import AgentBadge from '../ui/AgentBadge.vue'

const props = defineProps<{ channelId: number }>()
const router = useRouter()
const ch = computed(() => store.channels.find(c => c.id === props.channelId))
const name = ref(ch.value?.name ?? ''), topic = ref(ch.value?.topic ?? '')
const error = ref(''), saved = ref(false), confirmDelete = ref(false)
const inChannel = computed(() => store.members.filter(m => !m.removed && ch.value?.memberIds.includes(m.id)))
const addable = computed(() => store.members.filter(m => !m.removed && !ch.value?.memberIds.includes(m.id) && (ch.value?.isPrivate || m.kind === 'agent')))
const addId = ref<number | ''>('')

async function guard(fn: () => Promise<unknown>) {
  error.value = ''; saved.value = false
  try { await fn(); await loadBootstrap() } catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' }
}
const save = () => guard(async () => { await patch(`/channels/${props.channelId}`, { name: name.value, topic: topic.value }); saved.value = true })
const setPrivate = (v: boolean) => guard(() => patch(`/channels/${props.channelId}`, { isPrivate: v }))
const add = () => guard(async () => { if (addId.value !== '') await put(`/channels/${props.channelId}/members/${addId.value}`); addId.value = '' })
const remove = (id: number) => guard(() => del(`/channels/${props.channelId}/members/${id}`))
async function destroy() {
  error.value = ''
  try { await del(`/channels/${props.channelId}`); closeModal(); await loadBootstrap(); router.replace('/') } catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' }
}
</script>
<template>
  <Modal v-if="ch" :title="`#${ch.name} settings`" wide @close="closeModal">
    <form @submit.prevent="save">
      <Field name="name" label="Name" v-model="name" />
      <Field name="topic" label="Topic" v-model="topic" />
      <div class="flex items-center gap-3 mb-4"><Button type="submit" variant="primary">Save</Button><span v-if="saved" class="text-online-ink text-[13px]" role="status">Saved</span></div>
    </form>
    <label class="flex items-center gap-2 text-[13px] mb-4 cursor-pointer"><input type="checkbox" :checked="ch.isPrivate" @change="setPrivate(($event.target as HTMLInputElement).checked)" /> Private channel</label>

    <h3 class="text-[13px] font-semibold mt-2 mb-1">{{ ch.isPrivate ? 'People and agents' : 'Agents' }}</h3>
    <p v-if="!ch.isPrivate" class="text-xs text-muted mt-0 mb-2">Everyone in the server can see this channel. Add the agents that should take part.</p>
    <ul class="list-none p-0 m-0 mb-3" data-testid="channel-members">
      <li v-for="m in inChannel" :key="m.id" class="flex items-center gap-2.5 py-1">
        <Avatar :member="m" size="sm" /><span class="font-medium">{{ m.displayName }}</span><AgentBadge v-if="m.kind === 'agent'" /><span class="text-muted text-xs">@{{ m.handle }}</span>
        <Button v-if="m.kind === 'agent' || m.id !== ch.creatorId" small class="ml-auto" :data-remove="m.handle" @click="remove(m.id)">Remove</Button>
      </li>
    </ul>
    <div class="flex gap-2 items-center">
      <select v-model="addId" aria-label="Add to channel" class="flex-1 border border-line bg-card rounded-btn px-2 py-1.5 text-sm text-ink" data-testid="add-member">
        <option value="">Add {{ ch.isPrivate ? 'a person or agent' : 'an agent' }}…</option>
        <option v-for="m in addable" :key="m.id" :value="m.id">{{ m.displayName }} (@{{ m.handle }}){{ m.kind === 'agent' ? ' · agent' : '' }}</option>
      </select>
      <Button :disabled="addId === ''" @click="add">Add</Button>
    </div>

    <p v-if="error" role="alert" class="text-danger-ink text-[13px] mt-3 mb-0">{{ error }}</p>
    <div class="mt-6 pt-4 border-t border-line">
      <Button v-if="!confirmDelete" variant="danger" data-testid="delete-channel" @click="confirmDelete = true">Delete channel…</Button>
      <div v-else class="flex items-center gap-2">
        <span class="text-[13px]">Delete #{{ ch.name }} and all its posts?</span>
        <Button variant="danger" data-testid="confirm-delete" @click="destroy">Delete</Button><Button @click="confirmDelete = false">Cancel</Button>
      </div>
    </div>
  </Modal>
</template>
