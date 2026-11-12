import { describe, expect, it } from 'vitest'
import {
  huggingFaceAccessIssue, huggingFaceRepositoryPage
} from '../../../src/LlmProxy.Admin/src/huggingFaceAccess'

describe('Hugging Face access failures', () => {
  it('recognises a gated or private repository from the Agent message', () => {
    expect(huggingFaceAccessIssue(
      "Preparation before Docker start failed: Hugging Face requires authentication for 'meta/muse-glimmer' (HTTP 401): the repository is private or gated. Accept its license on huggingface.co, add a Hugging Face access token for this server in LlmProxy Admin and retry."
    )).toEqual({ kind: 'required', repository: 'meta/muse-glimmer' })
  })

  it('names the repository, not the file, when a file download is refused', () => {
    expect(huggingFaceAccessIssue(
      "Hugging Face requires authentication for 'model-Q4_K_M.gguf' in 'org/repo' (HTTP 401): the repository is private or gated."
    )).toEqual({ kind: 'required', repository: 'org/repo' })
  })

  it('recognises the gated-license precheck, a rejected token and a missing permission', () => {
    expect(huggingFaceAccessIssue("'org/repo' is a gated Hugging Face repository. Accept its license on huggingface.co, add a token."))
      .toEqual({ kind: 'required', repository: 'org/repo' })
    expect(huggingFaceAccessIssue("Hugging Face rejected the configured access token for 'org/repo' (HTTP 401). Replace it in LlmProxy Admin."))
      .toEqual({ kind: 'rejected', repository: 'org/repo' })
    expect(huggingFaceAccessIssue("Access to 'org/repo' is restricted (HTTP 403). Request access on huggingface.co with the account that owns the access token."))
      .toEqual({ kind: 'forbidden', repository: 'org/repo' })
  })

  it('ignores unrelated failures', () => {
    expect(huggingFaceAccessIssue("No GGUF file in 'org/repo' matches quantization 'Q4_K_M'.")).toBeNull()
    expect(huggingFaceAccessIssue("Hugging Face repository 'org/repo' was not found (HTTP 404).")).toBeNull()
    expect(huggingFaceAccessIssue('')).toBeNull()
    expect(huggingFaceAccessIssue(undefined)).toBeNull()
  })

  it('links only to the canonical repository page', () => {
    expect(huggingFaceRepositoryPage('meta/muse-glimmer')).toBe('https://huggingface.co/meta/muse-glimmer')
  })
})
