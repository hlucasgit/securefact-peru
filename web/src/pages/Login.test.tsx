import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, vi } from 'vitest'
import { SessionProvider, decodeToken } from '../auth/session'
import { Login } from './Login'

const jwt = (payload: object) => `h.${btoa(JSON.stringify(payload))}.s`
const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

function renderLogin() {
  return render(
    <MemoryRouter initialEntries={['/ingresar']}>
      <SessionProvider>
        <Routes>
          <Route path="/ingresar" element={<Login />} />
          <Route path="/" element={<p>Panel</p>} />
        </Routes>
      </SessionProvider>
    </MemoryRouter>,
  )
}

afterEach(() => {
  vi.restoreAllMocks()
  localStorage.clear()
  sessionStorage.clear()
})

describe('decodeToken', () => {
  it('reads the user and the roles, one or many', () => {
    expect(decodeToken(jwt({ sub: 'u1', role: ['TenantOwner'] }))).toEqual({ userId: 'u1', roles: ['TenantOwner'] })
    expect(decodeToken(jwt({ sub: 'u2', role: 'Sales' }))).toEqual({ userId: 'u2', roles: ['Sales'] })
    expect(decodeToken('garbage')).toBeNull()
  })
})

describe('Login', () => {
  it('asks for the second factor when the API requires it and then signs in', async () => {
    const user = userEvent.setup()
    const fetchMock = vi
      .spyOn(globalThis, 'fetch')
      .mockResolvedValueOnce(json(400, { code: 'SF-AUTH-005', title: 'Segundo factor requerido' }))
      .mockResolvedValueOnce(json(200, { accessToken: jwt({ sub: 'u1', role: ['TenantOwner'] }), refreshToken: '', expiresInSeconds: 600 }))
    renderLogin()

    await user.type(screen.getByLabelText('Correo electrónico'), 'a@b.pe')
    await user.type(screen.getByLabelText('Contraseña'), 'secreto-largo-1')
    await user.click(screen.getByRole('button', { name: 'Ingresar' }))
    await user.type(await screen.findByLabelText(/Código de verificación/), '123456')
    await user.click(screen.getByRole('button', { name: 'Ingresar' }))

    expect(await screen.findByText('Panel')).toBeInTheDocument()
    const second = JSON.parse(fetchMock.mock.calls[1][1]!.body as string) as { totpCode: string }
    expect(second.totpCode).toBe('123456')
    // The page asked for the cookie mode and keeps no token of renewal anywhere it could be read: only a hint that it had a session.
    expect((fetchMock.mock.calls[1][1]!.headers as Record<string, string>)['X-SecureFact-Session']).toBe('cookie')
    expect(sessionStorage.length).toBe(0)
    expect(Object.keys(localStorage)).toEqual(['sf.session'])
    expect(JSON.stringify([...Object.values(localStorage), ...Object.values(sessionStorage)])).not.toMatch(/eyJ/)
  })

  it('shows the error of a wrong password without leaving the page', async () => {
    const user = userEvent.setup()
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(401, { code: 'SF-AUTH-001', title: 'Credenciales inválidas', detail: 'Correo o contraseña incorrectos.' }))
    renderLogin()

    await user.type(screen.getByLabelText('Correo electrónico'), 'a@b.pe')
    await user.type(screen.getByLabelText('Contraseña'), 'mala')
    await user.click(screen.getByRole('button', { name: 'Ingresar' }))

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Correo o contraseña incorrectos.'))
    expect(screen.queryByText('Panel')).not.toBeInTheDocument()
  })
})
