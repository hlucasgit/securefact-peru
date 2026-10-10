import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, vi } from 'vitest'
import { api } from '../api/http'
import type { SupportSession } from '../api/types'
import { SessionProvider, useSession } from './session'

const jwt = (payload: object) => `h.${btoa(JSON.stringify(payload))}.s`
const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const OWN = jwt({ sub: 'staff', role: ['PlatformSupport'] })
const INSIDE = jwt({ sub: 'staff', role: ['SupportViewer'], tid: 't1', sup: 'g1' })
const SESSION: SupportSession = { accessToken: INSIDE, expiresInSeconds: 1800, tenantId: 't1', tenantName: 'Emisora SAC', expiresAt: new Date(Date.now() + 30 * 60_000).toISOString(), readOnly: true }

function Probe() {
  const { principal, support, login, enterSupport, exitSupport } = useSession()
  return (
    <>
      <p>roles: {principal?.roles.join(',') ?? 'ninguno'}</p>
      <p>modo: {support ? support.tenantName : 'propio'}</p>
      <button onClick={() => void login('a@b.pe', 'una clave larga 1')}>entrar a la plataforma</button>
      <button onClick={() => enterSupport(SESSION)}>entrar como soporte</button>
      <button onClick={() => void exitSupport()}>salir</button>
      <button onClick={() => void api('GET', '/api/v1/users')}>leer</button>
    </>
  )
}

function renderProbe() {
  return render(
    <SessionProvider>
      <Probe />
    </SessionProvider>,
  )
}

/** Answers like the API: the login of the person, the logout of the session, and the reads with the token that they carry. */
function serve(onUsers: (authorization: string | null) => Response) {
  return vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    const url = String(input)
    const authorization = (init?.headers as Record<string, string> | undefined)?.Authorization ?? null
    if (url.endsWith('/auth/login')) return Promise.resolve(json(200, { accessToken: OWN, expiresInSeconds: 900 }))
    if (url.endsWith('/auth/refresh')) return Promise.resolve(json(200, { accessToken: OWN, expiresInSeconds: 900 }))
    if (url.endsWith('/auth/logout')) return Promise.resolve(new Response(null, { status: 204 }))
    return Promise.resolve(onUsers(authorization))
  })
}

afterEach(() => {
  vi.restoreAllMocks()
  localStorage.clear()
})

describe('the session inside an account as support', () => {
  it('reads with the token of the account, only as support, and goes back to the own session when the person leaves', async () => {
    const user = userEvent.setup()
    const seen: (string | null)[] = []
    const fetchMock = serve((authorization) => {
      seen.push(authorization)
      return json(200, [])
    })
    renderProbe()
    await user.click(screen.getByRole('button', { name: 'entrar a la plataforma' }))
    await screen.findByText('roles: PlatformSupport')

    await user.click(screen.getByRole('button', { name: 'entrar como soporte' }))
    expect(screen.getByText('roles: SupportViewer')).toBeVisible()
    expect(screen.getByText('modo: Emisora SAC')).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'leer' }))
    await waitFor(() => expect(seen).toEqual([`Bearer ${INSIDE}`]))

    await user.click(screen.getByRole('button', { name: 'salir' }))
    await screen.findByText('roles: PlatformSupport')
    expect(screen.getByText('modo: propio')).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'leer' }))
    await waitFor(() => expect(seen).toEqual([`Bearer ${INSIDE}`, `Bearer ${OWN}`]))
    // The session inside the account was ended on the server before going back.
    const logout = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/auth/logout'))
    expect(logout).toBeDefined()
    expect((logout![1]!.headers as Record<string, string>).Authorization).toBe(`Bearer ${INSIDE}`)
  })

  it('goes back to the own session by itself when the session inside the account is refused: it cannot be renewed', async () => {
    const user = userEvent.setup()
    serve((authorization) => (authorization === `Bearer ${INSIDE}` ? json(401, { code: 'SF-AUTH-002', title: 'Sesión terminada' }) : json(200, [])))
    renderProbe()
    await user.click(screen.getByRole('button', { name: 'entrar a la plataforma' }))
    await screen.findByText('roles: PlatformSupport')
    await user.click(screen.getByRole('button', { name: 'entrar como soporte' }))

    await user.click(screen.getByRole('button', { name: 'leer' }))

    await screen.findByText('roles: PlatformSupport')
    expect(screen.getByText('modo: propio')).toBeVisible()
  })
})
