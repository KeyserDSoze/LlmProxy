import { useState } from 'react'
import type { ModelCapabilityProfile, ModelCapabilityFeature } from './types'

const checks: Array<{ key: 'chat' | 'toolCalling' | 'streaming' | 'vision' | 'thinking'; label: string }> = [
  { key: 'chat', label: 'Chat completions' },
  { key: 'toolCalling', label: 'Tool calling' },
  { key: 'streaming', label: 'Streaming (SSE)' },
  { key: 'vision', label: 'Vision' },
  { key: 'thinking', label: 'Thinking / reasoning' }
]

function supportLabel(value: string): string {
  return ({
    supported: 'Supported', unsupported: 'Not supported',
    partial: 'Partial', unverified: 'Not verified',
    unknown: 'Unknown', 'not-applicable': 'Runtime feature'
  } as Record<string, string>)[value] ?? 'Unknown'
}

function FeatureRow({ label, feature }: { label: string; feature: ModelCapabilityFeature }) {
  return <tr>
    <td><strong>{label}</strong></td>
    <td>{supportLabel(feature.model)}</td>
    <td>{supportLabel(feature.serving)}</td>
    <td className="muted">{feature.explanation}</td>
  </tr>
}

export function compatibleClientSettings(profile: ModelCapabilityProfile): string {
  // Unverified/partial is never promoted to an affirmative checkbox.
  // Do not emit guessed numeric token limits for unknown adapters.
  return JSON.stringify({
    model: profile.providerModelName,
    endpointType: 'chat-completions',
    toolCalling: profile.toolCalling.serving === 'supported',
    streaming: profile.streaming.serving === 'supported',
    vision: profile.vision.serving === 'supported',
    thinking: profile.thinking.serving === 'supported',
    maximumInputTokens: profile.servingContextTokens,
    maximumOutputTokens: profile.maximumOutputTokens
  }, null, 2)
}

export default function ModelCapabilityPanel({ profile }: { profile: ModelCapabilityProfile }) {
  const [copied, setCopied] = useState(false)
  return <section className="stack" aria-label="Model and runtime capabilities">
    <div className="notice">
      <strong>Model capabilities vs. usable runtime API</strong>
      <p>The model card describes the weights; the serving column describes what this installation can expose. Only Supported values should be enabled in external clients. Partial/unverified features require a separate API test.</p>
      <p className="muted">{profile.source} · Runtime: {profile.runtime}</p>
    </div>
    <div className="tableScroll">
      <table>
        <thead><tr><th>Capability</th><th>Base model</th><th>Installed runtime</th><th>Details</th></tr></thead>
        <tbody>{profile.audioTranscription && <FeatureRow label="Audio transcription / English translation" feature={profile.audioTranscription} />}{checks.map(item => <FeatureRow key={item.key} label={item.label} feature={profile[item.key]} />)}</tbody>
      </table>
    </div>
    <p><strong>Context:</strong> {profile.nativeContextTokens?.toLocaleString() ?? 'unknown'} native tokens
      {profile.extendedContextTokens ? ` · ${profile.extendedContextTokens.toLocaleString()} with validated extension configuration` : ''}
      {' · '}<strong>Configured serving limit:</strong> {profile.servingContextTokens?.toLocaleString() ?? 'not verified'} tokens
      {' · '}<strong>Maximum output:</strong> {profile.maximumOutputTokens?.toLocaleString() ?? 'not verified'} tokens.</p>
    <p className="muted">{profile.notes}</p>
    {profile.modelCardUrl && <a href={profile.modelCardUrl} target="_blank" rel="noreferrer">Official model card</a>}
    <div className="actions">
      <button type="button" className="secondary" onClick={() => {
        void navigator.clipboard.writeText(compatibleClientSettings(profile))
          .then(() => setCopied(true)).catch(() => setCopied(false))
      }}>{copied ? 'Copied configuration' : 'Copy suggested client settings'}</button>
    </div>
    <pre className="mono" style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{compatibleClientSettings(profile)}</pre>
    <p className="muted">These settings are guidance for external client forms, not an automatic modification of third-party software. Unknown output/context limits must be verified before use.</p>
  </section>
}
