import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { Layout } from './Layout'

const session = vi.hoisted(() => ({
  roles: [] as string[],
  support: null as { tenantName: string; endsAt: number } | null,
  exitSupport: vi.fn(() => Promise.resolve()),
}))

vi.mock('../auth/session', () => ({
  useSession: () => ({
    principal: { userId: 'u1', roles: session.roles },
    hasRole: (...wanted: string[]) => session.roles.some((role) => wanted.includes(role)),
    logout: () => Promise.resolve(),
    support: session.support,
    exitSupport: session.exitSupport,
  }),
}))

function show(roles: string[]) {
  session.roles = roles
  render(
    <QueryClientProvider client={new QueryClient()}>
      <MemoryRouter>
        <Routes>
          <Route element={<Layout />}>
            <Route index element={<p>inicio</p>} />
          </Route>
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

function links(roles: string[]): string[] {
  show(roles)
  return screen.getAllByRole('link').map((link) => link.textContent ?? '')
}

describe('the navigation follows the role', () => {
  beforeEach(() => {
    session.support = null
    session.exitSupport.mockClear()
  })

  it('shows platform staff the platform and no tenant operation', () => {
    expect(links(['PlatformSuperAdmin'])).toEqual(['Inquilinos', 'Planes', 'Precios y comisiones', 'Cobranza', 'Revendedores', 'Correos fallidos', 'Auditoría', 'Seguridad', 'Reglas'])
  })

  it('shows a reseller its accounts and nothing else of the platform or of a tenant', () => {
    expect(links(['ResellerAdmin'])).toEqual(['Mis cuentas', 'Comisiones', 'Marca', 'Seguridad'])
  })

  it('shows a tenant owner the operation, the audit and the failed messages, and no platform', () => {
    const owner = links(['TenantOwner'])

    expect(owner).toEqual(expect.arrayContaining(['Panel', 'Documentos', 'Emitir', 'Usuarios', 'Plan y consumo', 'Auditoría', 'Mensajes fallidos', 'Acceso de soporte']))
    expect(owner).not.toContain('Inquilinos')
  })

  it('hides from a read-only user what only issuers, administrators and auditors use', () => {
    const reader = links(['ReadOnly'])

    expect(reader).toEqual(expect.arrayContaining(['Panel', 'Documentos', 'Clientes']))
    for (const hidden of ['Emitir', 'Resumen diario', 'Usuarios', 'Plan y consumo', 'Auditoría', 'Mensajes fallidos', 'Inquilinos', 'Acceso de soporte']) expect(reader).not.toContain(hidden)
  })

  it('shows an auditor the audit and nothing to issue', () => {
    const auditor = links(['Auditor'])

    expect(auditor).toContain('Auditoría')
    expect(auditor).not.toContain('Emitir')
    expect(auditor).not.toContain('Mensajes fallidos')
  })

  it('shows a person of support inside an account the read-only operation and no way to issue or administer', () => {
    const inside = links(['SupportViewer'])

    expect(inside).toEqual(expect.arrayContaining(['Panel', 'Documentos', 'Clientes']))
    for (const hidden of ['Emitir', 'Usuarios', 'Integraciones', 'Acceso de soporte', 'Inquilinos']) expect(inside).not.toContain(hidden)
  })
})

describe('the mode of support', () => {
  beforeEach(() => {
    session.support = null
    session.exitSupport.mockClear()
  })

  it('says nothing when the person is in their own session', () => {
    show(['TenantOwner'])

    expect(screen.queryByText(/Modo soporte/)).not.toBeInTheDocument()
  })

  it('tells whose account it is, that it only reads and when it ends, and lets the person leave', async () => {
    session.support = { tenantName: 'Emisora SAC', endsAt: Date.now() + 20 * 60_000 }
    show(['SupportViewer'])

    const banner = screen.getByRole('status')
    expect(banner).toHaveTextContent('Está viendo «Emisora SAC» en solo lectura')
    await userEvent.click(screen.getByRole('button', { name: 'Salir del modo soporte' }))
    await waitFor(() => expect(session.exitSupport).toHaveBeenCalledTimes(1))
  })

  it('leaves by itself when the session ends', async () => {
    session.support = { tenantName: 'Emisora SAC', endsAt: Date.now() - 1 }
    show(['SupportViewer'])

    await waitFor(() => expect(session.exitSupport).toHaveBeenCalledTimes(1))
  })
})
