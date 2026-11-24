export interface Member {
  id: number; kind: 'person' | 'agent'; handle: string; displayName: string; removed: boolean
  role: 'admin' | 'member' | null; ownerId: number | null; about: string | null; online: boolean
  harnessCommand: string[] | null; workspace: string | null; runnerHost: string | null; connected: boolean
}
export interface Channel {
  id: number; name: string; kind: 'text' | 'voice'; topic: string; isPrivate: boolean; creatorId: number; memberIds: number[]; canManage: boolean
}
export interface Post {
  id: number; channelId: number; threadId: number | null; authorId: number; body: string
  kind: 'text' | 'tool' | 'ask' | 'notice'; meta: any; streaming: boolean; createdAt: number
  replyCount: number; hasThread: boolean; threadAgents?: string[] | null
}
export interface ThreadAgent {
  id: number; handle: string; displayName: string; online: boolean; busy: boolean; queued: number
  harnessCommand: string[]; runnerHost: string | null; ownerId: number
}
export interface ThreadState { rootId: number; root: Post; replies: Post[]; agents: ThreadAgent[]; startedBy: number }
export interface FeedState { channelId: number | null; posts: Post[]; thread: ThreadState | null }
