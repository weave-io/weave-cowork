<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { Link, Copy, Check } from 'lucide-vue-next'
import { get, post, del } from '../../api'
import { closeModal } from '../../store'
import AppDialog from '../AppDialog.vue'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'

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
  <AppDialog title="Invite people" description="A link lets someone make an account and join. It expires in 7 days and can be used 10 times." wide @close="closeModal">
    <Button data-testid="create-invite" @click="create"><Link :size="16" />Create an invite link</Button>
    <div v-if="fresh" class="mt-3 flex items-center gap-2 rounded-btn border border-border bg-card-bg p-2">
      <code class="flex-1" data-testid="invite-link">{{ link(fresh) }}</code>
      <Button size="sm" variant="outline" @click="copy(link(fresh))"><Check v-if="copied" :size="14" /><Copy v-else :size="14" />{{ copied ? 'Copied' : 'Copy' }}</Button>
    </div>
    <h3 class="mt-5 mb-1 text-[11px] font-semibold tracking-[0.05em] text-muted uppercase">Active links</h3>
    <EmptyState v-if="!invites.length" compact title="No active links" bordered />
    <ul class="m-0 list-none p-0" data-testid="invite-list">
      <li v-for="i in invites" :key="i.code" class="flex items-center gap-2 border-b border-border py-1.5 text-[13px]">
        <code class="max-w-[200px] truncate">…{{ i.code.slice(-8) }}</code>
        <span class="text-muted">by {{ i.byName }} · {{ i.uses }}/{{ i.maxUses }} used · expires in {{ days(i.expiresAt) }} days</span>
        <Button size="sm" variant="outline" class="ml-auto text-error-text" :data-revoke="i.code" @click="revoke(i.code)">Revoke</Button>
      </li>
    </ul>
  </AppDialog>
</template>
