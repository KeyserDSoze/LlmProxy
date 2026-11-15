import { describe, expect, it } from 'vitest'
import {
  contextChoices, defaultSyncPublished, limitsChanged, perRequestTokens, publishedFollowsRuntime, shortTokens, sharesContextPool, stepsWith
} from '../../../src/LlmProxy.Admin/src/capacityOptions'

describe('capacity options', () => {
  it('offers the usual steps up to the model ceiling and keeps an unusual current value', () => {
    expect(contextChoices(32768, 262144)).toEqual([4096, 8192, 16384, 32768, 49152, 65536, 98304, 131072, 196608, 262144])
    expect(contextChoices(12288, 32768)).toEqual([4096, 8192, 12288, 16384, 32768])
  })

  it('adds the ceiling itself when a model stops at a value that is not a usual step', () => {
    expect(contextChoices(8192, 40960)).toEqual([4096, 8192, 16384, 32768, 40960])
    expect(contextChoices(8192, 40960).filter(value => value === 40960)).toHaveLength(1)
  })

  it('keeps a current value above the ceiling visible rather than hiding what is running', () => {
    expect(contextChoices(65536, 32768)).toContain(65536)
  })

  it('never duplicates a step and ignores a missing current value', () => {
    expect(stepsWith([1, 2, 3], 2)).toEqual([1, 2, 3])
    expect(stepsWith([1, 2, 3], null)).toEqual([1, 2, 3])
    expect(stepsWith([1, 2, 3], undefined, 2)).toEqual([1, 2])
  })

  it('divides the pool among slots only on llama.cpp', () => {
    expect(sharesContextPool('llama.cpp')).toBe(true)
    expect(sharesContextPool('vllm')).toBe(false)
    expect(perRequestTokens('llama.cpp', 65536, 4)).toBe(16384)
    expect(perRequestTokens('llama.cpp', 65536, 0)).toBe(65536)
    expect(perRequestTokens('vllm', 65536, 4)).toBe(65536)
    expect(perRequestTokens('sglang', 32768, 8)).toBe(32768)
  })

  it('writes token counts the way people say them', () => {
    expect(shortTokens(512)).toBe('512')
    expect(shortTokens(32768)).toBe('32K')
    expect(shortTokens(49152)).toBe('48K')
    expect(shortTokens(10000)).toBe('9.8K')
  })

  it('knows when anything changed', () => {
    const base = { context: 8192, sequences: 1, output: null, gpu: null }
    expect(limitsChanged(base, { ...base })).toBe(false)
    expect(limitsChanged(base, { ...base, context: 16384 })).toBe(true)
    expect(limitsChanged(base, { ...base, sequences: 2 })).toBe(true)
    expect(limitsChanged(base, { ...base, output: 256 })).toBe(true)
    expect(limitsChanged(base, { ...base, gpu: 0.7 })).toBe(true)
  })

  it('moves the published context only when it followed the runtime and no other server keeps the old size', () => {
    const now = { context: 65536, sequences: 2, output: null, gpu: null }
    // llama.cpp serves 32768 per request here.
    expect(publishedFollowsRuntime(32768, 'llama.cpp', now)).toBe(true)
    expect(publishedFollowsRuntime(65536, 'llama.cpp', now)).toBe(false)
    expect(publishedFollowsRuntime(65536, 'vllm', now)).toBe(true)
    expect(publishedFollowsRuntime(null, 'vllm', now)).toBe(false)
    expect(defaultSyncPublished(65536, 'vllm', now, 0, 0)).toBe(true)
    expect(defaultSyncPublished(65536, 'vllm', now, 1, 0)).toBe(false)
    expect(defaultSyncPublished(65536, 'vllm', now, 1, 1)).toBe(true)
    expect(defaultSyncPublished(40000, 'vllm', now, 0, 0)).toBe(false)
  })
})
