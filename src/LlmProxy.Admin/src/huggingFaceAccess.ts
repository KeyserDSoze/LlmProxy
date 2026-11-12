export type HuggingFaceAccessIssue = {
  kind: 'required' | 'rejected' | 'forbidden'
  repository?: string
}

/**
 * Recognises the Agent's Hugging Face access failures (gated/private repository, rejected token,
 * missing permission) so Admin can guide the operator instead of showing only the error text.
 */
export function huggingFaceAccessIssue(text: string | null | undefined): HuggingFaceAccessIssue | null {
  if (!text) return null
  const sentence = text.split(/\r?\n/).find(line => /Hugging Face|huggingface\.co/i.test(line)) ?? ''
  const quoted = [...sentence.matchAll(/'([^']+)'/g)].map(match => match[1])
  // "'file' in 'org/repo'" names the repository last; "'org/repo' is gated" names it alone.
  const repository = quoted.reverse().find(value => /^[\w.-]+\/[\w.-]+$/.test(value))

  if (/rejected the configured access token/i.test(sentence)) return { kind: 'rejected', repository }
  if (/\(HTTP 403\)/.test(sentence) && /restricted/i.test(sentence)) return { kind: 'forbidden', repository }
  if (/requires authentication for/i.test(sentence) || /is a gated Hugging Face repository/i.test(sentence))
    return { kind: 'required', repository }
  return null
}

export const huggingFaceTokenPage = 'https://huggingface.co/settings/tokens/new?tokenType=read'

export function huggingFaceRepositoryPage(repository: string): string {
  return 'https://huggingface.co/' + repository.split('/').map(encodeURIComponent).join('/')
}
