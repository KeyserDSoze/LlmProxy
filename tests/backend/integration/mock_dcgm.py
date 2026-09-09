#!/usr/bin/env python3
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def normalize_prefix(prefix: str) -> str:
    if not prefix or prefix == "/":
        return ""
    return "/" + prefix.strip("/")


class DcgmHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "LlmProxyMockDcgm/1.0"

    def log_message(self, fmt, *args):
        return

    def _expected(self, suffix: str) -> str:
        return f"{self.server.prefix}{suffix}"

    def _text(self, status: int, text: str):
        body = text.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "text/plain; version=0.0.4")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
        self.wfile.flush()

    def do_GET(self):
        if self.path != self._expected("/metrics"):
            self._text(404, "not found\n")
            return

        if self.server.metrics_status != 200:
            self._text(self.server.metrics_status, "dcgm exporter unavailable\n")
            return

        exposition = """# HELP DCGM_FI_DEV_GPU_UTIL GPU utilization (in %).
# TYPE DCGM_FI_DEV_GPU_UTIL gauge
DCGM_FI_DEV_GPU_UTIL{gpu=\"0\",UUID=\"GPU-a\"} 40
DCGM_FI_DEV_GPU_UTIL{gpu=\"1\",UUID=\"GPU-b\"} 80
DCGM_FI_DEV_FB_USED{gpu=\"0\",UUID=\"GPU-a\"} 1000
DCGM_FI_DEV_FB_USED{gpu=\"1\",UUID=\"GPU-b\"} 3000
DCGM_FI_DEV_FB_FREE{gpu=\"0\",UUID=\"GPU-a\"} 7000
DCGM_FI_DEV_FB_FREE{gpu=\"1\",UUID=\"GPU-b\"} 5000
DCGM_FI_DEV_GPU_TEMP{gpu=\"0\",UUID=\"GPU-a\"} 61
DCGM_FI_DEV_GPU_TEMP{gpu=\"1\",UUID=\"GPU-b\"} 67
DCGM_FI_DEV_POWER_USAGE{gpu=\"0\",UUID=\"GPU-a\"} 120.5
DCGM_FI_DEV_POWER_USAGE{gpu=\"1\",UUID=\"GPU-b\"} 140.5
"""
        self._text(200, exposition)

    def do_POST(self):
        if self.path.startswith("/__control/status/"):
            try:
                status = int(self.path.rsplit("/", 1)[-1])
            except ValueError:
                self._text(400, "invalid status\n")
                return
            if status < 100 or status > 599:
                self._text(400, "invalid status\n")
                return
            self.server.metrics_status = status
            self._text(200, f"{status}\n")
            return

        self._text(404, "not found\n")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--prefix", default="")
    args = parser.parse_args()

    server = ThreadingHTTPServer(("0.0.0.0", args.port), DcgmHandler)
    server.prefix = normalize_prefix(args.prefix)
    server.metrics_status = 200
    server.serve_forever()


if __name__ == "__main__":
    main()
