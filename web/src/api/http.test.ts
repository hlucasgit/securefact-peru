import { afterEach, vi } from 'vitest'
import { api, ApiError, setTokenSource } from './http'

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

afterEach(() => {
  vi.restoreAllMocks()
  setTokenSource({ accessToken: () => null, refresh: () => Promise.resolve(false), expired: () => undefined })
})

describe('api', () => {
  it('sends the bearer token and the idempotency key', async () => {
    setTokenSource({ accessToken: () => 'tok', refresh: () => Promise.resolve(false), expired: () => undefined })
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(200, { ok: true }))

    await api('POST', '/api/v1/documents', { body: { a: 1 }, idempotencyKey: 'k1' })

    const init = fetchMock.mock.calls[0][1]!
    expect(init.headers).toMatchObject({ Authorization: 'Bearer tok', 'Idempotency-Key': 'k1', 'Content-Type': 'application/json' })
  })

  it('refreshes once on 401 and repeats the request with the new token', async () => {
    let token = 'old'
    const refresh = vi.fn(() => {
      token = 'new'
      return Promise.resolve(true)
    })
    setTokenSource({ accessToken: () => token, refresh, expired: () => undefined })
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(json(401, {})).mockResolvedValueOnce(json(200, { value: 7 }))

    const result = await api<{ value: number }>('GET', '/api/v1/rules')

    expect(result.value).toBe(7)
    expect(refresh).toHaveBeenCalledTimes(1)
    expect((fetchMock.mock.calls[1][1]!.headers as Record<string, string>).Authorization).toBe('Bearer new')
  })

  it('ends the session when the refresh does not work', async () => {
    const expired = vi.fn()
    setTokenSource({ accessToken: () => 'old', refresh: () => Promise.resolve(false), expired })
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(401, { title: 'No autorizado' }))

    await expect(api('GET', '/api/v1/rules')).rejects.toMatchObject({ status: 401 })
    expect(expired).toHaveBeenCalled()
  })

  it('turns problem details into a typed error with the stable code', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(422, { title: 'Documento inválido', detail: 'La serie no existe.', code: 'SF-BIL-002' }))

    const error = await api('POST', '/api/v1/documents', { body: {} }).catch((failure: unknown) => failure)

    expect(error).toBeInstanceOf(ApiError)
    expect(error).toMatchObject({ status: 422, code: 'SF-BIL-002', message: 'La serie no existe.' })
  })

  it('does not try to refresh an anonymous call', async () => {
    const refresh = vi.fn(() => Promise.resolve(true))
    setTokenSource({ accessToken: () => 'tok', refresh, expired: () => undefined })
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(401, { code: 'SF-AUTH-001' }))

    await expect(api('POST', '/api/v1/auth/login', { body: {}, anonymous: true })).rejects.toMatchObject({ code: 'SF-AUTH-001' })
    expect(refresh).not.toHaveBeenCalled()
  })
})
