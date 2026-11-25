<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { patch, put, del, ApiError } from '../../api'
import { store, closeModal, loadBootstrap } from '../../store'
import AppDialog from '../AppDialog.vue'
import Field from '../FormField.vue'
import MemberAvatar from '../MemberAvatar.vue'
import AgentBadge from '../AgentBadge.vue'
import { Button } from '@/components/ui/button'
import { Switch } from '@/components/ui/switch'
import { ErrorNote } from '@/components/ui/error-note'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { confirmAction } from '@/components/ui/confirm'

const props = defineProps<{ channelId: number }>()
const router = useRouter()
const ch = computed(() => store.channels.find(c => c.id === props.channelId))
const name = ref(ch.value?.name ?? ''), topic = ref(ch.value?.topic ?? '')
const error = ref(''), saved = ref(false)
const inChannel = computed(() => store.members.filter(m => !m.removed && ch.value?.memberIds.includes(m.id)))
const addable = computed(() => store.members.filter(m => !m.removed && !ch.value?.memberIds.includes(m.id) && (ch.value?.isPrivate || m.kind === 'agent')))
const addId = ref('')

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
  if (!(await confirmAction({ title: `Delete #${ch.value?.name}?`, body: 'The channel and all its posts are deleted. This can’t be undone.', confirmLabel: 'Delete channel', destructive: true }))) return
  try { await del(`/channels/${props.channelId}`); closeModal(); await loadBootstrap(); router.replace('/') } catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' }
}
</script>
<template>
  <AppDialog v-if="ch" :title="`${ch.kind === 'voice' ? '' : '#'}${ch.name} settings`" wide @close="closeModal">
    <form @submit.prevent="save">
      <Field name="name" label="Name" v-model="name" />
      <Field name="topic" label="Topic" v-model="topic" />
      <div class="mb-4 flex items-center gap-3"><Button type="submit">Save</Button><span v-if="saved" class="text-[13px] text-running-text" role="status">Saved</span></div>
    </form>
    <label class="mb-4 flex cursor-pointer items-center gap-3 text-[13px]"><Switch :model-value="ch.isPrivate" data-testid="private-switch" aria-label="Private channel" @update:model-value="setPrivate($event === true)" /> Private channel</label>

    <h3 class="mt-2 mb-1 text-[11px] font-semibold tracking-[0.05em] text-muted uppercase">{{ ch.isPrivate ? 'People and agents' : 'Agents' }}</h3>
    <p v-if="!ch.isPrivate" class="mt-0 mb-2 text-xs text-muted">Everyone in the server can see this channel. Add the agents that should take part.</p>
    <ul class="m-0 mb-3 list-none p-0" data-testid="channel-members">
      <li v-for="m in inChannel" :key="m.id" class="flex items-center gap-2.5 py-1">
        <MemberAvatar :member="m" size="sm" /><span class="font-medium">{{ m.displayName }}</span><AgentBadge v-if="m.kind === 'agent'" /><span class="text-xs text-muted">@{{ m.handle }}</span>
        <Button v-if="m.kind === 'agent' || m.id !== ch.creatorId" size="sm" variant="outline" class="ml-auto" :data-remove="m.handle" @click="remove(m.id)">Remove</Button>
      </li>
    </ul>
    <div class="flex items-center gap-2">
      <Select v-model="addId">
        <SelectTrigger class="flex-1" aria-label="Add to channel" data-testid="add-member"><SelectValue :placeholder="`Add ${ch.isPrivate ? 'a person or agent' : 'an agent'}…`" /></SelectTrigger>
        <SelectContent>
          <SelectItem v-for="m in addable" :key="m.id" :value="String(m.id)">{{ m.displayName }} (@{{ m.handle }}){{ m.kind === 'agent' ? ' · agent' : '' }}</SelectItem>
        </SelectContent>
      </Select>
      <Button variant="outline" :disabled="addId === ''" @click="add">Add</Button>
    </div>

    <ErrorNote v-if="error" :message="error" class="mt-3" />
    <div class="mt-6 border-t border-border pt-4">
      <Button variant="destructive" data-testid="delete-channel" @click="destroy">Delete channel…</Button>
    </div>
  </AppDialog>
</template>
