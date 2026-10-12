import type { AdminTestResult } from './types'

export type ChatTestProgress = {
  phase: string
  secondsElapsed: number
  requestId?: string
}

/** Parse bounded SSE diagnostic events, including chunk-split JSON payloads. */
export async function readChatTestStream(
  response: Response, onProgress: (value: ChatTestProgress) => void
): Promise<AdminTestResult> {
  if (!response.body) throw new Error('The browser did not receive a streaming response.')
  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  let outcome: AdminTestResult | null = null
  const maxBufferedEvent = 2 * 1024 * 1024

  const consume = (part: string) => {
    let event = ''
    const data: string[] = []
    for (const line of part.split('\n')) {
      if (line.startsWith('event:')) event = line.slice(6).trim()
      if (line.startsWith('data:')) data.push(line.slice(5).trimStart())
    }
    if (!data.length) return
    let parsed: unknown
    try { parsed = JSON.parse(data.join('\n')) }
    catch { throw new Error('The Gateway sent an invalid diagnostic event.') }
    if (event === 'progress') {
      const progress = parsed as ChatTestProgress
      if (typeof progress.secondsElapsed === 'number' && typeof progress.phase === 'string') onProgress(progress)
    } else if (event === 'result') {
      const final = parsed as AdminTestResult
      if (typeof final.statusCode !== 'number' || typeof final.success !== 'boolean') {
        throw new Error('The Gateway returned an incomplete inference result.')
      }
      outcome = final
    }
  }

  try {
    while (true) {
      const { done, value } = await reader.read()
      buffer += decoder.decode(value ?? new Uint8Array(), { stream: !done }).replaceAll('\r\n', '\n')
      if (buffer.length > maxBufferedEvent) throw new Error('The inference diagnostic response is too large.')
      let separator: number
      while ((separator = buffer.indexOf('\n\n')) !== -1) {
        consume(buffer.slice(0, separator))
        buffer = buffer.slice(separator + 2)
      }
      if (done) {
        if (buffer.trim()) consume(buffer)
        break
      }
    }
  } finally { reader.releaseLock() }
  if (!outcome) throw new Error('Inference stream ended without a result. Check Agent connectivity and retry.')
  return outcome
}
