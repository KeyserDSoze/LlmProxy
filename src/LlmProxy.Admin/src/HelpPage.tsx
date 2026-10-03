import { useEffect, useMemo, useState } from 'react'
import { api } from './api'
import type { Model, SystemOneStatus } from './types'

export default function HelpPage({ models }: { models: Model[] }) {
  const [classifier, setClassifier] = useState<SystemOneStatus | null>(null)
  const sampleModel = useMemo(() => models.find(item => item.enabled)?.publicName ?? 'your-model', [models])

  useEffect(() => {
    void api.systemOneStatus().then(setClassifier).catch(() => undefined)
  }, [])

  const listModels = [
    'curl "$BASE_URL/v1/models" \\',
    '  -H "Authorization: Bearer $LLMPROXY_API_KEY"'
  ].join('\n')
  const chat = [
    'curl "$BASE_URL/v1/chat/completions" \\',
    '  -H "Authorization: Bearer $LLMPROXY_API_KEY" \\',
    '  -H "Content-Type: application/json" \\',
    '  -d \'{"model":"' + sampleModel + '","messages":[{"role":"user","content":"Hello"}],"stream":true}\''
  ].join('\n')
  const responses = [
    'curl "$BASE_URL/v1/responses" \\',
    '  -H "Authorization: Bearer $LLMPROXY_API_KEY" \\',
    '  -H "Content-Type: application/json" \\',
    '  -d \'{"model":"' + sampleModel + '","input":"Hello"}\''
  ].join('\n')
  const classifierExample = [
    'curl "$BASE_URL/v1/systemone" \\',
    '  -H "Authorization: Bearer $LLMPROXY_API_KEY" \\',
    '  -H "Content-Type: application/json" \\',
    '  -d \'{"state":{"document":"duplicate card charge"},"questions":{"billing":{"type":"noul","instructions":"Is this billing?"}}}\''
  ].join('\n')

  return <div className="stack">
    <section className="panel formPanel">
      <div className="panelTitle tuningTitle"><h2>Client endpoints</h2><span>OpenAI-compatible + System One</span></div>
      <div className="docGrid">
        <Doc title="List models" code={listModels} />
        <Doc title="Chat Completions" code={chat} />
        <Doc title="Responses" code={responses} />
        <Doc title="System One classifier" code={classifierExample} />
      </div>
      <p className="muted">The client sends the LlmProxy API key. For System One, LlmProxy replaces that credential with the classifier upstream bearer token configured on the server.</p>
    </section>

    <section className="panel">
      <div className="panelTitle"><h2>Published logical models</h2><span>{models.filter(item => item.enabled).length} active</span></div>
      <table><thead><tr><th>Client model</th><th>Provider model</th><th>Streaming</th><th>Tools</th><th>State</th></tr></thead><tbody>
        {models.map(model => <tr key={model.id}><td><strong>{model.publicName}</strong></td><td className="mono">{model.providerModelName}</td><td>{model.supportsStreaming ? 'Yes' : 'No'}</td><td>{model.supportsTools ? 'Yes' : 'No'}</td><td>{model.enabled ? 'Enabled' : 'Disabled'}</td></tr>)}
      </tbody></table>
    </section>

    <section className="panel formPanel">
      <div className="panelTitle tuningTitle"><h2>System One / Laya</h2><span>{classifier?.enabled ? 'Enabled' : 'Disabled'}</span></div>
      <p><strong>Public endpoint:</strong> <code>/v1/systemone</code></p>
      <p><strong>Configured upstream:</strong> <code>{classifier?.upstreamEndpoint ?? 'Not configured'}</code></p>
      <p><strong>Upstream bearer:</strong> {classifier?.apiKeyConfigured ? 'configured' : 'not configured'} · <strong>timeout:</strong> {classifier?.timeoutSeconds ?? '—'}s</p>
      <p>The payload is forwarded transparently to the configured System One service hosting the classifier, for example <code>convaiinnovations/laya</code>. LlmProxy does not reinterpret classifier fields; response status and body are returned to the caller.</p>
      <p>Use <strong>Playground → System One classifier</strong> to test the exact JSON body and inspect the raw response. The same exchange is written to administrator-only Content Logs.</p>
    </section>

    <section className="panel formPanel">
      <div className="panelTitle tuningTitle"><h2>GitHub Copilot / custom-model attribution</h2><span>What LlmProxy can identify</span></div>
      <p>GitHub Copilot organization/enterprise custom models are configured with a provider API key and made available to members. GitHub's public BYOK/custom-model contract does not document a guaranteed end-user identity header (email, GitHub user ID or Entra object ID) on each OpenAI-compatible provider request.</p>
      <p>Therefore, when the organization uses one shared LlmProxy API key, LlmProxy can attribute the live request to that API credential/usage group, but it must not guess the individual developer from IP, User-Agent or undocumented headers.</p>
      <p>GitHub exposes separate Copilot usage-metrics reports with per-user <code>user_id</code> and <code>user_login</code> fields. Those reports are useful for adoption/aggregate reporting, but they are not a documented request-by-request identity signal that can be correlated deterministically to this gateway call.</p>
      <p>Deterministic LlmProxy per-user attribution requires either one personal LlmProxy key per user/client configuration or a trusted component that authenticates the person and injects a signed identity token/header under a contract we control.</p>
    </section>

    <section className="panel formPanel">
      <div className="panelTitle tuningTitle"><h2>How LlmProxy processes a request</h2><span>Control-plane overview</span></div>
      <ol className="docSteps">
        <li><strong>Authentication:</strong> the client API key is HMAC-verified against the in-memory credential cache.</li>
        <li><strong>Governance:</strong> user/key/model rate limits and output-token budgets can reject a request before inference.</li>
        <li><strong>Routing:</strong> the logical model is resolved to healthy enabled deployments. The selected strategy chooses an eligible DGX deployment.</li>
        <li><strong>Capacity admission:</strong> node/deployment concurrency is acquired locally or through Redis. Saturation returns <code>429 capacity_exhausted</code> before the request reaches vLLM.</li>
        <li><strong>Upstream inference:</strong> the public model name is rewritten to the provider model and the request is proxied. Infrastructure failures may fail over to another deployment.</li>
        <li><strong>Observability:</strong> metadata/TTFT/token metrics feed routing and reports. Administrator-only payload logs keep the exact request and response encrypted at rest.</li>
        <li><strong>Retention:</strong> full-body payload logs are retained 10–180 days and cleaned every four hours; aggregate telemetry has its own independent retention policy.</li>
      </ol>
    </section>

    <section className="panel formPanel">
      <div className="panelTitle tuningTitle"><h2>Security notes</h2><span>What is and is not stored</span></div>
      <p>Authorization headers and upstream bearer tokens are never written to content logs. API-key plaintext is not stored directly: administrators can recover newly created/rotated keys because an encrypted copy is stored alongside the one-way authentication hash.</p>
      <p>Content Logs contain prompts and generated content by explicit platform policy. Access is limited to <code>LlmProxy.Admin</code>, including configured super admins, every API-key reveal is audited, and decrypted payload responses are marked <code>Cache-Control: no-store</code>.</p>
    </section>
  </div>
}

function Doc({ title, code }: { title: string; code: string }) {
  return <div className="docCard"><h3>{title}</h3><pre className="payload">{code}</pre><button className="secondary" onClick={() => void navigator.clipboard.writeText(code)}>Copy</button></div>
}
