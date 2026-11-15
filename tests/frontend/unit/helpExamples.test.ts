import { describe, expect, it } from 'vitest'
import { buildHelpExamples, detailsResponse, helpTabOrder, promptBudgetSnippet, publishedCount, sampleName } from '../../../src/LlmProxy.Admin/src/helpExamples'
import type { Model } from '../../../src/LlmProxy.Admin/src/types'

const model = (over: Partial<Model> & Pick<Model, 'publicName' | 'surface'>): Model => ({
  id: over.publicName, providerModelName: 'org/' + over.publicName, supportsStreaming: true, supportsTools: false, enabled: true, ...over
})

const models: Model[] = [
  model({ publicName: 'disabled-chat', surface: 'OpenAi', enabled: false }),
  model({ publicName: 'agic-code', surface: 'OpenAi', supportsTools: true, supportsVision: true, supportsReasoning: true, contextWindowTokens: 65536, maxOutputTokens: 8192 }),
  model({ publicName: 'bge', surface: 'Embedding', contextWindowTokens: 8192 }),
  model({ publicName: 'sdxl', surface: 'ImageGeneration' })
]

describe('help examples', () => {
  it('uses the name of a model that is really published, never a disabled one, and a placeholder otherwise', () => {
    expect(sampleName(models, 'OpenAi')).toBe('agic-code')
    expect(sampleName(models, 'SpeechSynthesis')).toBe('your-speech-model')
    expect(sampleName([], 'OpenAi')).toBe('your-chat-model')
  })

  it('counts published models per tab, with audio covering speech and transcription', () => {
    const all = [...models, model({ publicName: 'tts', surface: 'SpeechSynthesis' }), model({ publicName: 'stt', surface: 'AudioTranscription' })]
    expect(publishedCount(all, 'text')).toBe(1)
    expect(publishedCount(all, 'audio')).toBe(2)
    expect(publishedCount(all, 'embeddings')).toBe(1)
    expect(publishedCount(all, 'voice')).toBe(0)
  })

  it('shows the values declared on the model exactly as /v1/models/details returns them', () => {
    const entry = JSON.parse(detailsResponse(models[1])).data[0]
    expect(entry).toMatchObject({
      id: 'agic-code', object: 'model', owned_by: 'llmproxy', llmproxy_surface: 'OpenAi', type: 'chat',
      context_window: 65536, max_output_tokens: 8192, input_modalities: ['text', 'image'], output_modalities: ['text'],
      endpoints: ['/v1/chat/completions', '/v1/responses'], capabilities: { streaming: true, tools: true, vision: true, reasoning: true }
    })
  })

  it('reports null for what nobody declared and only describes a context for the kinds that have one', () => {
    const undeclared = JSON.parse(detailsResponse(model({ publicName: 'plain', surface: 'OpenAi' }))).data[0]
    expect(undeclared.context_window).toBeNull()
    expect(undeclared.max_output_tokens).toBeNull()
    expect(undeclared.input_modalities).toEqual(['text'])
    const embedding = JSON.parse(detailsResponse(models[2])).data[0]
    expect(embedding).toMatchObject({ type: 'embedding', context_window: 8192, max_output_tokens: null, output_modalities: ['embedding'], endpoints: ['/v1/embeddings'] })
    const image = JSON.parse(detailsResponse(models[3])).data[0]
    expect(image).toMatchObject({ type: 'image', context_window: null, output_modalities: ['image'] })
    expect(JSON.parse(detailsResponse(undefined)).data[0].id).toBe('your-chat-model')
  })

  it('sizes a prompt from the declared window and tells the reader the gateway does not count tokens', () => {
    const snippet = promptBudgetSnippet('agic-code')
    expect(snippet).toContain("item.id === 'agic-code'")
    expect(snippet).toContain('model.context_window')
    expect(snippet).toContain('model.max_output_tokens')
    expect(snippet).toContain('does not count or cut')
  })

  it('gives every tab at least one example with a unique id and a call a person can run', () => {
    const examples = buildHelpExamples(models)
    const ids = helpTabOrder.flatMap(tab => examples[tab].map(item => item.id))
    expect(helpTabOrder.every(tab => examples[tab].length > 0)).toBe(true)
    expect(new Set(ids).size).toBe(ids.length)
    for (const item of helpTabOrder.flatMap(tab => examples[tab])) {
      expect(item.code.length).toBeGreaterThan(20)
      if (item.code.startsWith('curl')) {
        expect(item.code).toContain('$BASE_URL')
        expect(item.code).toContain('Authorization: Bearer $LLMPROXY_API_KEY')
      }
    }
  })

  it('covers every endpoint the gateway serves with the parameters it actually requires', () => {
    const all = Object.values(buildHelpExamples(models)).flat().map(item => item.code).join('\n')
    for (const path of ['/v1/models', '/v1/models/details?type=chat', '/v1/modalities', '/v1/chat/completions', '/v1/responses',
      '/v1/images/generations', '/v1/audio/speech', '/v1/audio/transcriptions', '/v1/audio/translations', '/v1/embeddings', '/v1/realtime', '/v1/systemone', '/v1/systemone/models'])
      expect(all, path).toContain(path)
    const examples = buildHelpExamples(models)
    const byId = (id: string) => Object.values(examples).flat().find(item => item.id === id)!
    expect(byId('image').code).toContain('"prompt"')
    expect(byId('image').code).toContain('base64 -d')
    expect(byId('speech').code).toContain('"voice":"af_heart"')
    expect(byId('speech').code).toContain('--output speech.wav')
    expect(byId('transcription').code).toContain('-F file=@audio.wav')
    expect(byId('chat-limit').code).toContain('"max_tokens":512')
    expect(byId('responses').note).toContain('max_output_tokens')
    expect(byId('embeddings').code).toContain('"input":["first text","second text"]')
    expect(byId('systemone').code).toContain('X-LlmProxy-Model:')
    expect(byId('realtime').code).toContain('/v1/realtime?model=your-voice-model')
  })

  it('uses the sample name of each kind in its own examples', () => {
    const examples = buildHelpExamples(models)
    expect(examples.text[0].code).toContain('"model":"agic-code"')
    expect(examples.embeddings[0].code).toContain('"model":"bge"')
    expect(examples.images[0].code).toContain('"model":"sdxl"')
    expect(examples.audio[0].code).toContain('"model":"your-speech-model"')
  })
})
