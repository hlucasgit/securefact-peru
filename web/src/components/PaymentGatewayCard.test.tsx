import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, vi } from 'vitest'
import { ToastProvider } from './ui'
import { PaymentGatewayCard } from './PaymentGatewayCard'

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const SETTINGS = { provider: 'Culqi', publicKey: 'pk_test_AbCdEf0123456789', hasSecretKey: true, environment: 'test', enabled: true, updatedAt: '2026-10-12T10:00:00Z' }
const ATTEMPT = { id: 'a1', tenantId: 't1', tenantName: 'Ferretería El Sol SAC', chargeId: 'c1', period: '2026-11', amount: 118, state: 'UnderReview', gatewayChargeId: null, code: 'no-answer', message: 'La pasarela no respondió y no se sabe si el pago se hizo.', createdAt: '2026-11-03T10:00:00Z', completedAt: '2026-11-03T10:00:30Z', resolution: null }

function renderCard(canManage = true) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>
      <ToastProvider>
        <PaymentGatewayCard canManage={canManage} />
      </ToastProvider>
    </QueryClientProvider>,
  )
}

afterEach(() => vi.restoreAllMocks())

describe('the gateway of the platform', () => {
  it('never shows the secret key that is stored, keeps it when the field is left empty and sends a new one when it is typed', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
      const url = String(input)
      if (init?.method === 'PUT') return Promise.resolve(json(200, SETTINGS))
      return Promise.resolve(json(200, url.endsWith('payment-attempts') ? [] : SETTINGS))
    })
    renderCard()

    const secret = await screen.findByLabelText(/^Llave secreta/)
    expect(secret).toHaveValue('')
    expect(screen.getByLabelText(/^Llave pública/)).toHaveValue('pk_test_AbCdEf0123456789')
    await user.click(screen.getByRole('button', { name: 'Guardar pasarela' }))
    expect(await screen.findByText('Pasarela guardada.')).toBeVisible()
    expect(JSON.parse(String(fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')?.[1]?.body))).toEqual({ provider: 'Culqi', publicKey: 'pk_test_AbCdEf0123456789', secretKey: null, enabled: true })

    await user.type(secret, 'sk' + '_test_ZyXwVu9876543210')
    await user.click(screen.getByRole('button', { name: 'Guardar pasarela' }))
    await vi.waitFor(() => expect(fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')).toHaveLength(2))
    expect(JSON.parse(String(fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT')[1][1]?.body)).secretKey).toBe('sk' + '_test_ZyXwVu9876543210')
  })

  it('is read only for the one who cannot manage it', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation((input) => Promise.resolve(json(200, String(input).endsWith('payment-attempts') ? [] : SETTINGS)))
    renderCard(false)

    expect(await screen.findByLabelText(/^Llave pública/)).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Guardar pasarela' })).not.toBeInTheDocument()
    expect(await screen.findByText('No hay pagos por revisar.')).toBeVisible()
  })

  it('lists the payments to review and closes one with a note', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
      const url = String(input)
      if (init?.method === 'POST') return Promise.resolve(json(200, { ...ATTEMPT, state: 'Resolved', resolution: 'Revisado' }))
      return Promise.resolve(json(200, url.endsWith('payment-attempts') ? [ATTEMPT] : SETTINGS))
    })
    renderCard()

    const row = await screen.findByRole('row', { name: /Ferretería El Sol SAC/ })
    expect(within(row).getByText(/no se sabe si el pago se hizo/)).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Revisar el pago de Ferretería El Sol SAC' }))
    await user.type(screen.getByLabelText('Qué se revisó y qué se hizo'), 'Revisado en el panel: no hubo cobro')
    await user.click(screen.getByRole('button', { name: 'Cerrar' }))

    expect(await screen.findByText('Intento cerrado.')).toBeVisible()
    const post = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(String(post?.[0])).toBe('/api/v1/platform/payment-attempts/a1/resolve')
    expect(JSON.parse(String(post?.[1]?.body))).toEqual({ note: 'Revisado en el panel: no hubo cobro' })
  })
})
