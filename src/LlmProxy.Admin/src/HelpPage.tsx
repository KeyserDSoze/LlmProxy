import { useEffect, useMemo, useState } from 'react'
import { api } from './api'
import type { Model, SystemOneStatus } from './types'
import { CopyButton, Tabs } from './UiPrimitives'
import { useRouteTab } from './adminNavigation'
import { modelKinds } from './modelKinds'
import { buildHelpExamples, helpTabOrder, publishedCount } from './helpExamples'
import type { HelpExample, HelpTab } from './helpExamples'

const tabLabels: Record<HelpTab, string> = {
  discover: 'Discover & limits', text: 'Text', images: 'Images', audio: 'Audio', embeddings: 'Embeddings', voice: 'Voice (streaming)', systemone: 'System One'
}

/** What each field of GET /v1/models/details means, for people who build a client against it. */
const detailFields: Array<[string, string]> = [
  ['id', 'The public name to put in "model". Provider and server names never appear.'],
  ['type', 'chat, embedding, image, speech, transcription, realtime or classifier (use it with ?type=).'],
  ['context_window', 'Longest prompt + answer one request can use. On llama.cpp this is the shared pool divided by the parallel slots. null: not declared.'],
  ['max_output_tokens', 'Largest answer worth asking for (max_tokens). Chat models only. null: not declared.'],
  ['input_modalities / output_modalities', 'What goes in and what comes out: text, image, audio or embedding.'],
  ['endpoints', 'The paths that serve this model.'],
  ['capabilities', 'streaming, tools, vision and reasoning, as declared on the model.']
]

export default function HelpPage({ models }: { models: Model[] }) {
  const [classifier, setClassifier] = useState<SystemOneStatus | null>(null)
  const sampleModel = useMemo(() => models.find(item => item.enabled)?.publicName ?? 'your-model', [models])

  useEffect(() => {
    void api.systemOneStatus().then(setClassifier).catch(() => undefined)
  }, [])

  const examples = useMemo(() => buildHelpExamples(models), [models])
  const [tab, setTab] = useRouteTab<HelpTab>('tab', helpTabOrder, 'discover')
  const browserExample = [
    "const response = await fetch(BASE_URL + '/v1/chat/completions', {",
    "  method: 'POST',",
    "  headers: { Authorization: 'Bearer ' + apiKey, 'Content-Type': 'application/json' },",
    "  body: JSON.stringify({ model: '" + sampleModel + "', stream: true, messages: [{ role: 'user', content: 'Hello' }] })",
    '})',
    'const reader = response.body.getReader() // server-sent events, one chunk at a time'
  ].join('\n')

  return <div className="stack">
    <section className="panel formPanel" aria-label="Client endpoints">
      <div className="panelTitle tuningTitle"><h2>Client endpoints</h2><span>Every API a client can call, with the model names you have published</span></div>
      <Tabs value={tab} onChange={setTab} items={helpTabOrder.map(value => ({ value, label: tabLabels[value], count: value === 'discover' ? undefined : publishedCount(models, value) }))} />
      <div className="docGrid">
        {examples[tab].map(example => <Doc key={example.id} example={example} />)}
      </div>
      {tab === 'discover' && <>
        <h3>Fields of <code>/v1/models/details</code></h3>
        <table><thead><tr><th>Field</th><th>Meaning</th></tr></thead><tbody>
          {detailFields.map(([field, meaning]) => <tr key={field}><td className="mono">{field}</td><td>{meaning}</td></tr>)}
        </tbody></table>
        <p className="muted">Administrators declare these values when a model is installed and change them later under <strong>Models → Installed → Published properties</strong> (and the context under <strong>Capacity</strong>). They describe the model; the gateway never blocks or rewrites a request because of them. The endpoint needs the same API key as the rest of <code>/v1</code>.</p>
        <h3>One endpoint per kind of model</h3>
        <table><thead><tr><th>Kind</th><th>Endpoint</th><th>Published</th></tr></thead><tbody>
          {modelKinds.map(kind => <tr key={kind.kind}><td><code>{kind.apiType}</code></td><td className="mono">{kind.endpoint}</td>
            <td>{models.filter(item => item.enabled && (item.surface ?? 'OpenAi') === kind.kind).length}</td></tr>)}
        </tbody></table>
      </>}
      <p className="muted">The client sends the LlmProxy API key as <code>Authorization: Bearer</code>. The <code>model</code> field is always the public name and is routed only to models of the same kind. For System One, LlmProxy replaces that credential with the classifier upstream bearer token configured on the server.</p>
    </section>

    <section className="panel formPanel">
      <div className="panelTitle tuningTitle"><h2>Calling from a browser (CORS)</h2><span>Web apps that talk to LlmProxy directly</span></div>
      <Doc example={{ id: 'browser', title: 'Streaming chat from a web page', code: browserExample }} />
      <p>A browser only reaches the API when its origin is allowed. Administrators list site-wide origins and each API key can list its own under <strong>Credentials → Origins</strong> (users manage their personal keys in <strong>My dashboard</strong>). Use <code>scheme://host[:port]</code> without a path, or <code>*</code> for any origin. A key that lists origins is rejected from any other origin.</p>
      <p className="muted">Never ship an administrator or shared key in a public front-end: CORS limits which pages may use a key in a browser, it does not hide the key. Prefer a personal key with its origin listed.</p>
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
        <li><strong>Routing:</strong> the logical model is resolved to healthy enabled deployments. The selected strategy chooses an eligible inference-node deployment.</li>
        <li><strong>Capacity admission:</strong> node/deployment concurrency is acquired locally or through Redis. Saturation returns <code>429 capacity_exhausted</code> before the request reaches vLLM.</li>
        <li><strong>Upstream inference:</strong> the public model name is rewritten to the provider model and the request is proxied. Infrastructure failures may fail over to another deployment.</li>
        <li><strong>Observability:</strong> metadata/TTFT/token metrics feed routing and reports. Exact request/response payloads are kept in a separate application-encrypted request-audit store: administrators can inspect all retained calls, while users can inspect only calls attributable to their own personal API keys.</li>
        <li><strong>Retention:</strong> full-body request-audit payloads are administrator-configurable from 10 days through 11 years (4015 days) and cleaned every four hours; aggregate telemetry has its own independent retention policy.</li>
      </ol>
    </section>

    <section className="panel formPanel">
      <div className="panelTitle tuningTitle"><h2>Security notes</h2><span>What is and is not stored</span></div>
      <p>Authorization headers and upstream bearer tokens are never written to content logs. API-key plaintext is not stored directly: administrators can recover newly created/rotated keys because an encrypted copy is stored alongside the one-way authentication hash.</p>
      <p>Content Logs contain prompts and generated content by explicit platform policy. Access is limited to <code>LlmProxy.Admin</code>, including configured super admins, every API-key reveal is audited, and decrypted payload responses are marked <code>Cache-Control: no-store</code>.</p>
    </section>
  </div>
}

function Doc({ example }: { example: HelpExample }) {
  return <div className={'docCard' + (example.wide ? ' docCardWide' : '')}>
    <h3>{example.title}</h3>
    {example.note && <p className="muted docNote">{example.note}</p>}
    <pre className="payload">{example.code}</pre>
    <CopyButton className="secondary" value={example.code}>Copy</CopyButton>
  </div>
}
