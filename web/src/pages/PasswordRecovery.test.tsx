import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { afterEach, vi } from 'vitest'
import { RecoverPassword } from './RecoverPassword'
import { ResetPassword } from './ResetPassword'

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

function Where() {
  const location = useLocation()
  return <p data-testid="where">{`${location.pathname}${location.search}${location.hash}`}</p>
}

function renderAt(entry: string) {
  return render(
    <MemoryRouter initialEntries={[entry]}>
      <Routes>
        <Route path="/recuperar" element={<RecoverPassword />} />
        <Route path="/restablecer" element={<ResetPassword />} />
        <Route path="/ingresar" element={<p>Ingreso</p>} />
      </Routes>
      <Where />
    </MemoryRouter>,
  )
}

afterEach(() => vi.restoreAllMocks())

describe('RecoverPassword', () => {
  it('asks for the link and gives the same answer whatever the address', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 204 }))
    renderAt('/recuperar')

    await user.type(screen.getByLabelText('Correo electrónico'), '  ana@ejemplo.pe ')
    await user.click(screen.getByRole('button', { name: 'Enviar el enlace' }))

    expect(await screen.findByRole('status')).toHaveTextContent('Si la dirección tiene una cuenta activa')
    const [path, init] = fetchMock.mock.calls[0]
    expect(path).toBe('/api/v1/auth/password-reset/request')
    expect(JSON.parse(String(init?.body))).toEqual({ email: 'ana@ejemplo.pe' })
    expect(new Headers(init?.headers).has('Authorization')).toBe(false)
  })

  it('shows the error of the API (for example the limit of requests) and keeps the form', async () => {
    const user = userEvent.setup()
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(429, { title: 'Demasiadas solicitudes', detail: 'Espere un minuto.', code: 'SF-RATE-001' }))
    renderAt('/recuperar')

    await user.type(screen.getByLabelText('Correo electrónico'), 'ana@ejemplo.pe')
    await user.click(screen.getByRole('button', { name: 'Enviar el enlace' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Espere un minuto.')
    expect(screen.getByRole('button', { name: 'Enviar el enlace' })).toBeEnabled()
  })
})

describe('ResetPassword', () => {
  it('reads the token from the fragment, removes it from the address and sends it with the new password', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 204 }))
    renderAt('/restablecer#token=abc%2Bdef_123')

    await waitFor(() => expect(screen.getByTestId('where')).toHaveTextContent(/^\/restablecer$/))
    await user.type(screen.getByLabelText(/Contraseña nueva/), 'una frase larga y única 42')
    await user.type(screen.getByLabelText('Repita la contraseña'), 'una frase larga y única 42')
    await user.click(screen.getByRole('button', { name: 'Cambiar la contraseña' }))

    expect(await screen.findByRole('status')).toHaveTextContent('Su contraseña cambió')
    const [path, init] = fetchMock.mock.calls[0]
    expect(path).toBe('/api/v1/auth/password-reset/confirm')
    expect(JSON.parse(String(init?.body))).toEqual({ token: 'abc+def_123', newPassword: 'una frase larga y única 42' })
    expect(screen.getByRole('link', { name: 'Ingresar' })).toHaveAttribute('href', '/ingresar')
  })

  it('does not send two passwords that differ', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch')
    renderAt('/restablecer#token=t')

    await user.type(screen.getByLabelText(/Contraseña nueva/), 'una frase larga y única 42')
    await user.type(screen.getByLabelText('Repita la contraseña'), 'una frase larga y única 43')
    await user.click(screen.getByRole('button', { name: 'Cambiar la contraseña' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Las dos contraseñas no coinciden.')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('offers a new link when there is no token, and when the API says the token is no good', async () => {
    const user = userEvent.setup()
    const missing = renderAt('/restablecer')
    expect(screen.getByRole('alert')).toHaveTextContent('El enlace no es válido')
    expect(screen.getByRole('link', { name: 'Pedir un enlace nuevo' })).toHaveAttribute('href', '/recuperar')
    missing.unmount()

    vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(400, { title: 'Enlace inválido', detail: 'El enlace de recuperación no es válido o expiró.', code: 'SF-AUTH-009' }))
    renderAt('/restablecer#token=vencido')
    await user.type(screen.getByLabelText(/Contraseña nueva/), 'una frase larga y única 42')
    await user.type(screen.getByLabelText('Repita la contraseña'), 'una frase larga y única 42')
    await user.click(screen.getByRole('button', { name: 'Cambiar la contraseña' }))

    expect(await screen.findByRole('link', { name: 'Pedir un enlace nuevo' })).toBeVisible()
  })

  it('keeps the form and shows the reason when the password is weak', async () => {
    const user = userEvent.setup()
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(400, { title: 'Contraseña débil', detail: 'La contraseña es demasiado repetitiva.', code: 'SF-AUTH-008' }))
    renderAt('/restablecer#token=t')

    await user.type(screen.getByLabelText(/Contraseña nueva/), 'aaaaaaaaaaaaaaaa')
    await user.type(screen.getByLabelText('Repita la contraseña'), 'aaaaaaaaaaaaaaaa')
    await user.click(screen.getByRole('button', { name: 'Cambiar la contraseña' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('demasiado repetitiva')
    expect(screen.getByLabelText(/Contraseña nueva/)).toBeVisible()
  })
})
