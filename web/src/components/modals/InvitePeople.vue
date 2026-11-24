<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { get, post, del } from '../../api'
import { closeModal, memberById } from '../../store'
import Modal from '../ui/Modal.vue'
import Button from '../ui/Button.vue'

interface Invite { code: string; byName: string; createdAt: number; expiresAt: number; maxUses: number; uses: number }
const invites = ref<Invite[]>([])
const fresh = ref<string | null>(null), copied = ref(false)
const link = (code: string) => `${location.origin}/invite/${code}`
const load = async () => { invites.value = await get('/invites') }
onMounted(load)
async function create() { const { code } = await post('/invites', {}); fresh.value = code; copied.value = false; await load() }
async function revoke(code: string) { await del(`/invites/${code}`); if (fresh.value === code) fresh.value = null; await load() }
async function copy(text: string) { try { await navigator.clipboard.writeText(text); copied.value = true } catch { /* not allowed on http */ } }
const days = (ms: number) => Math.max(0, Math.ceil((ms - Date.now()) / 86_400_000))
</script>
<template>
  <Modal title="Invite people" wide @close="closeModal">
    <p class="mt-0 text-[13px] text-muted">A link lets someone make an account and join. It expires in 7 days and can be used 10 times.</p>
    <Button variant="primary" data-testid="create-invite" @click="create">Create an invite link</Button>
    <div v-if="fresh" class="mt-3 flex gap-2 items-center bg-card border border-line rounded-btn p-2">
      <code class="flex-1" data-testid="invite-link">{{ link(fresh) }}</code>
      <Button small @click="copy(link(fresh))">{{ copied ? 'Copied' : 'Copy' }}</Button>
    </div>
    <h3 class="text-[13px] font-semibold mt-5 mb-1">Active links</h3>
    <p v-if="!invites.length" class="text-xs text-muted m-0">No active links.</p>
    <ul class="list-none p-0 m-0" data-testid="invite-list">
      <li v-for="i in invites" :key="i.code" class="flex items-center gap-2 py-1.5 border-b border-line text-[13px]">
        <code class="truncate max-w-[200px]">…{{ i.code.slice(-8) }}</code>
        <span class="text-muted">by {{ i.byName }} · {{ i.uses }}/{{ i.maxUses }} used · expires in {{ days(i.expiresAt) }} days</span>
        <Button small variant="danger" class="ml-auto" :data-revoke="i.code" @click="revoke(i.code)">Revoke</Button>
      </li>
    </ul>
  </Modal>
</template>
