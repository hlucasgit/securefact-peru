import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, vi } from 'vitest'
import { ToastProvider } from '../components/ui'
import { DeadEmails } from './Audit'

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const DEAD = [{ id: 'e1', toAddress: 'dueno@cliente.pe', subject: 'La cuenta «Uno» fue suspendida', attempts: 10, lastError: 'The SMTP server did not accept the e-mail (SocketException).', createdAt: '2026-10-08T10:00:00Z', deadAt: '2026-10-08T14:00:00Z' }]

function renderPage() {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>
      <ToastProvider>
        <DeadEmails />
      </ToastProvider>
    </QueryClientProvider>,
  )
}

afterEach(() => vi.restoreAllMocks())

describe('DeadEmails', () => {
  it('lists the e-mails that died and sends one again', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
      const url = String(input)
      if (init?.method === 'POST') return Promise.resolve(new Response(null, { status: 204 }))
      return Promise.resolve(json(200, url.endsWith('/dead') ? DEAD : []))
    })
    renderPage()

    expect(await screen.findByText('dueno@cliente.pe')).toBeVisible()
    expect(screen.getByText('La cuenta «Uno» fue suspendida')).toBeVisible()
    expect(screen.getByText(/SocketException/)).toBeVisible()

    await user.click(screen.getByRole('button', { name: /Reenviar a dueno@cliente.pe/ }))

    expect(await screen.findByText('Correo devuelto a la cola.')).toBeVisible()
    const post = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(post?.[0]).toBe('/api/v1/platform/emails/e1/requeue')
  })

  it('says so when no e-mail died', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(200, []))
    renderPage()

    expect(await screen.findByText('No hay correos fallidos.')).toBeVisible()
  })
})
