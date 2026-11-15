#!/usr/bin/env python3
"""With example 10 and its local oMLX model running, exercise both hosted protocols."""
import http.client
import json


def request(path, payload, host="127.0.0.1:5079"):
    connection = http.client.HTTPConnection("127.0.0.1", 5079, timeout=120)
    try:
        connection.request("POST", path, json.dumps(payload), {
            "Content-Type": "application/json", "Host": host,
        })
        response = connection.getresponse()
        data = response.read()
        return response.status, json.loads(data) if data else None
    finally:
        connection.close()


status, result = request("/v1/chat/completions", {
    "model": "harness", "messages": [{"role": "user", "content": "Reply with exactly hello."}],
})
assert status == 200, (status, result)
assert result["object"] == "chat.completion" and result["choices"][0]["message"]["content"].strip(), result

status, result = request("/v1/responses", {"model": "harness", "input": "Reply with exactly hello."})
assert status == 200, (status, result)
assert result["object"] == "response" and result["status"] == "completed", result
assert any(item.get("type") == "message" and any(part.get("text", "").strip() for part in item.get("content", [])) for item in result["output"]), result

status, _ = request("/v1/responses", {}, host="attacker.example")
assert status == 400, status
print("Chat Completions, Responses, and hostile Host rejection passed.")
