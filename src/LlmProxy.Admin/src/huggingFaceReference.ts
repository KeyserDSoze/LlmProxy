/**
 * Turns whatever is pasted for a Hugging Face model into `org/model[:quant]`: a page link (with /tree/main, /blob/...
 * or a query), hf.co/..., hf://... or the plain id. Returns null when it is not a Hugging Face model reference.
 * Mirrors HuggingFaceRepositoryReference on the gateway.
 */
export type HuggingFaceReference = { repository: string; quantization: string | null; providerModelName: string }

const repositoryPart = /^[A-Za-z0-9][A-Za-z0-9_.-]{0,98}$/

function quantizationFromFileName(fileName: string): string | null {
  const match = /(?:^|[-_.])((?:I?Q\d[A-Za-z0-9_]*)|BF16|F16|F32)(?:-\d{5}-of-\d{5})?\.gguf$/i.exec(fileName)
  return match ? match[1].toUpperCase() : null
}

export function parseHuggingFaceReference(input: string): HuggingFaceReference | null {
  let text = input.trim()
  if (!text || text.length > 400) return null
  if (/^hf:\/\//i.test(text)) text = text.slice(5)
  else if (/^(?:https?:\/\/)?(?:www\.)?(?:huggingface\.co|hf\.co)\//i.test(text))
    text = text.replace(/^(?:https?:\/\/)?(?:www\.)?(?:huggingface\.co|hf\.co)\//i, '')
  else if (text.includes('://')) return null

  const cut = text.search(/[?#]/)
  if (cut >= 0) text = text.slice(0, cut)
  let quantization: string | null = null
  const colon = text.indexOf(':')
  if (colon >= 0) { quantization = text.slice(colon + 1).replace(/^\/+|\/+$/g, ''); text = text.slice(0, colon) }

  const parts = text.split('/').filter(Boolean)
  if (parts.length < 2 || !repositoryPart.test(parts[0]) || !repositoryPart.test(parts[1])) return null
  const repository = parts[0] + '/' + parts[1]
  if (!quantization && parts.length >= 5 && (parts[2] === 'blob' || parts[2] === 'resolve') && /\.gguf$/i.test(parts[parts.length - 1]))
    quantization = quantizationFromFileName(parts[parts.length - 1])
  if (quantization !== null && !/^[A-Za-z0-9_.-]{1,50}$/.test(quantization)) quantization = null
  return { repository, quantization, providerModelName: quantization ? repository + ':' + quantization : repository }
}

/** Looks like a link rather than an id: worth rewriting the field as soon as it is pasted. */
export function looksLikeHuggingFaceLink(input: string): boolean {
  return /^(?:https?:\/\/|hf:\/\/|(?:www\.)?(?:huggingface\.co|hf\.co)\/)/i.test(input.trim())
}
