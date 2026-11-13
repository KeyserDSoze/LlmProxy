import { useT } from './uiPreferences'
import type { ModelProperties, SuggestedModelProperties } from './types'

export type DeclaredProperties = {
  streaming: boolean
  tools: boolean
  vision: boolean
  reasoning: boolean
  contextWindow: number | null
  maxOutputTokens: number | null
}

export const emptyDeclared: DeclaredProperties = {
  streaming: true, tools: false, vision: false, reasoning: false, contextWindow: null, maxOutputTokens: null
}

export function fromSuggestion(properties: ModelProperties): DeclaredProperties {
  return {
    streaming: properties.streaming, tools: properties.tools, vision: properties.vision, reasoning: properties.reasoning,
    contextWindow: properties.contextWindow ?? null, maxOutputTokens: properties.maxOutputTokens ?? null
  }
}

/** Whole numbers only; an empty field means "not declared". */
export function parseTokenField(raw: string): number | null {
  if (!/^\d{1,8}$/.test(raw.trim())) return null
  const value = Number(raw)
  return value >= 1 ? value : null
}

export function validateDeclared(value: DeclaredProperties): string | null {
  if (value.contextWindow !== null && value.maxOutputTokens !== null && value.maxOutputTokens > value.contextWindow)
    return 'maxOutputTooLarge'
  return null
}

/**
 * What API clients will be told about this model on /v1/models/details. It is filled in from the catalog and from the
 * Hugging Face repository, so the operator only reviews it; any value can be changed and is kept when the page re-detects.
 */
export default function InstallPropertiesEditor({ value, onChange, onReset, suggestion, runtime, contextWindowCeiling }: {
  value: DeclaredProperties
  onChange: <K extends keyof DeclaredProperties>(key: K, next: DeclaredProperties[K]) => void
  onReset: () => void
  suggestion: SuggestedModelProperties | null
  runtime: string
  contextWindowCeiling: number
}) {
  const t = useT()
  const problem = validateDeclared(value)
  const toggles: Array<{ key: 'tools' | 'vision' | 'reasoning' | 'streaming'; label: string; help: string }> = [
    { key: 'tools', label: t('props.tools'), help: t('props.toolsHelp') },
    { key: 'vision', label: t('props.vision'), help: t('props.visionHelp') },
    { key: 'reasoning', label: t('props.reasoning'), help: t('props.reasoningHelp') },
    { key: 'streaming', label: t('props.streaming'), help: runtime === 'airllm' ? t('props.streamingAirllm') : t('props.streamingHelp') }
  ]
  return <fieldset className="installProperties">
    <legend>{t('props.title')}</legend>
    <p className="muted">{t('props.intro')}</p>
    {suggestion && <p className="notice" role="status">
      <strong>{t('props.detectedFrom')}:</strong> {suggestion.source}.
      {suggestion.evidence.length > 0 && <ul className="installPropertiesEvidence">
        {suggestion.evidence.map(item => <li key={item}>{item}</li>)}
      </ul>}
    </p>}
    <div className="installPropertiesToggles">
      {toggles.map(toggle => <label key={toggle.key} className="installPropertyToggle">
        <input type="checkbox" checked={value[toggle.key]} onChange={event => onChange(toggle.key, event.target.checked)} />
        <span><strong>{toggle.label}</strong><small>{toggle.help}</small></span>
      </label>)}
    </div>
    <div className="formGridTwo">
      <label>{t('props.contextWindow')}
        <input aria-label={t('props.contextWindow')} inputMode="numeric" value={value.contextWindow ?? ''}
          onChange={event => onChange('contextWindow', parseTokenField(event.target.value))}
          placeholder={String(Math.min(contextWindowCeiling, 32768))} />
      </label>
      <label>{t('props.maxOutput')}
        <input aria-label={t('props.maxOutput')} inputMode="numeric" value={value.maxOutputTokens ?? ''}
          onChange={event => onChange('maxOutputTokens', parseTokenField(event.target.value))} placeholder="4096" />
      </label>
    </div>
    <p className="muted">{t('props.limitsHelp')}</p>
    {problem && <p className="error" role="alert">{t('props.maxOutputTooLarge')}</p>}
    <button type="button" className="secondary compact" disabled={!suggestion} onClick={onReset}>{t('props.reset')}</button>
  </fieldset>
}
