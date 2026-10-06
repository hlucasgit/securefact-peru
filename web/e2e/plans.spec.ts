import type { Page } from '@playwright/test'
import { expect, seriousViolations, signIn, test } from './fixtures.ts'
import { adminCredentials, newRuc } from './helpers/api.ts'

async function signInAsAdmin(page: Page): Promise<void> {
  await signIn(page, adminCredentials())
  await expect(page.getByRole('heading', { name: 'Inquilinos' })).toBeVisible()
}

async function createPlan(page: Page, plan: { code: string; name: string; companies: string }): Promise<void> {
  await page.getByRole('navigation', { name: 'Navegación principal' }).getByRole('link', { name: 'Planes' }).click()
  await expect(page.getByRole('heading', { name: 'Planes' })).toBeVisible()
  await page.getByRole('button', { name: 'Nuevo plan' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('Código').fill(plan.code)
  await dialog.getByLabel('Nombre').fill(plan.name)
  await dialog.getByLabel('Máximo de empresas').fill(plan.companies)
  await dialog.getByRole('button', { name: 'Crear plan' }).click()
  await expect(page.getByRole('row', { name: new RegExp(plan.name) })).toBeVisible()
}

test.describe('planes y consumo', () => {
  test('un plan con tope de empresas se crea, se asigna, se agota y se amplía, y el propietario lo ve', async ({ page, tenant, browser }) => {
    const stamp = Date.now()
    const plan = { code: `e2e-${stamp}`, name: `Plan e2e ${stamp}`, companies: '1' }
    await signInAsAdmin(page)

    await createPlan(page, plan)
    const row = page.getByRole('row', { name: new RegExp(plan.name) })
    await expect(row).toContainText('Ilimitado') // usuarios y comprobantes: vacío es ilimitado

    // Asignarlo a la cuenta desde su detalle.
    await page.getByRole('link', { name: 'Inquilinos' }).click()
    await page.getByLabel('Buscar por nombre').fill(tenant.name)
    await page.getByRole('link', { name: tenant.name }).click()
    await expect(page.getByRole('heading', { name: 'Plan y consumo' })).toBeVisible()
    await expect(page.getByText('Plan Piloto')).toBeVisible()
    await page.getByLabel('Cambiar a').selectOption({ label: `${plan.name} (${plan.code})` })
    await page.getByRole('button', { name: 'Cambiar plan' }).click()
    await expect(page.getByText(`Plan ${plan.name}`)).toBeVisible()

    // La cuenta usa su única empresa; el propietario ve el límite alcanzado y no puede registrar otra.
    await tenant.api.post('/api/v1/companies', {
      ruc: newRuc(),
      details: { legalName: 'Única SAC', tradeName: null, fiscalAddress: 'Av. Larco 123', ubigeo: '150122', taxRegime: null, contactEmail: null, timeZone: 'America/Lima', defaultCurrency: 'PEN' },
    })
    const context = await browser.newContext()
    const owner = await context.newPage()
    await signIn(owner, tenant.owner)
    await owner.getByRole('link', { name: 'Plan y consumo' }).click()
    await expect(owner.getByText(`Plan ${plan.name}`)).toBeVisible()
    await expect(owner.getByText('Límite alcanzado')).toBeVisible()
    expect(await seriousViolations(owner)).toEqual([])

    await owner.getByRole('link', { name: 'Empresas', exact: true }).click()
    await owner.getByRole('button', { name: 'Nueva empresa' }).click()
    const dialog = owner.getByRole('dialog')
    await dialog.getByLabel('RUC').fill(newRuc())
    await dialog.getByLabel('Razón social').fill('Segunda SAC')
    await dialog.getByLabel('Dirección fiscal').fill('Jr. Cusco 456')
    await dialog.getByLabel('Ubigeo').fill('150101')
    await dialog.getByRole('button', { name: 'Registrar empresa' }).click()
    await expect(dialog.getByRole('alert')).toContainText('SF-PLAN-001')
    await expect(dialog.getByRole('alert')).toContainText(plan.name)
    await owner.keyboard.press('Escape')
    await expect(dialog).toHaveCount(0)

    // Se amplía el tope y la misma operación pasa.
    await page.getByRole('navigation', { name: 'Navegación principal' }).getByRole('link', { name: 'Planes' }).click()
    await page.getByRole('button', { name: `Editar ${plan.name}` }).click()
    await page.getByRole('dialog').getByLabel('Máximo de empresas').fill('2')
    await page.getByRole('dialog').getByRole('button', { name: 'Guardar' }).click()
    await expect(page.getByRole('row', { name: new RegExp(plan.name) })).toContainText('2')

    await owner.getByRole('button', { name: 'Nueva empresa' }).click()
    await owner.getByRole('dialog').getByLabel('RUC').fill(newRuc())
    await owner.getByRole('dialog').getByLabel('Razón social').fill('Segunda SAC')
    await owner.getByRole('dialog').getByLabel('Dirección fiscal').fill('Jr. Cusco 456')
    await owner.getByRole('dialog').getByLabel('Ubigeo').fill('150101')
    await owner.getByRole('dialog').getByRole('button', { name: 'Registrar empresa' }).click()
    await expect(owner.getByRole('heading', { name: 'Segunda SAC' })).toBeVisible()
    await context.close()

    // El cambio de plan queda en la auditoría de la cuenta.
    await page.getByRole('link', { name: 'Inquilinos' }).click()
    await page.getByLabel('Buscar por nombre').fill(tenant.name)
    await page.getByRole('link', { name: tenant.name }).click()
    await page.getByRole('link', { name: 'Ver la auditoría de esta cuenta' }).click()
    await expect(page.getByRole('row', { name: /tenancy\.tenant\.plan_changed/ })).toBeVisible()
  })

  test('el catálogo de planes no tiene fallas de accesibilidad y el propietario no lo ve', async ({ page, tenant, browser }) => {
    await signInAsAdmin(page)
    await page.getByRole('navigation', { name: 'Navegación principal' }).getByRole('link', { name: 'Planes' }).click()
    await expect(page.getByRole('heading', { name: 'Planes' })).toBeVisible()
    await expect(page.getByRole('row', { name: /Piloto/ })).toBeVisible()
    expect(await seriousViolations(page)).toEqual([])
    await page.getByRole('button', { name: 'Nuevo plan' }).click()
    expect(await seriousViolations(page)).toEqual([])
    await page.getByRole('button', { name: 'Cancelar' }).click()

    // El propietario de una cuenta no tiene la entrada de planes ni los ve por la dirección.
    const context = await browser.newContext()
    const owner = await context.newPage()
    await signIn(owner, tenant.owner)
    await expect(owner.getByRole('heading', { name: 'Panel' })).toBeVisible()
    await expect(owner.getByRole('navigation', { name: 'Navegación principal' }).getByRole('link', { name: 'Planes' })).toHaveCount(0)
    await owner.goto('/plataforma/planes')
    await expect(owner.getByRole('alert')).toBeVisible()
    await context.close()
  })
})
