import { expect, test } from './fixtures.ts'
import { adminCredentials, login, newPassword } from './helpers/api.ts'
import { mailTo } from './helpers/mail.ts'
import { createReseller } from './helpers/resellers.ts'

// The notices go through the queue and leave from the workers (ADR-054); the stack of the tests writes them as files.
test.describe('avisos por correo', () => {
  test('el dueño de una cuenta nueva recibe la bienvenida y, al suspenderla el revendedor, el aviso (sin el motivo)', async () => {
    const stamp = Date.now()
    const reseller = await createReseller('avisos')
    const brand = `Avisos ${stamp}`
    const admin = await login(adminCredentials())
    await admin.put(`/api/v1/platform/resellers/${reseller.id}/branding`, { brandName: brand, primaryColor: '#0b5394', supportEmail: null })

    const api = await login(reseller.admin)
    const tenantName = `Cuenta ${stamp}`
    const tenant = await api.post<{ id: string }>('/api/v1/reseller/tenants', { name: tenantName, environment: 'Sandbox' })
    const owner = { email: `dueno-${stamp}@e2e.test`, password: newPassword() }
    await api.post(`/api/v1/reseller/tenants/${tenant.id}/owner`, { ...owner, displayName: 'Dueño Avisos' })

    await expect.poll(() => mailTo(owner.email).length, { timeout: 30_000 }).toBe(1)
    const welcome = mailTo(owner.email)[0]
    expect(welcome.subject).toContain(`Se creó su cuenta en ${brand}`)
    expect(welcome.from).toContain(brand)
    expect(welcome.body).toContain('/ingresar')
    expect(welcome.body).not.toContain(owner.password)

    await api.post(`/api/v1/reseller/tenants/${tenant.id}/status`, { status: 'Suspended', reason: 'Motivo interno de cobranza' })
    await expect.poll(() => mailTo(owner.email).length, { timeout: 30_000 }).toBe(2)
    const suspended = mailTo(owner.email)[1]
    expect(suspended.subject).toContain('suspendida')
    expect(suspended.subject).toContain(tenantName)
    expect(suspended.body).not.toContain('Motivo interno de cobranza')
  })
})
