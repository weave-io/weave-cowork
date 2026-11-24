#!/usr/bin/env python3
# A tiny fake ACP agent for automated tests only (the demo uses a real harness).
# prompt text containing PERMISSION makes it ask for permission and report the outcome; ECHO makes it echo the prompt.
import json, sys

def send(o): sys.stdout.write(json.dumps(o) + "\n"); sys.stdout.flush()
n = 0
pending = {}
loaded = set()
for line in sys.stdin:
    m = json.loads(line)
    if "method" not in m:                      # a response to our request
        pending[m["id"]] = m; continue
    if m["method"] == "initialize":
        send({"jsonrpc": "2.0", "id": m["id"], "result": {"protocolVersion": 1, "agentCapabilities": {"loadSession": True}}})
    elif m["method"] == "session/load":
        sid = m["params"]["sessionId"]
        if sid.startswith("bad"):
            send({"jsonrpc": "2.0", "id": m["id"], "error": {"code": -32602, "message": "no such session"}})
        else:
            loaded.add(sid)
            # a real harness replays the old conversation; the runner must not show it again
            send({"jsonrpc": "2.0", "method": "session/update", "params": {"sessionId": sid, "update": {"sessionUpdate": "agent_message_chunk", "content": {"type": "text", "text": "REPLAYED"}}}})
            send({"jsonrpc": "2.0", "id": m["id"], "result": {}})
    elif m["method"] == "session/new":
        n += 1
        send({"jsonrpc": "2.0", "id": m["id"], "result": {"sessionId": f"s{n}-{__import__('os').getpid()}"}})
    elif m["method"] == "session/prompt":
        sid = m["params"]["sessionId"]; text = m["params"]["prompt"][0]["text"]
        upd = lambda u: send({"jsonrpc": "2.0", "method": "session/update", "params": {"sessionId": sid, "update": u}})
        if "ECHO" in text:
            upd({"sessionUpdate": "agent_message_chunk", "content": {"type": "text", "text": "ECHO[" + text + "]" + ("LOADED:" + sid if sid in loaded else "")}})
        else:
            upd({"sessionUpdate": "agent_message_chunk", "content": {"type": "text", "text": "Hello "}})
            upd({"sessionUpdate": "tool_call", "toolCallId": "t1", "title": "Read README.md", "kind": "read", "status": "in_progress"})
            upd({"sessionUpdate": "tool_call_update", "toolCallId": "t1", "status": "completed"})
            upd({"sessionUpdate": "agent_message_chunk", "content": {"type": "text", "text": "world"}})
        if "PERMISSION" in text:
            send({"jsonrpc": "2.0", "id": 900, "method": "session/request_permission", "params": {"sessionId": sid,
                  "toolCall": {"toolCallId": "t2", "title": "Run ls"},
                  "options": [{"optionId": "allow", "name": "Allow once", "kind": "allow_once"}, {"optionId": "deny", "name": "Deny", "kind": "reject_once"}]}})
            while 900 not in pending:
                m2 = json.loads(sys.stdin.readline()); pending[m2.get("id")] = m2
            out = pending[900]["result"]["outcome"]
            upd({"sessionUpdate": "agent_message_chunk", "content": {"type": "text", "text": " outcome=" + json.dumps(out)}})
        send({"jsonrpc": "2.0", "id": m["id"], "result": {"stopReason": "end_turn"}})
