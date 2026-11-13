import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { CopyButton, copyText } from '../../../src/LlmProxy.Admin/src/UiPrimitives'

function setClipboard(value: unknown) {
  Object.defineProperty(navigator, 'clipboard', { value, configurable: true })
}

afterEach(() => {
  vi.restoreAllMocks()
  setClipboard(undefined)
  // jsdom has no execCommand; remove anything a test installed.
  delete (document as unknown as Record<string, unknown>).execCommand
})

describe('copyText', () => {
  it('uses the async clipboard when the browser offers it', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    setClipboard({ writeText })
    expect(await copyText('secret')).toBe(true)
    expect(writeText).toHaveBeenCalledWith('secret')
  })

  it('falls back to a selection copy when the clipboard API is missing (plain HTTP)', async () => {
    setClipboard(undefined)
    const execCommand = vi.fn().mockReturnValue(true)
    ;(document as unknown as Record<string, unknown>).execCommand = execCommand
    expect(await copyText('secret')).toBe(true)
    expect(execCommand).toHaveBeenCalledWith('copy')
    expect(document.querySelector('textarea')).toBeNull()
  })

  it('reports false instead of throwing when nothing can copy', async () => {
    setClipboard({ writeText: vi.fn().mockRejectedValue(new Error('denied')) })
    expect(await copyText('secret')).toBe(false)
  })
})

describe('CopyButton', () => {
  it('confirms a successful copy', async () => {
    setClipboard({ writeText: vi.fn().mockResolvedValue(undefined) })
    render(<CopyButton value="abc">Copy</CopyButton>)
    await userEvent.click(screen.getByRole('button', { name: 'Copy' }))
    expect(await screen.findByText('Copied')).toBeInTheDocument()
  })

  it('tells the operator when the browser blocks the copy', async () => {
    setClipboard(undefined)
    render(<CopyButton value="abc">Copy</CopyButton>)
    await userEvent.click(screen.getByRole('button', { name: 'Copy' }))
    expect(await screen.findByText(/Copy is blocked here/)).toBeInTheDocument()
  })
})
