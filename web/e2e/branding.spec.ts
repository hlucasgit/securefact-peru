import type { Page } from '@playwright/test'
import { expect, seriousViolations, signIn, test } from './fixtures.ts'
import { adminCredentials, login, newPassword } from './helpers/api.ts'
import { createReseller } from './helpers/resellers.ts'

// A 1x1 PNG: a real image, as small as it gets.
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==', 'base64')

const portalOf = (host: string) => `http://${host}:${new URL(process.env.SF_E2E_WEB_URL ?? 'http://localhost:5173').port}`

async function brandViaApi(resellerId: string, brand: { name: string; color: string }, host?: string): Promise<void> {
  const admin = await login(adminCredentials())
  await admin.put(`/api/v1/platform/resellers/${resellerId}/branding`, { brandName: brand.name, primaryColor: brand.color, supportEmail: 'soporte@marca.e2e.test' })
  if (host) await admin.put(`/api/v1/platform/resellers/${resellerId}/host`, { host })
}

async function rootColor(page: Page): Promise<string> {
  const style = (await page.locator('html').getAttribute('style')) ?? ''
  return /--brand:\s*([^;]+)/.exec(style)?.[1].trim() ?? ''
}

test.describe('marca blanca', () => {
  test('el ingreso en el dominio del revendedor muestra su marca y en cualquier otro dominio la de la plataforma', async ({ page }) => {
    const stamp = Date.now()
    const reseller = await createReseller('marca')
    const host = `marca-${stamp}.e2e.test`
    const name = `Distribuidora ${stamp}`
    await brandViaApi(reseller.id, { name, color: '#0b5394' }, host)
    const admin = await login(adminCredentials())
    await admin.put(`/api/v1/platform/resellers/${reseller.id}/branding/logo`, { dataBase64: PNG.toString('base64') })

    await page.goto(`${portalOf(host)}/ingresar`)
    await expect(page.getByRole('heading', { name })).toBeVisible()
    await expect(page.locator('.login-card img.brand-logo')).toHaveAttribute('src', /\/api\/v1\/branding\/logos\/.+\?v=[0-9a-f]{12}/)
    await expect(page.getByRole('link', { name: 'soporte@marca.e2e.test' })).toBeVisible()
    await expect(page.getByText('Con tecnología SecureFact')).toBeVisible()
    await expect(page).toHaveTitle(name)
    expect(await rootColor(page)).toBe('#0b5394')
    expect(await seriousViolations(page)).toEqual([])

    // Otro dominio no hereda nada.
    await page.goto('/ingresar')
    await expect(page.getByRole('heading', { name: 'SecureFact Perú' })).toBeVisible()
    await expect(page.locator('img.brand-logo')).toHaveCount(0)
    await expect(page).toHaveTitle('SecureFact Perú')
    expect(await rootColor(page)).toBe('')
  })

  test('el revendedor pone su marca desde la interfaz y sus clientes la ven al ingresar', async ({ page, browser }) => {
    const stamp = Date.now()
    const reseller = await createReseller('propia')
    const name = `Marca propia ${stamp}`
    await signIn(page, reseller.admin)
    await page.getByRole('navigation', { name: 'Navegación principal' }).getByRole('link', { name: 'Marca' }).click()
    await expect(page.getByRole('heading', { name: 'Marca', level: 1 })).toBeVisible()

    await page.getByLabel('Nombre de la marca').fill(name)
    await page.locator('#brand-color-text').fill('#ffff00')
    await page.getByRole('button', { name: 'Guardar marca' }).click()
    await expect(page.getByRole('alert')).toContainText('contraste') // el texto blanco no se leería sobre amarillo
    await page.locator('#brand-color-text').fill('#0b5394')
    await page.getByRole('button', { name: 'Guardar marca' }).click()
    await expect(page.locator('.sidebar .brand')).toContainText(name)
    await expect(page).toHaveTitle(name)
    expect(await rootColor(page)).toBe('#0b5394')

    await page.locator('#brand-logo-file').setInputFiles({ name: 'logo.png', mimeType: 'image/png', buffer: PNG })
    await expect(page.getByRole('img', { name: 'Logotipo actual' })).toBeVisible()
    await expect(page.locator('.sidebar img.brand-logo')).toBeVisible()
    await page.locator('#brand-logo-file').setInputFiles({ name: 'logo.svg', mimeType: 'image/svg+xml', buffer: Buffer.from('<svg xmlns="http://www.w3.org/2000/svg"/>') })
    await expect(page.getByRole('alert')).toContainText('SF-BRAND-002')
    expect(await seriousViolations(page)).toEqual([])

    // Una cuenta que abre este revendedor ve la marca al ingresar.
    const api = await login(reseller.admin)
    const tenant = await api.post<{ id: string }>('/api/v1/reseller/tenants', { name: `Cliente de ${name}`, environment: 'Sandbox' })
    const owner = { email: `cliente-${stamp}@e2e.test`, password: newPassword() }
    await api.post(`/api/v1/reseller/tenants/${tenant.id}/owner`, { ...owner, displayName: 'Dueño del cliente' })
    const context = await browser.newContext()
    const customer = await context.newPage()
    await signIn(customer, owner)
    await expect(customer.getByRole('heading', { name: 'Panel' })).toBeVisible()
    await expect(customer.locator('.sidebar .brand')).toContainText(name)
    await expect(customer).toHaveTitle(name)
    await expect(customer.getByRole('link', { name: 'Marca', exact: true })).toHaveCount(0)
    await context.close()

    // Quitar el nombre devuelve la marca de la plataforma.
    await page.getByLabel('Nombre de la marca').fill('')
    await page.getByRole('button', { name: 'Guardar marca' }).click()
    await expect(page.locator('.sidebar .brand')).toContainText('SecureFact Perú')
    expect(await rootColor(page)).toBe('')
  })

  test('la plataforma asigna el dominio de un revendedor, que no puede repetirse', async ({ page }) => {
    const stamp = Date.now()
    const one = await createReseller('uno')
    const two = await createReseller('dos')
    const taken = `ocupado-${stamp}.e2e.test`
    await brandViaApi(two.id, { name: 'Otra marca', color: '#0b5394' }, taken)

    await signIn(page, adminCredentials())
    await expect(page.getByRole('heading', { name: 'Inquilinos' })).toBeVisible()
    await page.getByRole('navigation', { name: 'Navegación principal' }).getByRole('link', { name: 'Revendedores' }).click()
    await page.getByRole('button', { name: `Marca de ${one.name}` }).click()
    const dialog = page.getByRole('dialog')
    await dialog.getByLabel('Dominio').fill(taken)
    await dialog.getByRole('button', { name: 'Guardar dominio' }).click()
    await expect(dialog.getByRole('alert')).toContainText('SF-BRAND-003')

    await dialog.getByLabel('Dominio').fill(`libre-${stamp}.e2e.test`)
    await dialog.getByRole('button', { name: 'Guardar dominio' }).click()
    await expect(dialog.getByLabel('Dominio')).toHaveValue(`libre-${stamp}.e2e.test`)
    expect(await seriousViolations(page)).toEqual([])
  })
})
