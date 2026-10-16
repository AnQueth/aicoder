import type { FeatureRequest, Page, Product, RunSummary, SkillIdentity } from './types'

let apiKey = ''

export function setApiKey(value: string): void {
  apiKey = value
}

export async function request<T>(
  path: string,
  options: { method?: string; body?: unknown; idempotencyKey?: string; signal?: AbortSignal } = {},
): Promise<T> {
  const headers: Record<string, string> = { 'X-Api-Key': apiKey }
  if (options.body !== undefined) headers['Content-Type'] = 'application/json'
  if (options.idempotencyKey) headers['Idempotency-Key'] = options.idempotencyKey
  let response: Response
  try {
    response = await fetch(`/api/v1${path}`, {
      method: options.method ?? 'GET',
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: options.signal ? AbortSignal.any([options.signal, AbortSignal.timeout(30_000)]) : AbortSignal.timeout(30_000),
      credentials: 'omit',
      cache: 'no-store',
    })
  } catch (error) {
    if (options.signal?.aborted) throw error
    throw new Error('Cannot reach the API. Check that it is running and the development proxy is configured.')
  }
  if (!response.ok) {
    const body = await response.text()
    let detail = body
    if (response.headers.get('content-type')?.includes('json') && body) {
      const problem: { title?: string; detail?: string } = JSON.parse(body)
      detail = problem.detail ?? problem.title ?? body
    }
    throw new Error(`${response.status}: ${detail || response.statusText || 'Request failed'}`)
  }
  if (response.status === 204 || response.headers.get('content-length') === '0') return undefined as T
  const body = await response.text()
  return body ? JSON.parse(body) as T : undefined as T
}

export const segment = (value: string): string => encodeURIComponent(value)
export const skillPath = (skill: SkillIdentity): string =>
  `/agent-skills/${segment(skill.roleId)}/${segment(skill.domain)}/${segment(skill.skillId)}/revisions`

export const products = (offset = 0) => request<Page<Product>>(`/products?offset=${offset}&limit=50`)
export const runs = (offset = 0) => request<Page<RunSummary>>(`/runs?offset=${offset}&limit=50`)
export const submitFeature = (productId: string, body: FeatureRequest, idempotencyKey: string) =>
  request<{ runId: string; status: string }>(`/products/${segment(productId)}/feature-requests`, {
    method: 'POST', body, idempotencyKey,
  })
