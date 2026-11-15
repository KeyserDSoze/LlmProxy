import { modelKinds } from './modelKinds'
import type { Model } from './types'

export type HelpTab = 'discover' | 'text' | 'images' | 'audio' | 'embeddings' | 'voice' | 'systemone'

export type HelpExample = {
  id: string
  title: string
  code: string
  /** What the call does and what to expect back. */
  note?: string
  /** Shown across the whole row: long snippets and JSON. */
  wide?: boolean
}

type Surface = Model['surface']

const placeholder: Record<Surface, string> = {
  OpenAi: 'your-chat-model', ImageGeneration: 'your-image-model', SpeechSynthesis: 'your-speech-model',
  AudioTranscription: 'your-transcription-model', Embedding: 'your-embedding-model', Realtime: 'your-voice-model', SystemOne: 'your-classifier'
}

export const helpTabOrder: readonly HelpTab[] = ['discover', 'text', 'images', 'audio', 'embeddings', 'voice', 'systemone']

/** Which published models belong to each tab, so the tab can say how many there are. */
export const helpTabSurfaces: Record<HelpTab, readonly Surface[]> = {
  discover: [], text: ['OpenAi'], images: ['ImageGeneration'], audio: ['SpeechSynthesis', 'AudioTranscription'],
  embeddings: ['Embedding'], voice: ['Realtime'], systemone: ['SystemOne']
}

export const publishedFirst = (models: Model[], surface: Surface): Model | undefined =>
  models.find(item => item.enabled && (item.surface ?? 'OpenAi') === surface)

/** The name a client would really call: the first enabled model of that kind, otherwise an obvious placeholder. */
export const sampleName = (models: Model[], surface: Surface): string => publishedFirst(models, surface)?.publicName ?? placeholder[surface]

export function publishedCount(models: Model[], tab: HelpTab): number {
  return models.filter(item => item.enabled && helpTabSurfaces[tab].includes(item.surface ?? 'OpenAi')).length
}

const curl = (path: string, extra: string[] = []) =>
  [`curl "$BASE_URL${path}" \\`, '  -H "Authorization: Bearer $LLMPROXY_API_KEY"' + (extra.length ? ' \\' : ''), ...extra].join('\n')

const json = (path: string, body: string, headers: string[] = []) =>
  curl(path, ['  -H "Content-Type: application/json" \\', ...headers.map(header => `  -H "${header}" \\`), `  -d '${body}'`])

/** What GET /v1/models/details returns for one model, built from what is declared on it in Admin. */
export function detailsResponse(model: Model | undefined): string {
  const surface: Surface = model?.surface ?? 'OpenAi'
  const kind = modelKinds.find(item => item.kind === surface) ?? modelKinds[0]
  const chat = surface === 'OpenAi'
  const endpoints = kind.endpoint.split('·').map(part => part.trim().replace(/^(GET|POST) /, '').replace(/ \(WebSocket\)/, ''))
  const input = surface === 'AudioTranscription' ? ['audio'] : surface === 'Realtime' ? ['audio', 'text'] : chat && model?.supportsVision ? ['text', 'image'] : ['text']
  const output = surface === 'ImageGeneration' ? ['image'] : surface === 'SpeechSynthesis' ? ['audio'] : surface === 'Embedding' ? ['embedding'] : surface === 'Realtime' ? ['audio', 'text'] : ['text']
  const entry: Record<string, unknown> = {
    id: model?.publicName ?? placeholder[surface], object: 'model', created: 0, owned_by: 'llmproxy',
    llmproxy_surface: surface, type: kind.apiType,
    context_window: chat || surface === 'Embedding' ? model?.contextWindowTokens ?? null : null,
    max_output_tokens: chat ? model?.maxOutputTokens ?? null : null,
    input_modalities: input, output_modalities: output, endpoints,
    capabilities: { streaming: model?.supportsStreaming ?? true, tools: model?.supportsTools ?? false, vision: model?.supportsVision ?? false, reasoning: model?.supportsReasoning ?? false }
  }
  return JSON.stringify({ object: 'list', data: [entry] }, null, 2)
}

/** A client reads the window once and sizes its prompts from it: the window is prompt + answer together. */
export function promptBudgetSnippet(name: string): string {
  return [
    "const details = await (await fetch(BASE_URL + '/v1/models/details?type=chat', {",
    "  headers: { Authorization: 'Bearer ' + apiKey }",
    '})).json()',
    `const model = details.data.find(item => item.id === '${name}')`,
    '',
    '// context_window is prompt + answer for ONE request. null means nobody declared it: be conservative.',
    'const answer = Math.min(1024, model.max_output_tokens ?? 1024)   // what you will ask for as max_tokens',
    'const promptBudget = (model.context_window ?? 8192) - answer      // tokens you can send',
    '// Count tokens with your own tokenizer; the gateway does not count or cut them for you.'
  ].join('\n')
}

export function buildHelpExamples(models: Model[]): Record<HelpTab, HelpExample[]> {
  const chat = sampleName(models, 'OpenAi')
  const image = sampleName(models, 'ImageGeneration')
  const speech = sampleName(models, 'SpeechSynthesis')
  const stt = sampleName(models, 'AudioTranscription')
  const embedding = sampleName(models, 'Embedding')
  const voice = sampleName(models, 'Realtime')
  const classifier = sampleName(models, 'SystemOne')
  const body = (value: unknown) => JSON.stringify(value)

  return {
    discover: [
      { id: 'list-models', title: 'List models', code: curl('/v1/models'),
        note: 'OpenAI-compatible. Only models that are running and reachable right now are listed; a registered but stopped model answers 503 model_not_running when called.' },
      { id: 'model-details', title: 'Model details and limits', code: curl('/v1/models/details?type=chat'),
        note: 'What each model can do and how much it can hold. Filter with ?type=chat, embedding, image, speech, transcription, realtime or classifier. Send If-None-Match with the ETag to get 304.' },
      { id: 'details-response', wide: true, title: 'What /v1/models/details returns for your model', code: detailsResponse(publishedFirst(models, 'OpenAi')),
        note: 'Built from the values declared on your published model. context_window is the longest prompt plus answer one request can use; max_output_tokens is the largest answer to ask for. null means it was not declared.' },
      { id: 'prompt-budget', wide: true, title: 'Size a prompt from the declared window', code: promptBudgetSnippet(chat) },
      { id: 'modalities', title: 'Everything on offer, by kind', code: curl('/v1/modalities'),
        note: 'Not part of the OpenAI API. One call lists every kind (chat, image, speech, transcription, embedding, realtime, classifier) with the models running for it; a kind with no running model is listed with an empty list.' }
    ],
    text: [
      { id: 'chat-stream', title: 'Chat Completions (streaming)',
        code: json('/v1/chat/completions', body({ model: chat, messages: [{ role: 'user', content: 'Hello' }], stream: true })),
        note: 'Server-sent events, one chunk at a time. Streaming is preserved end to end.' },
      { id: 'chat-limit', title: 'Chat Completions with an answer limit',
        code: json('/v1/chat/completions', body({ model: chat, max_tokens: 512, messages: [{ role: 'system', content: 'Answer briefly.' }, { role: 'user', content: 'Explain this method' }] })),
        note: 'max_tokens must fit inside the context window together with the prompt. An administrator can cap it per key: a cap rewrites the value you sent.' },
      { id: 'chat-tools', wide: true, title: 'Chat Completions with a tool',
        code: json('/v1/chat/completions', body({ model: chat, messages: [{ role: 'user', content: 'Weather in Rome?' }], tools: [{ type: 'function', function: { name: 'get_weather', description: 'Current weather for a city', parameters: { type: 'object', properties: { city: { type: 'string' } }, required: ['city'] } } }] })),
        note: 'Only when capabilities.tools is true for the model on /v1/models/details.' },
      { id: 'responses', title: 'Responses', code: json('/v1/responses', body({ model: chat, input: 'Hello' })),
        note: 'Same routing, capacity and governance as Chat Completions. Here the answer limit is max_output_tokens.' }
    ],
    images: [
      { id: 'image', title: 'Generate an image',
        code: [json('/v1/images/generations', body({ model: image, prompt: 'a red bicycle on a beach', size: '512x512' })) + ' \\', "  | jq -r '.data[0].b64_json' | base64 -d > image.png"].join('\n'),
        note: 'Returns the picture as base64 (b64_json). The size accepted depends on the image model.' }
    ],
    audio: [
      { id: 'speech', title: 'Text to speech',
        code: json('/v1/audio/speech', body({ model: speech, input: 'Hello from LlmProxy', voice: 'af_heart' })) + ' \\\n  --output speech.wav',
        note: 'input and voice are required (up to 4096 characters). The curated Kokoro model installs the af_heart voice and answers audio/wav.' },
      { id: 'transcription', title: 'Speech to text',
        code: curl('/v1/audio/transcriptions', [`  -F model=${stt} \\`, '  -F language=en \\', '  -F response_format=json \\', '  -F file=@audio.wav']),
        note: 'Multipart upload with exactly one file field named file (up to 25 MiB). response_format: json, verbose_json, text, srt or vtt; language is an optional two-letter code.' },
      { id: 'translation', title: 'Speech to English text',
        code: curl('/v1/audio/translations', [`  -F model=${stt} \\`, '  -F file=@audio.wav']),
        note: 'Same upload as transcription; the text comes back translated.' }
    ],
    embeddings: [
      { id: 'embeddings', title: 'Embeddings', code: json('/v1/embeddings', body({ model: embedding, input: ['first text', 'second text'] })),
        note: 'input is a string or an array (up to 2048 items); encoding_format may be float or base64. The body is limited to 4 MiB and each input to the model context shown on /v1/models/details.' }
    ],
    voice: [
      { id: 'realtime', wide: true, title: 'Streaming voice (WebSocket)',
        code: [
          "import WebSocket from 'ws'",
          '',
          "const socket = new WebSocket(BASE_URL.replace(/^http/, 'ws') + '/v1/realtime?model=" + voice + "', {",
          "  headers: { Authorization: 'Bearer ' + apiKey }",
          '})',
          "socket.on('open', () => console.log('connected'))",
          "socket.on('message', frame => console.log(frame.toString()))   // frames arrive exactly as the runtime sends them"
        ].join('\n'),
        note: 'The gateway authenticates, applies limits and routes, then relays frames in both directions without interpreting them: use the runtime\'s own event protocol. A browser WebSocket cannot set the Authorization header, so call this from a server. A model hosted behind a server Agent answers 501 realtime_not_supported_through_agent_relay.' }
    ],
    systemone: [
      { id: 'systemone', wide: true, title: 'System One classifier',
        code: json('/v1/systemone', body({ state: { document: 'duplicate card charge' }, questions: { billing: { type: 'noul', instructions: 'Is this billing?' } } }),
          [`X-LlmProxy-Model: ${classifier}`]),
        note: 'The typed body is forwarded unchanged. Name the model with X-LlmProxy-Model, or rely on the configured default or the only classifier.' },
      { id: 'systemone-models', title: 'List classifiers', code: curl('/v1/systemone/models'),
        note: 'System One models are not listed on /v1/models.' }
    ]
  }
}
