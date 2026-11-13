import { useEffect, useState } from 'react'
import type { TranslationKey } from './uiPreferences'

/**
 * The kinds of model the gateway serves. Each has its own workspace in Admin and its own OpenAI-style endpoint;
 * all of them use the same pages (install, installed, jobs, publishing).
 */
export type ModelKind = 'OpenAi' | 'ImageGeneration' | 'SpeechSynthesis' | 'AudioTranscription' | 'Realtime' | 'Embedding' | 'SystemOne'

export type ModelKindInfo = {
  kind: ModelKind
  label: TranslationKey
  description: TranslationKey
  /** OpenAI-style endpoint(s) clients call for this kind. */
  endpoint: string
  /** Value of `type` on GET /v1/models/details. */
  apiType: string
  icon: 'models' | 'image' | 'speaker' | 'mic' | 'voice' | 'vectors' | 'classifier'
}

export const modelKinds: readonly ModelKindInfo[] = [
  { kind: 'OpenAi', label: 'kind.chat', description: 'kind.chatDesc', endpoint: 'POST /v1/chat/completions · POST /v1/responses', apiType: 'chat', icon: 'models' },
  { kind: 'ImageGeneration', label: 'kind.image', description: 'kind.imageDesc', endpoint: 'POST /v1/images/generations', apiType: 'image', icon: 'image' },
  { kind: 'SpeechSynthesis', label: 'kind.tts', description: 'kind.ttsDesc', endpoint: 'POST /v1/audio/speech', apiType: 'speech', icon: 'speaker' },
  { kind: 'AudioTranscription', label: 'kind.stt', description: 'kind.sttDesc', endpoint: 'POST /v1/audio/transcriptions · POST /v1/audio/translations', apiType: 'transcription', icon: 'mic' },
  { kind: 'Realtime', label: 'kind.voice', description: 'kind.voiceDesc', endpoint: 'GET /v1/realtime (WebSocket)', apiType: 'realtime', icon: 'voice' },
  { kind: 'Embedding', label: 'kind.embedding', description: 'kind.embeddingDesc', endpoint: 'POST /v1/embeddings', apiType: 'embedding', icon: 'vectors' },
  { kind: 'SystemOne', label: 'kind.systemOne', description: 'kind.systemOneDesc', endpoint: 'POST /v1/systemone[/{model}]', apiType: 'classifier', icon: 'classifier' }
]

export const modelKindOrder = modelKinds.map(item => item.kind)

export function kindInfo(kind: ModelKind): ModelKindInfo {
  return modelKinds.find(item => item.kind === kind) ?? modelKinds[0]
}

/** An unknown or legacy `kind` (for example `all`) means the text models workspace. */
export function parseModelKind(value: string | null): ModelKind {
  return (modelKindOrder as readonly string[]).includes(value ?? '') ? value as ModelKind : 'OpenAi'
}

export function modelKindFromLocation(): ModelKind {
  return parseModelKind(new URLSearchParams(window.location.search).get('kind'))
}

export function urlForModelKind(kind: ModelKind, panel?: string): string {
  const params = new URLSearchParams({ kind })
  if (panel) params.set('panel', panel)
  return '/admin/models?' + params.toString()
}

/** The workspace selected in the URL (`?kind=`), following history navigation. */
export function useModelKind(): ModelKind {
  const [kind, setKind] = useState<ModelKind>(modelKindFromLocation)
  useEffect(() => {
    const onChange = () => setKind(modelKindFromLocation())
    window.addEventListener('popstate', onChange)
    return () => window.removeEventListener('popstate', onChange)
  }, [])
  return kind
}

/** Surface of a catalog entry or logical model; models without one are text models. */
export function surfaceOf(surface: string | null | undefined): ModelKind {
  return parseModelKind(surface ?? null)
}
