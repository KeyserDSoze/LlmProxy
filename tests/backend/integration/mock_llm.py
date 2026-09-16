#!/usr/bin/env python3
import argparse
import json
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def normalize_prefix(prefix: str) -> str:
    if not prefix or prefix == "/":
        return ""
    return "/" + prefix.strip("/")


class MockLlmHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "LlmProxyMockRuntime/1.0"

    def log_message(self, fmt, *args):
        return

    def _expected(self, suffix: str) -> str:
        return f"{self.server.prefix}{suffix}"

    def _json(self, status: int, payload: dict):
        body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
        self.wfile.flush()

    def _text(self, status: int, content_type: str, text: str):
        body = text.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
        self.wfile.flush()

    def do_GET(self):
        if self.path in {self._expected("/healthz"), self._expected("/readyz")}:
            self._json(200, {
                "status": "ok",
                "served_by": self.server.runtime_name,
            })
            return
        if self.path == self._expected("/health"):
            status = self.server.health_status
            self._json(status, {
                "status": "ok" if 200 <= status < 300 else "failed",
                "served_by": self.server.runtime_name,
                "configured_status": status,
            })
            return
        if self.path == self._expected("/v1/models"):
            self._json(200, {
                "object": "list",
                "data": [{"id": "bootstrap-model", "object": "model", "owned_by": "mock"}],
                "served_by": self.server.runtime_name,
            })
            return
        if self.path == self._expected("/metrics"):
            running = 1 if self.server.runtime_name == "primary" else 2
            waiting = 0 if self.server.runtime_name == "primary" else 1
            kv_cache = 0.22 if self.server.runtime_name == "primary" else 0.55
            model = "bootstrap-model"
            exposition = f"""# HELP vllm:num_requests_running Number of requests in model execution batches.
# TYPE vllm:num_requests_running gauge
vllm:num_requests_running{{model_name=\"{model}\"}} {running}
# HELP vllm:num_requests_waiting Number of requests waiting to be processed.
vllm:num_requests_waiting{{model_name=\"{model}\"}} {waiting}
# HELP vllm:kv_cache_usage_perc KV cache usage.
vllm:kv_cache_usage_perc{{model_name=\"{model}\"}} {kv_cache}
vllm:prompt_tokens_total{{model_name=\"{model}\"}} 1234
vllm:generation_tokens_total{{model_name=\"{model}\"}} 567
"""
            self._text(200, "text/plain; version=0.0.4", exposition)
            return
        self._json(404, {"error": "not_found", "path": self.path})

    def do_POST(self):
        if self.path.startswith("/__control/health/"):
            try:
                status = int(self.path.rsplit("/", 1)[-1])
            except ValueError:
                self._json(400, {"error": "invalid_status"})
                return
            if status < 100 or status > 599:
                self._json(400, {"error": "invalid_status"})
                return
            self.server.health_status = status
            self._json(200, {"health_status": status, "served_by": self.server.runtime_name})
            return

        if self.path not in {
            self._expected("/v1/chat/completions"),
            self._expected("/v1/responses"),
        }:
            self._json(404, {"error": "not_found", "path": self.path})
            return

        length = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(length) if length else b"{}"
        payload = json.loads(raw.decode("utf-8"))

        if payload.get("stream"):
            self._stream(payload)
            return

        if self.path.endswith("/v1/responses"):
            self._json(200, {
                "id": f"resp_{self.server.runtime_name}",
                "object": "response",
                "model": payload.get("model"),
                "served_by": self.server.runtime_name,
                "output": [],
                "usage": {"input_tokens": 13, "output_tokens": 5, "total_tokens": 18},
            })
            return

        self._json(200, {
            "id": f"chatcmpl_{self.server.runtime_name}",
            "object": "chat.completion",
            "model": payload.get("model"),
            "served_by": self.server.runtime_name,
            "choices": [{"index": 0, "message": {"role": "assistant", "content": "mock response"}, "finish_reason": "stop"}],
            "usage": {"prompt_tokens": 11, "completion_tokens": 7, "total_tokens": 18},
        })

    def _stream(self, payload: dict):
        delay_seconds = float(payload.get("mock_stream_delay_seconds", 0.5))
        delay_seconds = min(max(delay_seconds, 0.0), 30.0)

        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "close")
        self.end_headers()

        for text in ["first", "second"]:
            event = {
                "id": f"chatcmpl_{self.server.runtime_name}",
                "object": "chat.completion.chunk",
                "model": payload.get("model"),
                "served_by": self.server.runtime_name,
                "choices": [{"index": 0, "delta": {"content": text}, "finish_reason": None}],
            }
            self.wfile.write(f"data: {json.dumps(event, separators=(',', ':'))}\n\n".encode("utf-8"))
            self.wfile.flush()
            time.sleep(delay_seconds)

        usage_event = {
            "id": f"chatcmpl_{self.server.runtime_name}",
            "object": "chat.completion.chunk",
            "model": payload.get("model"),
            "served_by": self.server.runtime_name,
            "choices": [],
            "usage": {"prompt_tokens": 17, "completion_tokens": 6, "total_tokens": 23},
        }
        self.wfile.write(f"data: {json.dumps(usage_event, separators=(',', ':'))}\n\n".encode("utf-8"))
        self.wfile.flush()
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()
        self.close_connection = True


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--prefix", default="")
    parser.add_argument("--name", required=True)
    args = parser.parse_args()

    server = ThreadingHTTPServer(("0.0.0.0", args.port), MockLlmHandler)
    server.prefix = normalize_prefix(args.prefix)
    server.runtime_name = args.name
    server.health_status = 200
    server.serve_forever()


if __name__ == "__main__":
    main()
