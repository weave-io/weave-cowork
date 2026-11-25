#!/usr/bin/env python3
# A scripted ACP agent for tests/live/phase5.mjs only: it says what it does in steps and waits for gate files, so a test can
# drop a browser's connection at an exact moment. Real harnesses are covered by phase2.mjs.
#   prompt contains SCENARIO-A : "Part one.", wait for $GATE_DIR/go1, "Part two.", a read tool, a shell tool that needs permission, "Part three."
#   prompt contains HOLD       : "Writing the answer…", then waits for $GATE_DIR/never (the test kills the server meanwhile)
import json, os, sys, time

GATE = os.environ.get("GATE_DIR", ".")
def send(o): sys.stdout.write(json.dumps(o) + "\n"); sys.stdout.flush()
def gate(name):
    while not os.path.exists(os.path.join(GATE, name)): time.sleep(0.1)
n = 0; pending = {}
for line in sys.stdin:
    m = json.loads(line)
    if "method" not in m: pending[m["id"]] = m; continue
    if m["method"] == "initialize":
        send({"jsonrpc": "2.0", "id": m["id"], "result": {"protocolVersion": 1, "agentCapabilities": {}}})
    elif m["method"] == "session/new":
        n += 1; send({"jsonrpc": "2.0", "id": m["id"], "result": {"sessionId": f"scripted-{os.getpid()}-{n}"}})
    elif m["method"] == "session/prompt":
        sid = m["params"]["sessionId"]; text = m["params"]["prompt"][0]["text"]
        upd = lambda u: send({"jsonrpc": "2.0", "method": "session/update", "params": {"sessionId": sid, "update": u}})
        say = lambda t: upd({"sessionUpdate": "agent_message_chunk", "content": {"type": "text", "text": t}})
        if "HOLD" in text:
            say("Writing the answer… "); gate("never")
        elif "SCENARIO-A" in text:
            say("Part one. "); gate("go1")
            say("Part two. "); time.sleep(0.3)
            upd({"sessionUpdate": "tool_call", "toolCallId": "r1", "title": "read", "kind": "read", "status": "pending", "locations": [], "rawInput": {}})
            upd({"sessionUpdate": "tool_call_update", "toolCallId": "r1", "status": "in_progress", "kind": "read", "title": "read", "rawInput": {"path": "README.md"}})
            upd({"sessionUpdate": "tool_call_update", "toolCallId": "r1", "status": "completed", "rawOutput": {"output": "# Weave CoWork\nA self-hosted Discord for people and agents."}})
            upd({"sessionUpdate": "tool_call", "toolCallId": "s1", "title": "bash", "kind": "execute", "status": "pending", "locations": [], "rawInput": {}})
            upd({"sessionUpdate": "tool_call_update", "toolCallId": "s1", "status": "in_progress", "kind": "execute", "title": "git log -3", "rawInput": {"command": "git log -3"}})
            send({"jsonrpc": "2.0", "id": 900, "method": "session/request_permission", "params": {"sessionId": sid,
                  "toolCall": {"toolCallId": "s1", "title": "git log -3"},
                  "options": [{"optionId": "allow", "name": "Allow once", "kind": "allow_once"}, {"optionId": "deny", "name": "Deny", "kind": "reject_once"}]}})
            while 900 not in pending:
                m2 = json.loads(sys.stdin.readline()); pending[m2.get("id")] = m2
            out = pending.pop(900)["result"]["outcome"]
            if out.get("optionId") == "allow":
                upd({"sessionUpdate": "tool_call_update", "toolCallId": "s1", "status": "completed", "rawOutput": {"output": "a1b2c3d Round tax at the line\ne4f5a6b Add the order confirmation page\n"}})
                say("Part three. The last commits are listed.")
            else:
                upd({"sessionUpdate": "tool_call_update", "toolCallId": "s1", "status": "failed"})
                say("Part three. I was told not to.")
        else:
            say("Hello.")
        send({"jsonrpc": "2.0", "id": m["id"], "result": {"stopReason": "end_turn"}})
