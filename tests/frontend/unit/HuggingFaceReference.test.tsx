import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { looksLikeHuggingFaceLink, parseHuggingFaceReference } from '../../../src/LlmProxy.Admin/src/huggingFaceReference'
import InstallPropertiesEditor, { emptyDeclared, fromSuggestion, parseTokenField, validateDeclared } from '../../../src/LlmProxy.Admin/src/InstallPropertiesEditor'

describe('parseHuggingFaceReference', () => {
  it.each([
    ['https://huggingface.co/meta-models/Muse-Glimmer-30B', 'meta-models/Muse-Glimmer-30B', null],
    ['https://huggingface.co/meta-models/Muse-Glimmer-30B/', 'meta-models/Muse-Glimmer-30B', null],
    ['  https://www.huggingface.co/meta-models/Muse-Glimmer-30B/tree/main?show_file_info=x ', 'meta-models/Muse-Glimmer-30B', null],
    ['huggingface.co/Qwen/Qwen3-8B', 'Qwen/Qwen3-8B', null],
    ['hf.co/Qwen/Qwen3-8B', 'Qwen/Qwen3-8B', null],
    ['hf://Qwen/Qwen3-8B', 'Qwen/Qwen3-8B', null],
    ['Qwen/Qwen3-8B', 'Qwen/Qwen3-8B', null],
    ['meta-models/Muse-Glimmer-30B-GGUF:Q4_K_M', 'meta-models/Muse-Glimmer-30B-GGUF', 'Q4_K_M'],
    ['https://huggingface.co/meta-models/Muse-Glimmer-30B-GGUF/blob/main/Muse-Glimmer-30B-Q4_K_M.gguf', 'meta-models/Muse-Glimmer-30B-GGUF', 'Q4_K_M']
  ])('%s', (input, repository, quantization) => {
    const reference = parseHuggingFaceReference(input)
    expect(reference?.repository).toBe(repository)
    expect(reference?.quantization).toBe(quantization)
    expect(reference?.providerModelName).toBe(quantization ? `${repository}:${quantization}` : repository)
  })

  it.each(['', '   ', 'https://example.com/org/model', 'https://huggingface.co/', 'https://huggingface.co/models', 'just-a-word', 'ftp://huggingface.co/a/b'])(
    'refuses %j', input => expect(parseHuggingFaceReference(input)).toBeNull())

  it('recognises what is worth rewriting as soon as it is pasted', () => {
    expect(looksLikeHuggingFaceLink('https://huggingface.co/a/b')).toBe(true)
    expect(looksLikeHuggingFaceLink('hf.co/a/b')).toBe(true)
    expect(looksLikeHuggingFaceLink('Qwen/Qwen3-8B')).toBe(false)
    expect(looksLikeHuggingFaceLink('Qwen/Qwen3-8B:Q4_K_M')).toBe(false)
  })
})

describe('declared properties', () => {
  it('parses token limits as whole positive numbers', () => {
    expect(parseTokenField('8192')).toBe(8192)
    expect(parseTokenField(' 4096 ')).toBe(4096)
    expect(parseTokenField('0')).toBeNull()
    expect(parseTokenField('12.5')).toBeNull()
    expect(parseTokenField('')).toBeNull()
  })

  it('refuses an output larger than the context window, and accepts missing values', () => {
    expect(validateDeclared({ ...emptyDeclared, contextWindow: 4096, maxOutputTokens: 8192 })).toBe('maxOutputTooLarge')
    expect(validateDeclared({ ...emptyDeclared, contextWindow: 8192, maxOutputTokens: 8192 })).toBeNull()
    expect(validateDeclared({ ...emptyDeclared, contextWindow: null, maxOutputTokens: 8192 })).toBeNull()
  })

  it('turns a suggestion into editable values', () => {
    expect(fromSuggestion({ streaming: true, tools: true, vision: false, reasoning: true, contextWindow: 32768 })).toEqual({
      streaming: true, tools: true, vision: false, reasoning: true, contextWindow: 32768, maxOutputTokens: null
    })
  })
})

describe('InstallPropertiesEditor', () => {
  it('shows where the values come from and reports each change', async () => {
    const onChange = vi.fn()
    render(<InstallPropertiesEditor value={{ ...emptyDeclared, tools: true }} onChange={onChange} onReset={vi.fn()} runtime="vllm" contextWindowCeiling={131072}
      suggestion={{ repository: 'a/b', source: 'Hugging Face repository metadata', evidence: ['The chat template renders tool definitions.'], properties: { ...emptyDeclared } }} />)

    expect(screen.getByText(/Hugging Face repository metadata/)).toBeInTheDocument()
    expect(screen.getByText('The chat template renders tool definitions.')).toBeInTheDocument()
    expect(screen.getByRole('checkbox', { name: /Tool calling/ })).toBeChecked()

    await userEvent.click(screen.getByRole('checkbox', { name: /Image input/ }))
    expect(onChange).toHaveBeenCalledWith('vision', true)
    await userEvent.type(screen.getByLabelText('Context window (tokens)'), '5')
    expect(onChange).toHaveBeenLastCalledWith('contextWindow', 5)
  })

  it('warns when the output is larger than the context and says AirLLM cannot stream', () => {
    render(<InstallPropertiesEditor value={{ ...emptyDeclared, streaming: false, contextWindow: 1000, maxOutputTokens: 2000 }} onChange={vi.fn()} onReset={vi.fn()}
      runtime="airllm" suggestion={null} contextWindowCeiling={8192} />)

    expect(screen.getByRole('alert')).toHaveTextContent('cannot be larger')
    expect(screen.getByText('AirLLM cannot stream; keep this off.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Use detected values' })).toBeDisabled()
  })
})
