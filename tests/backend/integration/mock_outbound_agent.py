#!/usr/bin/env python3
"""Dependency-free fake outbound Linux Agent for the two-gateway Redis/WSS smoke.
Never writes the invitation or Agent secret to logs or persistent files.
"""
import argparse
import base64
import http.client
import json
import os
import socket
import struct
import time
from urllib.parse import urlparse

inventory = {
    "hostname": "ci-outbound-agent", "operatingSystem": "Linux test", "architecture": "X64",
    "cpuLogicalCores": 64, "systemMemoryTotalGiB": 1024,
    "systemMemoryAvailableGiB": 950, "diskTotalGiB": 1024, "diskAvailableGiB": 950,
    "gpus": [{"name": "Simulated accelerator", "memoryTotalGiB": 512,
              "memoryFreeGiB": 500, "driverVersion": "test", "computeCapability": "9.0"}],
    "runtime": "docker", "runtimeVersion": "test",
    "readiness": {"dockerInstalled": True, "dockerDaemonReady": True,
                  "nvidiaDriverDetected": True, "nvidiaToolkitReady": True, "issues": []}
}
state = {"installationId": "ci-outbound-installation", "catalogModelId": "",
         "providerModelName": "", "status": "stopped", "runtimeBaseAddress": "http://127.0.0.1:18000",
         "port": 18000, "runtime": "vllm"}


def request_api(host, port, path, body):
    conn = http.client.HTTPConnection(host, port, timeout=10)
    conn.request("POST", path, json.dumps(body).encode("utf-8"),
                 {"Content-Type": "application/json"})
    res = conn.getresponse()
    payload = res.read()
    if not 200 <= res.status < 300:
        raise RuntimeError("API returned HTTP %d for %s" % (res.status, path))
    conn.close()
    return json.loads(payload)


def read_exact(sock, count):
    chunks = []
    while count:
        part = sock.recv(count)
        if not part:
            raise EOFError("WebSocket disconnected")
        chunks.append(part)
        count -= len(part)
    return b"".join(chunks)


def receive(sock):
    while True:
        head = read_exact(sock, 2)
        opcode = head[0] & 15
        size = head[1] & 127
        masked = bool(head[1] & 128)
        if size == 126:
            size = struct.unpack("!H", read_exact(sock, 2))[0]
        if size == 127:
            size = struct.unpack("!Q", read_exact(sock, 8))[0]
        if size > 6 * 1024 * 1024:
            raise ValueError("Oversized gateway frame")
        mask = read_exact(sock, 4) if masked else None
        data = read_exact(sock, size)
        if mask:
            data = bytes(b ^ mask[i % 4] for i, b in enumerate(data))
        if opcode == 9:
            send(sock, data, 10)
            continue
        if opcode == 8:
            raise EOFError("WebSocket close")
        if opcode != 1:
            continue
        return json.loads(data)


def send(sock, payload, opcode=1):
    if isinstance(payload, dict):
        payload = json.dumps(payload, separators=(",", ":")).encode()
    size = len(payload)
    if size < 126:
        hdr = bytes([0x80 | opcode, 0x80 | size])
    elif size < 65536:
        hdr = bytes([0x80 | opcode, 0xfe]) + struct.pack("!H", size)
    else:
        hdr = bytes([0x80 | opcode, 0xff]) + struct.pack("!Q", size)
    mask = os.urandom(4)
    sock.sendall(hdr + mask + bytes(x ^ mask[i % 4] for i, x in enumerate(payload)))


def emit(sock, ident, method, path, body):
    content_type = "application/json"
    if path.endswith("/v1/system"):
        payload = inventory
    elif path.endswith("/v1/models") and method == "GET":
        payload = {"models": [state] if state["catalogModelId"] else []}
    elif path.endswith("/v1/models/install"):
        data = json.loads(base64.b64decode(body or "").decode())
        state["catalogModelId"] = data["catalogModelId"]
        state["providerModelName"] = data["providerModelName"]
        state["status"] = "stopped"
        payload = state
    elif path.endswith("/start"):
        state["status"] = "running"
        payload = state
    elif path.endswith("/stop"):
        state["status"] = "stopped"
        payload = state
    elif path.endswith("/health"):
        payload = {"status": "ok"}
    elif path.endswith("/v1/chat/completions"):
        content_type = "text/event-stream"
        payload = None
    else:
        payload = {"error": "unrecognized_mock_endpoint"}

    send(sock, {"type": "headers", "id": ident, "status": 200,
                "contentType": content_type})
    if payload is not None:
        data = json.dumps(payload, separators=(",", ":")).encode()
        send(sock, {"type": "chunk", "id": ident,
                    "body": base64.b64encode(data).decode()})
    else:
        for part in (
            'data: {"id":"ci-outbound","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"peer-ok"},"finish_reason":null}]}\\n\\n',
            "data: [DONE]\\n\\n",
        ):
            # Decode escaped newlines into genuine SSE delimiters.
            data = part.replace("\\n", "\n").encode()
            send(sock, {"type": "chunk", "id": ident,
                        "body": base64.b64encode(data).decode()})
            time.sleep(0.02)
    send(sock, {"type": "done", "id": ident})


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--gateway", default="http://127.0.0.1:8080")
    parser.add_argument("--state-file", required=True)
    args = parser.parse_args()
    url = urlparse(args.gateway)
    host, port = url.hostname, url.port or 80
    invitation = request_api(host, port, "/api/admin/node-enrollment/invitations", {})
    paired = request_api(host, port, "/api/agent-connection/enroll",
                         {"token": invitation["enrollmentToken"], "hostname": "ci-outbound-agent",
                          "mode": "outbound", "inventory": inventory})
    key = base64.b64encode(os.urandom(16)).decode()
    sock = socket.create_connection((host, port), timeout=15)
    sock.settimeout(90)
    sock.sendall(
        ("GET /api/agent-connection/%s/tunnel HTTP/1.1\r\n" % paired["nodeId"] +
         "Host: %s:%d\r\n" % (host, port) +
         "Upgrade: websocket\r\nConnection: Upgrade\r\n" +
         "Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: %s\r\n" % key +
         "Authorization: Bearer %s\r\n\r\n" % paired["agentSecret"]).encode()
    )
    header = bytearray()
    while b"\r\n\r\n" not in header:
        header.extend(sock.recv(1))
        if len(header) > 16384:
            raise ValueError("Oversized handshake")
    if b" 101 " not in header.split(b"\r\n", 1)[0]:
        raise RuntimeError("WebSocket upgrade failed")
    with open(args.state_file, "w", encoding="utf-8") as out:
        json.dump({"nodeId": paired["nodeId"]}, out)
    while True:
        message = receive(sock)
        if message.get("type") == "request":
            emit(sock, message["id"], message.get("method"),
                 message.get("path") or "", message.get("body"))


if __name__ == "__main__":
    main()
