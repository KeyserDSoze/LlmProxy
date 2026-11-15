/** Steps offered in the Capacity dropdowns. Any other value an installation already has is kept as its own entry. */
export const CONTEXT_STEPS = [4096, 8192, 16384, 32768, 49152, 65536, 98304, 131072, 196608, 262144] as const
export const SEQUENCE_STEPS = [1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48, 64, 96, 128] as const
export const OUTPUT_STEPS = [64, 128, 256, 512, 1024, 2048, 4096] as const
export const GPU_FRACTION_STEPS = [0.5, 0.6, 0.7, 0.75, 0.8, 0.85, 0.9, 0.95] as const

/** Media runtimes serve one fixed profile: there is no context, answer length or parallelism to change. */
export const FIXED_PROFILE_RUNTIMES = ['faster-whisper', 'laya', 'kokoro', 'diffusers']

export const POLICY_CONTEXT_CEILING = 262144

export function stepsWith(steps: readonly number[], current: number | null | undefined, ceiling = Number.POSITIVE_INFINITY): number[] {
  const values = new Set(steps.filter(step => step <= ceiling))
  if (current != null && Number.isFinite(current)) values.add(current)
  return [...values].sort((a, b) => a - b)
}

/** Context choices up to the model's own limit; the limit itself is a choice when it is not one of the usual steps. */
export function contextChoices(current: number | null | undefined, ceiling: number): number[] {
  const steps = stepsWith(CONTEXT_STEPS, current, ceiling)
  if (ceiling < POLICY_CONTEXT_CEILING && !steps.includes(ceiling)) steps.push(ceiling)
  return steps.sort((a, b) => a - b)
}

/** llama.cpp splits one context pool among its slots; the other engines give each request the whole context. */
export function sharesContextPool(runtime: string | undefined): boolean {
  return runtime === 'llama.cpp'
}

export function perRequestTokens(runtime: string | undefined, context: number, sequences: number): number {
  return sharesContextPool(runtime) ? Math.floor(context / Math.max(1, sequences)) : context
}

export function shortTokens(value: number): string {
  if (value < 1024) return String(value)
  const thousands = value / 1024
  return (Number.isInteger(thousands) ? String(thousands) : thousands.toFixed(1)) + 'K'
}

export type Limits = { context: number; sequences: number; output: number | null; gpu: number | null }

export function limitsChanged(before: Limits, after: Limits): boolean {
  return before.context !== after.context || before.sequences !== after.sequences
    || before.output !== after.output || before.gpu !== after.gpu
}

/** True when the context clients read on /v1/models/details is the one the runtime serves now, i.e. nobody set it by hand. */
export function publishedFollowsRuntime(published: number | null | undefined, runtime: string | undefined, limits: Limits): boolean {
  return published != null && published === perRequestTokens(runtime, limits.context, limits.sequences)
}

/**
 * Whether to offer to move the published context with the runtime. It stays off when a person chose a different number, and
 * when other servers keep serving this name with the old size: clients must not be promised more than the smallest server gives.
 */
export function defaultSyncPublished(published: number | null | undefined, runtime: string | undefined, current: Limits,
  otherServers: number, otherServersSelected: number): boolean {
  return publishedFollowsRuntime(published, runtime, current) && otherServersSelected === otherServers
}
