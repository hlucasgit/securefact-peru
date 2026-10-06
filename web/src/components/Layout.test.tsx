import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { Layout } from './Layout'

const session = vi.hoisted(() => ({ roles: [] as string[] }))

vi.mock('../auth/session', () => ({
  useSession: () => ({
    principal: { userId: 'u1', roles: session.roles },
    hasRole: (...wanted: string[]) => session.roles.some((role) => wanted.includes(role)),
    logout: () => Promise.resolve(),
  }),
}))

function links(roles: string[]): string[] {
  session.roles = roles
  render(
    <MemoryRouter>
      <Routes>
        <Route element={<Layout />}>
          <Route index element={<p>inicio</p>} />
        </Route>
      </Routes>
    </MemoryRouter>,
  )
  return screen.getAllByRole('link').map((link) => link.textContent ?? '')
}

describe('the navigation follows the role', () => {
  it('shows platform staff the platform and no tenant operation', () => {
    expect(links(['PlatformSuperAdmin'])).toEqual(['Inquilinos', 'Planes', 'Auditoría', 'Seguridad', 'Reglas'])
  })

  it('shows a tenant owner the operation, the audit and the failed messages, and no platform', () => {
    const owner = links(['TenantOwner'])

    expect(owner).toEqual(expect.arrayContaining(['Panel', 'Documentos', 'Emitir', 'Usuarios', 'Plan y consumo', 'Auditoría', 'Mensajes fallidos']))
    expect(owner).not.toContain('Inquilinos')
  })

  it('hides from a read-only user what only issuers, administrators and auditors use', () => {
    const reader = links(['ReadOnly'])

    expect(reader).toEqual(expect.arrayContaining(['Panel', 'Documentos', 'Clientes']))
    for (const hidden of ['Emitir', 'Resumen diario', 'Usuarios', 'Plan y consumo', 'Auditoría', 'Mensajes fallidos', 'Inquilinos']) expect(reader).not.toContain(hidden)
  })

  it('shows an auditor the audit and nothing to issue', () => {
    const auditor = links(['Auditor'])

    expect(auditor).toContain('Auditoría')
    expect(auditor).not.toContain('Emitir')
    expect(auditor).not.toContain('Mensajes fallidos')
  })
})
