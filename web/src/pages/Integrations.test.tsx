import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, vi } from 'vitest'
import { ToastProvider } from '../components/ui'
import { Integrations } from './Integrations'

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const KEY = { id: 'k1', name: 'Sistema de ventas', role: 'Sales', prefix: 'AbC123', createdAt: '2026-10-01T10:00:00Z', expiresAt: null, lastUsedAt: '2026-10-09T10:00:00Z', revokedAt: null }

const HOOK = {
  id: 'w1',
  url: 'https://hooks.cliente.pe/securefact',
  description: 'ERP',
  events: ['document.accepted', 'document.rejected'],
  isActive: true,
  secretHint: 'x9Zq',
  consecutiveFailures: 0,
  createdAt: '2026-10-01T10:00:00Z',
  disabledAt: null,
  disabledReason: null,
}

function renderPage() {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>
      <ToastProvider>
        <Integrations />
      </ToastProvider>
    </QueryClientProvider>,
  )
}

afterEach(() => vi.restoreAllMocks())

describe('API keys', () => {
  it('lists the keys without their secret and creates one, showing its secret once', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
      const url = String(input)
      if (init?.method === 'POST') return Promise.resolve(json(201, { key: { ...KEY, id: 'k2', name: 'Tienda web', prefix: 'Zz9' }, secret: 'sfk_prueba' }))
      if (url.endsWith('/roles')) return Promise.resolve(json(200, ['BillingAdmin', 'Sales', 'Accountant', 'Auditor', 'ReadOnly']))
      return Promise.resolve(json(200, [KEY]))
    })
    renderPage()

    expect(await screen.findByText('Sistema de ventas')).toBeVisible()
    expect(screen.getByText('sfk_…AbC123')).toBeVisible()
    expect(screen.getByText('Activa')).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Nueva llave' }))
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByLabelText(/Nombre/), 'Tienda web')
    await user.selectOptions(within(dialog).getByLabelText(/Rol/), 'ReadOnly')
    await user.click(within(dialog).getByRole('button', { name: 'Crear llave' }))

    const secret = await screen.findByLabelText('Secreto')
    expect(secret).toHaveTextContent('sfk_prueba')
    expect(screen.getByText(/no se vuelve a mostrar/)).toBeVisible()
    const post = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(post?.[0]).toBe('/api/v1/api-keys')
    expect(JSON.parse(String(post?.[1]?.body))).toEqual({ name: 'Tienda web', role: 'ReadOnly', expiresAt: null })

    await user.click(screen.getByRole('button', { name: 'Ya la guardé' }))
    expect(screen.queryByLabelText('Secreto')).toBeNull()
  })

  it('revokes a key after asking and shows revoked and expired keys as such', async () => {
    const user = userEvent.setup()
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((_input, init) => {
      if (init?.method === 'POST') return Promise.resolve(json(200, { ...KEY, revokedAt: '2026-10-09T12:00:00Z' }))
      return Promise.resolve(
        json(200, [KEY, { ...KEY, id: 'k3', name: 'Vieja', revokedAt: '2026-09-01T10:00:00Z' }, { ...KEY, id: 'k4', name: 'Caducada', expiresAt: '2026-01-01T10:00:00Z' }]),
      )
    })
    renderPage()

    await screen.findByText('Sistema de ventas')
    expect(screen.getByText('Revocada')).toBeVisible()
    expect(screen.getByText('Vencida')).toBeVisible()
    expect(screen.getAllByRole('button', { name: /^Revocar / })).toHaveLength(2) // the revoked key has no button
    await user.click(screen.getByRole('button', { name: 'Revocar Sistema de ventas' }))

    expect(await screen.findByText('Llave revocada.')).toBeVisible()
    expect(fetchMock.mock.calls.some(([url, init]) => init?.method === 'POST' && String(url) === '/api/v1/api-keys/k1/revoke')).toBe(true)
  })

  it('says so when there are no keys', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(200, []))
    renderPage()

    expect(await screen.findByText('Aún no hay llaves.')).toBeVisible()
  })
})

describe('Webhooks', () => {
  async function openWebhooks(user: ReturnType<typeof userEvent.setup>) {
    await user.click(screen.getByRole('tab', { name: 'Webhooks' }))
  }

  it('creates a webhook with the events chosen and shows its secret once', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
      const url = String(input)
      if (init?.method === 'POST') return Promise.resolve(json(201, { endpoint: HOOK, secret: 'whsec_prueba' }))
      if (url.endsWith('/events')) return Promise.resolve(json(200, ['document.issued', 'document.accepted', 'document.rejected']))
      return Promise.resolve(json(200, url.includes('webhooks') ? [] : [KEY]))
    })
    renderPage()
    await openWebhooks(user)

    expect(await screen.findByText('Aún no hay webhooks.')).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Nuevo webhook' }))
    const dialog = await screen.findByRole('dialog')
    await user.type(within(dialog).getByLabelText(/Dirección/), 'https://hooks.cliente.pe/securefact')
    await user.click(await within(dialog).findByLabelText(/Comprobante emitido/))
    await user.click(within(dialog).getByLabelText(/Rechazado por SUNAT/)) // off
    await user.click(within(dialog).getByRole('button', { name: 'Crear webhook' }))

    expect(await screen.findByLabelText('Secreto')).toHaveTextContent('whsec_prueba')
    const post = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(post?.[0]).toBe('/api/v1/webhooks')
    expect(JSON.parse(String(post?.[1]?.body))).toEqual({ url: 'https://hooks.cliente.pe/securefact', description: null, events: ['document.accepted', 'document.issued'], isActive: true })
  })

  it('does not allow a webhook without events', async () => {
    const user = userEvent.setup()
    vi.spyOn(globalThis, 'fetch').mockImplementation((input) => Promise.resolve(json(200, String(input).endsWith('/events') ? ['document.issued', 'document.accepted', 'document.rejected'] : [])))
    renderPage()
    await openWebhooks(user)
    await user.click(await screen.findByRole('button', { name: 'Nuevo webhook' }))
    const dialog = await screen.findByRole('dialog')

    await user.click(within(dialog).getByLabelText(/Aceptado por SUNAT/))
    await user.click(within(dialog).getByLabelText(/Rechazado por SUNAT/))

    expect(within(dialog).getByRole('button', { name: 'Crear webhook' })).toBeDisabled()
  })

  it('lists the webhooks with their state and tries one, saying how it went', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
      const url = String(input)
      if (init?.method === 'POST' && url.endsWith('/test')) return Promise.resolve(json(200, { id: 'd1', state: 'Delivered', lastStatusCode: 200, lastError: null }))
      if (url.endsWith('/api/v1/webhooks')) return Promise.resolve(json(200, [HOOK, { ...HOOK, id: 'w2', url: 'https://otro.cliente.pe/h', isActive: false, disabledReason: 'Falló 40 veces seguidas' }, { ...HOOK, id: 'w3', url: 'https://tercero.cliente.pe/h', consecutiveFailures: 3 }]))
      return Promise.resolve(json(200, []))
    })
    renderPage()
    await openWebhooks(user)

    expect(await screen.findByText('https://hooks.cliente.pe/securefact')).toBeVisible()
    expect(screen.getAllByText('Aceptado por SUNAT, Rechazado por SUNAT', { selector: 'td' })).toHaveLength(3)
    expect(screen.getByText('Apagado')).toBeVisible()
    expect(screen.getByText('Falló 40 veces seguidas')).toBeVisible()
    expect(screen.getByText('Activo, 3 fallos seguidos')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Probar https://otro.cliente.pe/h' })).toBeDisabled()
    await user.click(screen.getByRole('button', { name: 'Probar https://hooks.cliente.pe/securefact' }))

    expect(await screen.findByText('La dirección contestó 200: funciona.')).toBeVisible()
    expect(fetchMock.mock.calls.some(([url, init]) => init?.method === 'POST' && String(url) === '/api/v1/webhooks/w1/test')).toBe(true)
  })

  it('says why a test failed', async () => {
    const user = userEvent.setup()
    vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
      if (init?.method === 'POST') return Promise.resolve(json(200, { id: 'd1', state: 'Failed', lastStatusCode: null, lastError: 'No se pudo conectar con la dirección.' }))
      return Promise.resolve(json(200, String(input).endsWith('/api/v1/webhooks') ? [HOOK] : []))
    })
    renderPage()
    await openWebhooks(user)

    await user.click(await screen.findByRole('button', { name: /^Probar / }))

    expect(await screen.findByText('La prueba falló: No se pudo conectar con la dirección..')).toBeVisible()
  })

  it('rotates the secret and shows the new one, and deletes a webhook, each after asking', async () => {
    const user = userEvent.setup()
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
      const url = String(input)
      if (init?.method === 'POST') return Promise.resolve(json(200, { endpoint: HOOK, secret: 'whsec_nuevo' }))
      if (init?.method === 'DELETE') return Promise.resolve(new Response(null, { status: 204 }))
      return Promise.resolve(json(200, url.endsWith('/api/v1/webhooks') ? [HOOK] : []))
    })
    renderPage()
    await openWebhooks(user)

    await user.click(await screen.findByRole('button', { name: /^Rotar el secreto de / }))
    expect(await screen.findByLabelText('Secreto')).toHaveTextContent('whsec_nuevo')
    await user.click(screen.getByRole('button', { name: 'Ya la guardé' }))
    await user.click(screen.getByRole('button', { name: /^Eliminar https/ }))

    expect(await screen.findByText('Webhook eliminado.')).toBeVisible()
    expect(fetchMock.mock.calls.some(([url, init]) => init?.method === 'POST' && String(url) === '/api/v1/webhooks/w1/rotate-secret')).toBe(true)
    expect(fetchMock.mock.calls.some(([url, init]) => init?.method === 'DELETE' && String(url) === '/api/v1/webhooks/w1')).toBe(true)
  })

  it('shows the deliveries, filters them and sends a dead one again', async () => {
    const user = userEvent.setup()
    const dead = { id: 'e1', endpointId: 'w1', eventId: 'ev1', eventType: 'document.accepted', state: 'Dead', attempts: 8, nextAttemptAt: null, lastStatusCode: 500, lastError: 'HTTP 500', createdAt: '2026-10-08T10:00:00Z', deliveredAt: null }
    const pending = { ...dead, id: 'e2', state: 'Pending', attempts: 0, lastStatusCode: null, lastError: null }
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
      const url = String(input)
      if (init?.method === 'POST') return Promise.resolve(json(200, { ...dead, state: 'Pending' }))
      if (url.includes('/deliveries?state=Dead')) return Promise.resolve(json(200, [dead]))
      if (url.includes('/deliveries')) return Promise.resolve(json(200, [dead, pending]))
      return Promise.resolve(json(200, url.endsWith('/api/v1/webhooks') ? [HOOK] : []))
    })
    renderPage()
    await openWebhooks(user)

    await user.click(await screen.findByRole('button', { name: /^Entregas de / }))
    const dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByText('Agotó los intentos', { selector: 'span' })).toBeVisible()
    expect(within(dialog).getAllByRole('button', { name: /^Reenviar/ })).toHaveLength(1) // the one that waits is not sent twice
    await user.selectOptions(within(dialog).getByLabelText('Estado'), 'Dead')
    await user.click(await within(dialog).findByRole('button', { name: /^Reenviar/ }))

    expect(await screen.findByText('Entrega en cola.')).toBeVisible()
    expect(fetchMock.mock.calls.some(([url, init]) => init?.method === 'POST' && String(url) === '/api/v1/webhooks/deliveries/e1/redeliver')).toBe(true)
  })
})
