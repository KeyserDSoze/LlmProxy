import { describe, expect, it } from 'vitest'
import { formatParameterCount } from '../../../src/LlmProxy.Admin/src/HuggingFaceRepositoryInspector'

describe('Hugging Face parameter counts', () => {
  it.each([
    [30_532_000_000, '30.5B'],
    [8_190_735_360, '8.2B'],
    [70_000_000_000, '70B'],
    [405_000_000_000, '405B'],
    [600_000_000, '600M'],
    [123, '123']
  ])('formats %d as %s', (value, expected) => {
    expect(formatParameterCount(value)).toBe(expected)
  })
})
