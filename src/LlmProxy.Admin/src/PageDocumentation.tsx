import type { ReactNode } from 'react'

const help: Record<string, { title: string; body: ReactNode }> = {
  dashboard: { title: 'Dashboard', body: <><p>Operational summary of the gateway: fleet health, active requests, logical models, deployments and recent inference latency.</p><p>Use this page to decide where to drill down. It does not change runtime configuration.</p></> },
  nodes: { title: 'DGX nodes', body: <><p>Registers inference service roots, health probes, node-level concurrency and optional upstream bearer credentials.</p><p>Node max concurrency is a hard admission limit. A request can receive <code>429 capacity_exhausted</code> before it reaches vLLM when all eligible capacity is occupied.</p></> },
  hardware: { title: 'DGX hardware', body: <><p>Shows optional DCGM and runtime telemetry separately from routing. Capacity profiles are recommendations until an administrator explicitly applies them.</p></> },
  models: { title: 'Logical models', body: <><p>Logical models are the client-facing names accepted by <code>/v1/chat/completions</code> and <code>/v1/responses</code>. The provider model name is what LlmProxy sends to the selected runtime.</p><p>Use Playground to send a real non-streaming chat test through routing and capacity admission.</p></> },
  deployments: { title: 'Deployments', body: <><p>A deployment binds one logical model to one DGX node. Effective selection also considers enabled state, health, configured weight and available node/deployment concurrency.</p></> },
  routing: { title: 'Routing', body: <><p>Weighted least loaded considers configured capacity, health, recent TTFT/failure feedback and optional live vLLM pressure. Round robin variants are available for simpler routing.</p><p>Rate limiting is caller governance; capacity admission protects the inference fleet. They intentionally produce different telemetry.</p></> },
  credentials: { title: 'API credentials', body: <><p>Keys authenticate inference calls. LlmProxy stores a one-way authentication hash plus an application-encrypted copy of newly created/rotated secrets so administrators can reveal/copy them later.</p><p>Older keys created before encrypted retention cannot be recovered; rotate them once to enable Reveal.</p></> },
  metrics: { title: 'Request metrics', body: <><p>Aggregated inference telemetry: status, routing attempts, TTFT, duration and token usage. Full request/response payloads are intentionally separated into administrator-only Content Logs.</p></> },
  logs: { title: 'Content logs', body: <><p>Administrator-only encrypted-at-rest request and response payloads for Chat Completions, Responses and System One. The page auto-refreshes every two seconds.</p><p>Retention is configurable from 10 to 180 days. Cleanup runs automatically every four hours.</p></> },
  playground: { title: 'Playground', body: <><p>Runs live diagnostics against active logical models and the configured System One classifier. Model tests pass through LlmProxy routing and capacity admission but bypass caller rate limits because they are administrative diagnostics.</p><p>Each upstream diagnostic is written to Content Logs so its exact request and response can be inspected later.</p></> },
  audit: { title: 'Audit trail', body: <><p>Administrative configuration and sensitive-control actions. API-key secret reveals, retention changes and manual cleanup runs are audited without recording the revealed secret itself.</p></> },
  help: { title: 'Endpoint & platform guide', body: <><p>Copy-ready endpoint examples plus an explanation of authentication, routing, capacity, rate limiting, classifier forwarding, observability and retention.</p></> }
}

export default function PageDocumentation({ page }: { page: string }) {
  const item = help[page] ?? { title: 'This page', body: <p>Operational controls and information for LlmProxy.</p> }
  return <details className="pageDocumentation">
    <summary>Page documentation · {item.title}</summary>
    <div className="pageDocumentationBody">{item.body}</div>
  </details>
}
