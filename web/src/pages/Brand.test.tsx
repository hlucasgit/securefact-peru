import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, vi } from 'vitest'
import { ToastProvider } from '../components/ui'
import { ResellerBrand } from './Brand'

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const SETTINGS = {
  resellerId: 'r1',
  resellerName: 'Canal SAC',
  brandName: 'Distribuidora Andina',
  primaryColor: '#0b5394',
  supportEmail: 'soporte@andina.pe',
  host: null,
  hostStatus: 'None',
  logoUrl: null,
  emailGreeting: 'Gracias por confiar en nosotros.',
  emailClosing: null,
}

function renderPage() {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>
      <MemoryRouter>
        <ToastProvider>
          <ResellerBrand />
        </ToastProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => vi.restoreAllMocks())

describe('the e-mail sentences of the brand', () => {
  it('shows what is saved in a preview that follows what the person types, with the support line and the signature', async () => {
    const user = userEvent.setup()
    vi.spyOn(globalThis, 'fetch').mockImplementation((input) => Promise.resolve(String(input).endsWith('/domain') ? json(200, { resellerId: 'r1', host: null, status: 'None' }) : json(200, SETTINGS)))
    renderPage()

    const preview = await screen.findByLabelText('Vista previa del correo')
    expect(within(preview).getByText('Gracias por confiar en nosotros.')).toBeVisible()
    expect(within(preview).getByText('Si necesita ayuda, escriba a soporte@andina.pe.')).toBeVisible()
    expect(within(preview).getByText('Distribuidora Andina · Con tecnología SecureFact')).toBeVisible()
    await user.type(screen.getByLabelText(/^Cierre/), 'Atendemos de 9 a 18 h.')
    expect(within(preview).getByText('Atendemos de 9 a 18 h.')).toBeVisible()
  })

  it('sends the sentences with the brand, empty ones as none, and says why a refused one was refused', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
      const url = String(input)
      if (init?.method === 'PUT') return Promise.resolve(json(422, { code: 'SF-BRAND-001', title: 'Texto de correo inválido', detail: 'El cierre no lleva direcciones web, correos ni marcas: el contacto es el correo de soporte de su marca.' }))
      return Promise.resolve(url.endsWith('/domain') ? json(200, { resellerId: 'r1', host: null, status: 'None' }) : json(200, SETTINGS))
    })
    renderPage()

    await screen.findByLabelText('Vista previa del correo')
    await user.clear(screen.getByLabelText(/^Saludo/))
    await user.type(screen.getByLabelText(/^Cierre/), 'Visite ofertas.com')
    await user.click(screen.getByRole('button', { name: 'Guardar marca' }))

    expect(await screen.findByText(/no lleva direcciones web/)).toBeVisible()
    const put = fetchMock.mock.calls.find(([, init]) => init?.method === 'PUT')
    expect(JSON.parse(String(put?.[1]?.body))).toEqual({
      brandName: 'Distribuidora Andina',
      primaryColor: '#0b5394',
      supportEmail: 'soporte@andina.pe',
      emailGreeting: null,
      emailClosing: 'Visite ofertas.com',
    })
  })
})
