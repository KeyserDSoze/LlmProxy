import { describe, expect, it } from 'vitest'
import { describeHttpError } from '../../../src/LlmProxy.Admin/src/api'
import { readChatTestStream } from '../../../src/LlmProxy.Admin/src/chatTestStream'

describe('Chat diagnostic stream', () => {
  it('handles events and final JSON split across arbitrary network chunks', async () => {
    const encoding = new TextEncoder()
    const payload = [
      'event: progress\n' + 'data: ' +
        JSON.stringify({ phase: 'waiting_for_model', secondsElapsed: 100 }) + '\n\n',
      'event: result\n' + 'data: ' +
        JSON.stringify({ requestId: 'test-1', success: true, statusCode: 200,
          latencyMilliseconds: 120000, responseBody: JSON.stringify({ ok: true }) }) + '\n\n'
    ].join('')
    const bytes = encoding.encode(payload)
    let index = 0
    const stream = new ReadableStream<Uint8Array>({
      pull(controller) {
        if (index >= bytes.length) { controller.close(); return }
        controller.enqueue(bytes.slice(index, Math.min(index + 13, bytes.length)))
        index += 13
      }
    })
    const messages: number[] = []
    const result = await readChatTestStream(new Response(stream, {
      headers: { 'content-type': 'text/event-stream' }
    }), progress => messages.push(progress.secondsElapsed))
    expect(messages).toEqual([100])
    expect(result.success).toBe(true)
    expect(result.statusCode).toBe(200)
    expect(result.requestId).toBe('test-1')
  })

  it('never interprets a heartbeat as successful model completion', async () => {
    const body = 'event: progress\ndata: {"phase":"waiting_for_model","secondsElapsed":110}\n\n'
    const response = new Response(body, { headers: { 'content-type': 'text/event-stream' } })
    await expect(readChatTestStream(response, () => {})).rejects.toThrow('ended without a result')
  })

  it('replaces Cloudflare HTML with a short actionable error', () => {
    const html = '<!DOCTYPE html><html><title>opencode.zone | 524: A timeout occurred</title><body>' +
      '<div>Cloudflare failure with lots of internal HTML</div></body></html>'
    const error = describeHttpError(524, 'text/html', html)
    expect(error).toContain('Cloudflare 524')
    expect(error).not.toContain('<html>')
    expect(error.length).toBeLessThan(270)
  })

  it('keeps JSON error descriptions readable and bounded', () => {
    expect(describeHttpError(503, 'application/json',
      JSON.stringify({ error: 'no_healthy_deployment', message: 'Start the model first' })))
      .toBe('HTTP 503: Start the model first')
    const error = describeHttpError(502, 'text/plain', 'x'.repeat(1200))
    expect(error.length).toBeLessThan(280)
  })
})
