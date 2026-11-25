<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { AtSign } from 'lucide-vue-next'
import { get } from '../../api'
import { store, closeModal, memberById } from '../../store'
import AppDialog from '../AppDialog.vue'
import MemberAvatar from '../MemberAvatar.vue'
import AgentBadge from '../AgentBadge.vue'
import { EmptyState } from '@/components/ui/empty-state'
import { RelativeTime } from '@/components/ui/relative-time'

interface Mention { postId: number; channelId: number; channelName: string; threadId: number | null; authorId: number; excerpt: string; at: number; unread: boolean }
const items = ref<Mention[]>([]), loaded = ref(false)
const router = useRouter()
async function load() { items.value = await get('/mentions'); loaded.value = true }
onMounted(load)
watch(() => store.viewsVersion, load)
function go(m: Mention) { closeModal(); router.push(m.threadId ? `/c/${m.channelId}/t/${m.threadId}` : `/c/${m.channelId}`) }
</script>
<template>
  <AppDialog title="Mentions" description="Where someone wrote your @name." wide @close="closeModal">
    <EmptyState v-if="loaded && !items.length" :icon="AtSign" title="No mentions yet" description="When someone writes @you in a channel or a thread, it shows up here." />
    <ul v-else class="m-0 list-none p-0" data-testid="mentions-list">
      <li v-for="m in items" :key="m.postId">
        <button type="button" class="flex w-full items-start gap-2.5 rounded-btn border-0 bg-transparent px-2 py-2 text-left text-text transition-colors hover:bg-text/5" :data-unread="m.unread" data-testid="mention" @click="go(m)">
          <MemberAvatar :member="memberById(m.authorId)" size="sm" />
          <span class="min-w-0 flex-1">
            <span class="flex items-center gap-1.5 text-[13px]"><b class="font-semibold">{{ memberById(m.authorId)?.displayName }}</b><AgentBadge v-if="memberById(m.authorId)?.kind === 'agent'" />
              <span class="text-muted">in #{{ m.channelName }}{{ m.threadId ? ' · thread' : '' }}</span><span class="ml-auto shrink-0 text-xs text-muted"><RelativeTime :at="m.at" compact /></span></span>
            <span class="mt-0.5 block text-[13.5px] break-words" :class="m.unread ? 'text-text' : 'text-muted'">{{ m.excerpt }}</span>
          </span>
          <i v-if="m.unread" class="mt-1.5 size-2 shrink-0 rounded-full bg-accent" aria-label="Unread" />
        </button>
      </li>
    </ul>
  </AppDialog>
</template>
