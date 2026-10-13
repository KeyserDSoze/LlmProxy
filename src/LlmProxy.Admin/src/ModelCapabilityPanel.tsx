import { useState } from 'react'
import { useT } from './uiPreferences'
import type { ModelCapabilityProfile, ModelCapabilityFeature } from './types'

type CapabilityKey = 'chat' | 'toolCalling' | 'streaming' | 'vision' | 'thinking'
const checks: Array<{ key: CapabilityKey; label: 'cap.chat' | 'cap.tools' | 'cap.streaming' | 'cap.vision' | 'cap.thinking'; short: string }> = [
  { key: 'chat', label: 'cap.chat', short: 'Chat' },
  { key: 'toolCalling', label: 'cap.tools', short: 'Tools' },
  { key: 'streaming', label: 'cap.streaming', short: 'SSE' },
  { key: 'vision', label: 'cap.vision', short: 'Vision' },
  { key: 'thinking', label: 'cap.thinking', short: 'Reasoning' }
]
const typedApis: Record<string, { label: string; endpoint: string }> = {
  laya: { label: 'System One', endpoint: '/v1/systemone' },
  kokoro: { label: 'Text to speech', endpoint: '/v1/audio/speech' },
  diffusers: { label: 'Images', endpoint: '/v1/images/generations' },
  'faster-whisper': { label: 'Speech to text', endpoint: '/v1/audio/transcriptions' }
}

function supportKey(status: string): 'cap.supported' | 'cap.unsupported' | 'cap.partial' | 'cap.unverified' | 'cap.unknown' | 'cap.runtimeFeature' {
  if (status === 'supported') return 'cap.supported'
  if (status === 'unsupported') return 'cap.unsupported'
  if (status === 'partial') return 'cap.partial'
  if (status === 'unverified') return 'cap.unverified'
  if (status === 'not-applicable') return 'cap.runtimeFeature'
  return 'cap.unknown'
}

function statusGlyph(status: string) {
  return status === 'supported' ? '✓' : status === 'partial' ? '◐' :
    status === 'unsupported' ? '–' : '?'
}

/** Capabilities are actual serving capabilities, not unverified model-card promises. */
export function ModelCapabilitySummary({ profile }: { profile?: ModelCapabilityProfile | null }) {
  const t = useT()
  if (!profile) return <span className="muted">{t('cap.unknown')}</span>
  const typed = typedApis[profile.runtime]
  if (typed) return <div className="capabilitySummary" aria-label={t('catalog.capabilities')}>
    <span className="capabilityTypeTag">{typed.label}</span>
    <span className="capabilitySummaryNote">{t('cap.typedRuntime')}</span>
  </div>
  return <div className="capabilitySummary" aria-label={t('catalog.capabilities')}>
    {checks.map(({ key, short, label }) => {
      const serving = profile[key].serving
      return <span key={key} className={`capabilityMini capabilityMini--${serving}`}
        title={`${t(label)}: ${t(supportKey(serving))}`}
        aria-label={`${t(label)}: ${t(supportKey(serving))}`}>
        <span aria-hidden="true">{statusGlyph(serving)}</span> {short}
      </span>
    })}
  </div>
}

function FeatureCard({ label, feature }: { label: string; feature: ModelCapabilityFeature }) {
  const t = useT()
  return <article className="capabilityFeature">
    <div className="capabilityFeatureHead">
      <strong>{label}</strong>
      <span className={`capabilityStatus capabilityStatus--${feature.serving}`}>
        <span aria-hidden="true">{statusGlyph(feature.serving)}</span> {t(supportKey(feature.serving))}
      </span>
    </div>
    <div className="capabilityFeatureModel">{t('cap.baseModel')}: {t(supportKey(feature.model))}</div>
    {feature.explanation && <p>{feature.explanation}</p>}
  </article>
}

export function compatibleClientSettings(profile: ModelCapabilityProfile, logicalModel?: string): string {
  const model = logicalModel ?? profile.providerModelName
  const typed = typedApis[profile.runtime]
  if (typed) return JSON.stringify({
    model, endpointType: typed.endpoint,
    ...(profile.runtime === 'kokoro' ? { voice: 'af_heart', response_format: 'wav' } : {}),
    ...(profile.runtime === 'diffusers' ? { size: '512x512', n: 1, response_format: 'b64_json' } : {})
  }, null, 2)
  // Never promote unverified/partial serving support into a client toggle.
  return JSON.stringify({
    model,
    endpointType: 'chat-completions',
    toolCalling: profile.toolCalling.serving === 'supported',
    streaming: profile.streaming.serving === 'supported',
    vision: profile.vision.serving === 'supported',
    thinking: profile.thinking.serving === 'supported',
    maximumInputTokens: profile.servingContextTokens,
    maximumOutputTokens: profile.maximumOutputTokens
  }, null, 2)
}

export default function ModelCapabilityPanel({ profile, logicalModel }: {
  profile: ModelCapabilityProfile; logicalModel?: string
}) {
  const t = useT()
  const [copied, setCopied] = useState(false)
  const typed = typedApis[profile.runtime]
  const settings = compatibleClientSettings(profile, logicalModel)
  return <section className="capabilityProfile" aria-label={t('cap.modalLabel')}>
    <div className="capabilityIntro">
      <div>
        <span className="capabilityEyebrow">{t('cap.servingReality')}</span>
        <h3>{typed ? typed.label : t('cap.chatApi')}</h3>
        <p>{typed ? t('cap.typedExplanation') : t('cap.chatExplanation')}</p>
      </div>
      <div className="capabilityRuntimePill">{t('cap.runtime')} <strong>{profile.runtime}</strong></div>
    </div>
    {typed && <div className="capabilityEndpoint">
      <span>{t('cap.endpoint')}</span><code>POST {typed.endpoint}</code>
    </div>}
    {!typed && <div className="capabilityTokenGrid">
      <div><span>{t('cap.nativeContext')}</span><strong>{profile.nativeContextTokens?.toLocaleString() ?? '—'}</strong></div>
      <div><span>{t('cap.servingLimit')}</span><strong>{profile.servingContextTokens?.toLocaleString() ?? '—'}</strong></div>
      <div><span>{t('cap.maxOutput')}</span><strong>{profile.maximumOutputTokens?.toLocaleString() ?? '—'}</strong></div>
    </div>}
    <div className="capabilityGroupHead">
      <h3>{t('cap.features')}</h3>
      <p>{t('cap.runtimeVsModel')}</p>
    </div>
    {typed ? <>
      {profile.audioTranscription && <div className="capabilityFeatureGrid">
        <FeatureCard label={t('cap.audioTranscription')} feature={profile.audioTranscription}/>
      </div>}
      <details className="capabilityAdvanced">
        <summary>{t('cap.chatNotApplicable')}</summary>
        <div className="capabilityFeatureGrid">
          {checks.map(({ key, label }) => <FeatureCard key={key} label={t(label)} feature={profile[key]}/>)}
        </div>
      </details>
    </> : <div className="capabilityFeatureGrid">
      {checks.map(({ key, label }) => <FeatureCard key={key} label={t(label)} feature={profile[key]}/>)}
      {profile.audioTranscription && <FeatureCard label={t('cap.audioTranscription')} feature={profile.audioTranscription}/>}
    </div>}
    {profile.extendedContextTokens && !typed && <p className="capabilityNote">
      {t('cap.extendedContext')}: {profile.extendedContextTokens.toLocaleString()}
    </p>}
    {profile.notes && <p className="capabilityNote">{profile.notes}</p>}
    <div className="capabilityFooter">
      <div>
        <strong>{t('cap.clientSettings')}</strong>
        <p>{t('cap.clientDisclaimer')}</p>
        <small>{profile.source}</small>
      </div>
      <button type="button" className="secondary" onClick={() => {
        void navigator.clipboard.writeText(settings)
          .then(() => setCopied(true)).catch(() => setCopied(false))
      }}>{copied ? t('cap.copied') : t('cap.copySettings')}</button>
    </div>
    <details className="capabilityAdvanced">
      <summary>{t('cap.showConfig')}</summary>
      <pre className="mono">{settings}</pre>
    </details>
    {profile.modelCardUrl && <a className="capabilityModelCard" href={profile.modelCardUrl}
      target="_blank" rel="noreferrer">{t('cap.modelCard')} ↗</a>}
  </section>
}
