import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, vi } from 'vitest'
import { ToastProvider } from '../components/ui'
import { EnterSupportCard, SupportAccess } from './SupportAccess'

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const enterSupport = vi.hoisted(() => vi.fn())

vi.mock('../auth/session', () => ({ useSession: () => ({ enterSupport }) }))

const ACTIVE = { id: 'g1', createdAt: '2026-10-10T10:00:00Z', expiresAt: '2026-10-10T14:00:00Z', revokedAt: null, note: 'Ayuda con las series', status: 'Active', entries: 2, lastEntryAt: '2026-10-10T11:00:00Z' }
const OLD = { id: 'g0', createdAt: '2026-10-01T10:00:00Z', expiresAt: '2026-10-01T11:00:00Z', revokedAt: null, note: null, status: 'Expired', entries: 0, lastEntryAt: null }

function renderWith(element: React.ReactNode) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}>
      <MemoryRouter>
        <ToastProvider>{element}</ToastProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  vi.restoreAllMocks()
  enterSupport.mockClear()
})

describe('the authorization of the owner', () => {
  it('lists what was authorized, with the entries, and takes the active one back', async () => {
    const user = userEvent.setup()
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((_input, init) =>
      Promise.resolve(init?.method === 'POST' ? json(200, { ...ACTIVE, status: 'Revoked', revokedAt: '2026-10-10T12:00:00Z' }) : json(200, [ACTIVE, OLD])),
    )
    renderWith(<SupportAccess />)

    expect(await screen.findByText('Ayuda con las series')).toBeVisible()
    expect(screen.getByText('Vigente')).toBeVisible()
    expect(screen.getByText('Vencida')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Autorizar acceso' })).toBeDisabled() // one at a time
    await user.click(screen.getByRole('button', { name: 'Quitar la autorización de soporte' }))

    expect(await screen.findByText('Autorización quitada.')).toBeVisible()
    expect(fetchMock.mock.calls.some(([url, init]) => String(url).endsWith('/api/v1/support-access/g1/revoke') && init?.method === 'POST')).toBe(true)
  })

  it('authorizes for the time that the owner picks with the note, and nothing is sent without it being asked', async () => {
    const user = userEvent.setup()
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((_input, init) => Promise.resolve(init?.method === 'POST' ? json(201, ACTIVE) : json(200, [])))
    renderWith(<SupportAccess />)

    expect(await screen.findByText('Nunca autorizó el acceso de soporte.')).toBeVisible()
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)
    await user.click(screen.getByRole('button', { name: 'Autorizar acceso' }))
    const dialog = await screen.findByRole('dialog')
    await user.selectOptions(within(dialog).getByLabelText('Durante'), '24')
    await user.type(within(dialog).getByLabelText(/Nota/), 'Capacitación')
    await user.click(within(dialog).getByRole('button', { name: 'Autorizar' }))

    expect(await screen.findByText('Acceso autorizado.')).toBeVisible()
    const post = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(JSON.parse(String(post?.[1]?.body))).toEqual({ hours: 24, note: 'Capacitación' })
  })
})

describe('the entry of the person who supports', () => {
  it('offers the entry only to an account that authorized it, asks for the reason and enters with it', async () => {
    const user = userEvent.setup()
    const session = { accessToken: 'jwt', expiresInSeconds: 1800, tenantId: 't1', tenantName: 'Emisora SAC', expiresAt: '2026-10-10T12:30:00Z', readOnly: true }
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation((_input, init) =>
      Promise.resolve(init?.method === 'POST' ? json(200, session) : json(200, [{ tenantId: 't1', tenantName: 'Emisora SAC', grantId: 'g1', expiresAt: '2026-10-10T14:00:00Z' }])),
    )
    renderWith(<EnterSupportCard tenantId="t1" />)

    await user.click(await screen.findByRole('button', { name: 'Entrar como soporte' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByRole('button', { name: 'Entrar' })).toBeDisabled() // without a reason it does not go
    await user.type(within(dialog).getByLabelText(/Motivo/), 'Revisar las series')
    await user.click(within(dialog).getByRole('button', { name: 'Entrar' }))

    await vi.waitFor(() => expect(enterSupport).toHaveBeenCalledWith(session))
    const post = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST')
    expect(JSON.parse(String(post?.[1]?.body))).toEqual({ tenantId: 't1', reason: 'Revisar las series' })
  })

  it('says that the account did not authorize it and gives no way in', async () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(() => Promise.resolve(json(200, [{ tenantId: 'other', tenantName: 'Otra SAC', grantId: 'g9', expiresAt: '2026-10-10T14:00:00Z' }])))
    renderWith(<EnterSupportCard tenantId="t1" />)

    expect(await screen.findByText(/no autorizó el acceso de soporte/)).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Entrar como soporte' })).not.toBeInTheDocument()
  })
})
