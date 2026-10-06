// HTTP client of the API: bearer token, one silent refresh on 401, and RFC 9457 problem details as typed errors.

export interface ProblemDetails {
  title?: string
  detail?: string
  status?: number
  code?: string
  traceId?: string
  errors?: Record<string, string[]>
}

export class ApiError extends Error {
  readonly status: number
  readonly code: string | undefined
  readonly problem: ProblemDetails

  constructor(status: number, problem: ProblemDetails) {
    super(problem.detail ?? problem.title ?? `Error ${status}`)
    this.name = 'ApiError'
    this.status = status
    this.code = problem.code
    this.problem = problem
  }
}

/** The token source is a hook-less module so the client works outside React and tests can replace it. */
export interface TokenSource {
  accessToken(): string | null
  /** Resolves true when a new access token is available. */
  refresh(): Promise<boolean>
  /** Called when the session cannot continue (refresh failed). */
  expired(): void
}

let tokens: TokenSource = { accessToken: () => null, refresh: () => Promise.resolve(false), expired: () => undefined }

export function setTokenSource(source: TokenSource): void {
  tokens = source
}

interface RequestOptions {
  body?: unknown
  idempotencyKey?: string
  anonymous?: boolean
  signal?: AbortSignal
}

async function send(method: string, path: string, options: RequestOptions): Promise<Response> {
  const headers: Record<string, string> = {}
  if (options.body !== undefined) headers['Content-Type'] = 'application/json'
  if (options.idempotencyKey) headers['Idempotency-Key'] = options.idempotencyKey
  const token = options.anonymous ? null : tokens.accessToken()
  if (token) headers.Authorization = `Bearer ${token}`
  return fetch(path, { method, headers, body: options.body === undefined ? undefined : JSON.stringify(options.body), signal: options.signal })
}

async function problemOf(response: Response): Promise<ProblemDetails> {
  try {
    return (await response.json()) as ProblemDetails
  } catch {
    return { title: response.statusText, status: response.status }
  }
}

async function execute(method: string, path: string, options: RequestOptions): Promise<Response> {
  let response = await send(method, path, options)
  if (response.status === 401 && !options.anonymous) {
    if (await tokens.refresh()) {
      response = await send(method, path, options)
    }
    if (response.status === 401) {
      tokens.expired()
    }
  }
  if (!response.ok) {
    throw new ApiError(response.status, await problemOf(response))
  }
  return response
}

export async function api<T>(method: string, path: string, options: RequestOptions = {}): Promise<T> {
  const response = await execute(method, path, options)
  if (response.status === 204) return undefined as T
  const text = await response.text()
  return (text ? JSON.parse(text) : undefined) as T
}

export const get = <T>(path: string, signal?: AbortSignal) => api<T>('GET', path, { signal })
export const post = <T>(path: string, body?: unknown, idempotencyKey?: string) => api<T>('POST', path, { body, idempotencyKey })
export const put = <T>(path: string, body: unknown) => api<T>('PUT', path, { body })
export const del = <T>(path: string) => api<T>('DELETE', path)

/** Fetches a binary or text file with the bearer token, for downloads. */
export async function fetchBlob(path: string): Promise<Blob> {
  return (await execute('GET', path, {})).blob()
}

export function newKey(): string {
  return crypto.randomUUID().replaceAll('-', '')
}

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.code ? `${error.message} (${error.code})` : error.message
  return error instanceof Error ? error.message : 'Error inesperado.'
}
