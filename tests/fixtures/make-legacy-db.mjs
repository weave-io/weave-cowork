// Fills a server that keeps its state in tables (a build from before round 2) with a bit of everything, and writes what its API answers.
// The importer's test starts a new server on a copy of that database and expects the same answers.
//
//   git worktree add --detach ~/.cache/cowork-legacy/main 291ef6b && (cd ~/.cache/cowork-legacy/main && dotnet build src/CoWork.Server)
//   COWORK_DB=~/.cache/cowork-legacy/data/cowork.db ASPNETCORE_URLS=http://127.0.0.1:5883 dotnet ~/.cache/cowork-legacy/main/src/CoWork.Server/bin/Debug/net10.0/CoWork.Server.dll &
//   node tests/fixtures/make-legacy-db.mjs http://127.0.0.1:5883 tests/CoWork.Server.Tests/Fixtures/legacy-main-round1.expected.json
//   (stop the server, then copy cowork.db to tests/CoWork.Server.Tests/Fixtures/legacy-main-round1.db)
import { writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { createRequire } from 'node:module'
const signalR = createRequire(import.meta.url)('../../web/node_modules/@microsoft/signalr/dist/cjs/index.js')

const BASE = process.argv[2] ?? 'http://127.0.0.1:5883'
const OUT = process.argv[3]
const sleep = ms => new Promise(r => setTimeout(r, ms))

class Client {
  cookie = ''
  async call(method, path, body) {
    const r = await fetch(BASE + path, { method, headers: { 'content-type': 'application/json', cookie: this.cookie }, body: body === undefined ? undefined : JSON.stringify(body) })
    const set = r.headers.getSetCookie?.()[0]; if (set) this.cookie = set.split(';')[0]
    const text = await r.text()
    if (!r.ok) throw new Error(`${method} ${path} -> ${r.status} ${text}`)
    return text ? JSON.parse(text) : null
  }
  get = p => this.call('GET', p)
  post = (p, b = {}) => this.call('POST', p, b)
  put = (p, b = {}) => this.call('PUT', p, b)
  del = p => this.call('DELETE', p)
  patch = (p, b) => this.call('PATCH', p, b)
}

/** A runner for one agent: connects with its token and answers deliveries by saying what `script` tells it to. */
async function runner(token, script) {
  const conn = new signalR.HubConnectionBuilder().withUrl(BASE + '/hubs/runner', { headers: { Authorization: 'Bearer ' + token, 'X-Cowork-Host': 'build-01' }, transport: signalR.HttpTransportType.LongPolling }).build()
  const delivered = []
  conn.on('Deliver', m => { delivered.push(m); script?.(conn, m) })
  await conn.start()
  return { conn, delivered, waitFor: async n => { for (let i = 0; i < 200 && delivered.length < n; i++) await sleep(50) } }
}
const stream = (conn, e) => conn.invoke('Stream', e)

const alex = new Client(), rosa = new Client()
await alex.post('/api/setup', { serverName: 'Lumen Labs', username: 'alex', displayName: 'Alex', password: 'correct horse' })
const used = await alex.post('/api/invites', { maxUses: 5 })
await rosa.post(`/api/invites/${used.code}/accept`, { username: 'rosa', displayName: 'Rosa', password: 'rosa-password' })
const bob = new Client()
await bob.post(`/api/invites/${used.code}/accept`, { username: 'bob', displayName: 'Bob', password: 'bob-password' })
await alex.del(`/api/people/${(await bob.get('/api/bootstrap')).meId}`)                              // a person who joined and was removed
const revoked = await alex.post('/api/invites', {}); await alex.del(`/api/invites/${revoked.code}`)
await alex.post('/api/invites', { maxUses: 2, expiresInDays: 30 })                                  // still open
const me = (await alex.get('/api/bootstrap')).meId, rosaId = (await rosa.get('/api/bootstrap')).meId

const checkout = (await alex.post('/api/channels', { name: 'checkout-app', topic: 'The checkout app' })).id
const leads = (await alex.post('/api/channels', { name: 'leads', isPrivate: true })).id
await alex.post('/api/channels', { name: 'standup', kind: 'voice' })
const scratch = (await alex.post('/api/channels', { name: 'scratch' })).id
await alex.post(`/api/channels/${scratch}/posts`, { body: 'this goes away' }); await alex.del(`/api/channels/${scratch}`)
await alex.post(`/api/channels/${leads}/posts`, { body: 'hiring plans for the quarter' })

// agents: reviewer (runs), sleeper (enrolled, never runs: what is said to it waits), fresh (a code nobody used yet), gone (removed)
const newAgent = async (handle, owner = alex) => (await owner.post('/api/agents', { handle, instructions: 'Be brief.', harnessCommand: 'opencode acp', workspace: '/work/lumen' }))
const enrol = async code => (await fetch(BASE + '/api/runner/enrol', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ code, host: 'build-01' }) }).then(r => r.json()))
const reviewer = await newAgent('reviewer'); const rev = await enrol(reviewer.code)
const sleeper = await newAgent('sleeper'); const slp = await enrol(sleeper.code)
await newAgent('fresh')
const gone = await newAgent('gone'); await alex.del(`/api/agents/${gone.agentId}`)
await alex.put(`/api/agents/${reviewer.agentId}`, { displayName: 'Reviewer', about: 'Reads code', instructions: 'Be brief.', harnessCommand: 'opencode acp', workspace: '/work/lumen', recordToolOutput: true })
for (const a of [rev, slp]) await alex.put(`/api/channels/${checkout}/members/${a.agentId}`, {})
await alex.put(`/api/channels/${leads}/members/${rosaId}`, {}); await alex.del(`/api/channels/${leads}/members/${rosaId}`)

// a conversation: a question, an answer with a tool, a follow-up from Rosa that needs an approval, then a mention of the sleeper
const run = await runner(rev.token, async (conn, m) => {
  const t = m.threadId
  if (m.text.includes('what does this repository do')) {
    await stream(conn, { threadId: t, kind: 'text', text: 'Let me look.' })
    await stream(conn, { threadId: t, kind: 'toolStart', toolId: 't1', title: 'read', toolKind: 'read', status: 'running' })
    await stream(conn, { threadId: t, kind: 'toolUpdate', toolId: 't1', title: 'read README.md' })
    await stream(conn, { threadId: t, kind: 'toolEnd', toolId: 't1', status: 'completed', outputBytes: 12, outputSha256: 'ab'.repeat(32), output: 'hello world.' })
    await stream(conn, { threadId: t, kind: 'text', text: "It's a checkout app." })
    await stream(conn, { threadId: t, kind: 'turnEnded' })
  } else if (m.text.includes('git log')) {
    await conn.invoke('RequestPermission', { threadId: t, requestId: 'r1', title: 'run git log -3', options: [{ id: 'allow', name: 'Allow once', kind: 'allow_once' }, { id: 'reject', name: 'Reject', kind: 'reject_once' }] })
  } else {
    await stream(conn, { threadId: t, kind: 'text', text: 'On it. Over to @sleeper.' })
    await stream(conn, { threadId: t, kind: 'turnEnded' })
  }
})
const root = (await alex.post(`/api/channels/${checkout}/posts`, { body: '@reviewer what does this repository do?' })).id
await run.waitFor(1)
for (let i = 0; i < 100; i++) { const t = await alex.get(`/api/threads/${root}`); if (t.agents[0] && !t.agents[0].busy) break; await sleep(50) }
await rosa.post(`/api/channels/${checkout}/posts`, { body: '@reviewer please run git log -3', threadId: root })
await run.waitFor(2)
let ask
for (let i = 0; i < 100 && !ask; i++) { ask = (await alex.get(`/api/threads/${root}`)).replies.find(p => p.kind === 'ask'); await sleep(50) }
await alex.post(`/api/posts/${ask.id}/answer`, { optionId: 'allow' })
await stream(run.conn, { threadId: root, kind: 'text', text: 'Three commits.' }); await stream(run.conn, { threadId: root, kind: 'turnEnded' })
await sleep(300)
await alex.post(`/api/channels/${checkout}/posts`, { body: '@reviewer one more thing, say hi to @sleeper', threadId: root })   // the agent answers and mentions the sleeper: a chained, queued delivery
await run.waitFor(3)
await alex.post(`/api/channels/${checkout}/posts`, { body: '@sleeper are you there?' })                                              // queued: its runner never connects
await alex.post(`/api/channels/${checkout}/posts`, { body: 'a plain message in the channel' })
await sleep(500)
await run.conn.stop(); await sleep(1500)                                                                                               // the runner goes: the server shows it offline

// what the API answers, for each person, without the positions of events (they differ between versions)
const strip = (k, v) => k === 'position' || k === 'at' ? undefined : v
const out = {}
for (const [name, c] of [['alex', alex], ['rosa', rosa]]) {
  const boot = await c.get('/api/bootstrap')
  const feeds = {}, threads = {}
  for (const ch of boot.channels.filter(x => x.kind === 'text')) {
    feeds[ch.id] = await c.get(`/api/channels/${ch.id}/posts`)
    for (const p of feeds[ch.id].filter(x => x.hasThread)) threads[p.id] = await c.get(`/api/threads/${p.id}`)
  }
  out[name] = { bootstrap: boot, feeds, threads }
}
console.log(JSON.stringify({ people: [me, rosaId], posts: Object.values(out.alex.feeds).flat().length }))
if (OUT) writeFileSync(OUT, JSON.stringify(out, strip, 1))
process.exit(0)
