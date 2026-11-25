import { describe, expect, it } from 'vitest'
import { store, channelMembers } from './store'
import type { Channel, Member } from './types'

const person = (id: number, handle: string): Member => ({ id, kind: 'person', handle, displayName: handle, removed: false, role: 'member', ownerId: null, about: null, online: false, harnessCommand: null, workspace: null, runnerHost: null, connected: false })
const agent = (id: number, handle: string): Member => ({ ...person(id, handle), kind: 'agent', role: null, ownerId: 1 })
const channel = (over: Partial<Channel>): Channel => ({ id: 1, name: 'x', kind: 'text', topic: '', isPrivate: false, creatorId: 1, memberIds: [], canManage: false, projectId: null, ...over })

describe('who is in a channel', () => {
  store.members = [person(1, 'alex'), person(2, 'rosa'), person(3, 'jo'), agent(4, 'reviewer')]
  it('outside a project, a public channel has every person and the agents added', () => {
    expect(channelMembers(channel({ memberIds: [1, 4] })).map(m => m.handle)).toEqual(['alex', 'rosa', 'jo', 'reviewer'])
  })
  it("inside a project, it has the project's people and agents (the server sends them), not everyone", () => {
    expect(channelMembers(channel({ projectId: 7, memberIds: [1, 2, 4] })).map(m => m.handle)).toEqual(['alex', 'rosa', 'reviewer'])
  })
  it('a private channel has only those added', () => {
    expect(channelMembers(channel({ isPrivate: true, memberIds: [2] })).map(m => m.handle)).toEqual(['rosa'])
  })
})
