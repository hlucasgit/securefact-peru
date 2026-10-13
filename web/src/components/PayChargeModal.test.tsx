import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, vi } from 'vitest'
import type { Charge, PaymentConfig } from '../api/types'
import { PayChargeModal } from './PayChargeModal'

const culqi = vi.hoisted(() => ({ ask: vi.fn() }))

vi.mock('../lib/culqi', async (original) => ({ ...(await original<typeof import('../lib/culqi')>()), askCulqiToken: culqi.ask }))

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const CHARGE = { id: 'c1', period: '2026-11-01', balance: 118, currency: 'PEN', status: 'Pending' } as Charge
const SANDBOX: PaymentConfig = { enabled: true, provider: 'Sandbox', publicKey: null, environment: 'sandbox' }
const CULQI: PaymentConfig = { enabled: true, provider: 'Culqi', publicKey: 'pk_test_AbCdEf0123456789', environment: 'test' }

function renderModal(config: PaymentConfig, onClose = vi.fn()) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>
      <PayChargeModal charge={CHARGE} config={config} onClose={onClose} />
    </QueryClientProvider>,
  )
  return onClose
}

afterEach(() => {
  vi.restoreAllMocks()
  culqi.ask.mockReset()
})

describe('paying a charge with a card', () => {
  it('with the simulator: the person picks what the bank does and what is sent is the token, never a card', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(200, { status: 'Paid', message: 'Pago recibido. Gracias.', payment: { id: 'p1', amount: 118 } }))
    renderModal(SANDBOX)

    expect(screen.getByRole('button', { name: /Pagar S\/\s*118\.00/ })).toBeDisabled() // the e-mail comes first
    await user.type(screen.getByLabelText(/Correo de quien paga/), 'dueno@cliente.pe')
    await user.selectOptions(screen.getByLabelText('Tarjeta de prueba'), 'NOFONDOS')
    await user.click(screen.getByRole('button', { name: /Pagar S\// }))

    expect(await screen.findByText('Pago recibido. Gracias.')).toBeVisible()
    const [url, init] = fetchMock.mock.calls[0]
    expect(String(url)).toBe('/api/v1/charges/c1/pay')
    expect(JSON.parse(String(init?.body))).toEqual({ tokenId: 'tkn_test_NOFONDOS00000000', email: 'dueno@cliente.pe' })
    expect(culqi.ask).not.toHaveBeenCalled()
  })

  it('with Culqi: its checkout gives the token, with the amount in céntimos, and only the token goes to the platform', async () => {
    const user = userEvent.setup()
    culqi.ask.mockResolvedValue('tkn_test_AbCdEf0123456789')
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(200, { status: 'Paid', message: 'Pago recibido. Gracias.', payment: { id: 'p1', amount: 118 } }))
    renderModal(CULQI)

    await user.type(screen.getByLabelText(/Correo de quien paga/), 'dueno@cliente.pe')
    await user.click(screen.getByRole('button', { name: /Pagar S\// }))

    expect(await screen.findByText('Pago recibido. Gracias.')).toBeVisible()
    expect(culqi.ask).toHaveBeenCalledWith({ publicKey: 'pk_test_AbCdEf0123456789', amountCents: 11800, title: expect.stringContaining('2026'), email: 'dueno@cliente.pe' })
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toEqual({ tokenId: 'tkn_test_AbCdEf0123456789', email: 'dueno@cliente.pe' })
  })

  it('says what the bank said when it refuses, and lets the person try again; a payment under review does not', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(json(200, { status: 'Declined', message: 'Su tarjeta no tiene fondos suficientes.', payment: null }))
    renderModal(SANDBOX)
    await user.type(screen.getByLabelText(/Correo de quien paga/), 'dueno@cliente.pe')
    await user.click(screen.getByRole('button', { name: /Pagar S\// }))

    expect(await screen.findByText('Su tarjeta no tiene fondos suficientes.')).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Intentar de nuevo' }))
    fetchMock.mockResolvedValueOnce(json(200, { status: 'UnderReview', message: 'No sabemos si su pago se hizo. No vuelva a pagar.', payment: null }))
    await user.click(screen.getByRole('button', { name: /Pagar S\// }))

    expect(await screen.findByText(/No vuelva a pagar/)).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Intentar de nuevo' })).not.toBeInTheDocument()
  })

  it('shows the error of the API and the one of the checkout when the card could not be read', async () => {
    const user = userEvent.setup()
    culqi.ask.mockRejectedValue(new Error('No se pudo leer la tarjeta. Intente de nuevo.'))
    renderModal(CULQI)
    await user.type(screen.getByLabelText(/Correo de quien paga/), 'dueno@cliente.pe')
    await user.click(screen.getByRole('button', { name: /Pagar S\// }))

    expect(await screen.findByRole('alert')).toHaveTextContent('No se pudo leer la tarjeta')
    expect(screen.getByRole('button', { name: /Pagar S\// })).toBeEnabled()
  })
})
