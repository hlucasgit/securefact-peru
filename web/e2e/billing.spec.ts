import type { Page } from '@playwright/test'
import { expect, seriousViolations, signIn, test } from './fixtures.ts'
import { adminCredentials, createTenant, newRuc } from './helpers/api.ts'
import { createReseller } from './helpers/resellers.ts'

const nav = (page: Page) => page.getByRole('navigation', { name: 'Navegación principal' })

async function signInAsAdmin(page: Page): Promise<void> {
  await signIn(page, adminCredentials())
  await expect(page.getByRole('heading', { name: 'Inquilinos' })).toBeVisible()
}

test.describe('precios, cobranza y comisiones', () => {
  test('un plan que cobra el excedente recibe su precio, se asigna y la cuenta ve lo que paga', async ({ page, tenant, browser }) => {
    const stamp = Date.now()
    const plan = { code: `e2e-${stamp}`, name: `Plan medido ${stamp}` }
    await signInAsAdmin(page)

    // El plan se crea cobrando el excedente: el modo no cambia después.
    await nav(page).getByRole('link', { name: 'Planes' }).click()
    await page.getByRole('button', { name: 'Nuevo plan' }).click()
    let dialog = page.getByRole('dialog')
    await dialog.getByLabel('Código').fill(plan.code)
    await dialog.getByLabel('Nombre').fill(plan.name)
    await dialog.getByLabel(/Cobrar los comprobantes que pasan/).check()
    await dialog.getByRole('button', { name: 'Crear plan' }).click()
    await expect(page.getByRole('row', { name: new RegExp(plan.name) })).toContainText('Se cobra')
    await page.getByRole('button', { name: `Editar ${plan.name}` }).click()
    await expect(page.getByRole('dialog')).toContainText('No cambia después de crear el plan')
    await page.getByRole('button', { name: 'Cancelar' }).click()

    // Su precio: una versión que rige desde el mes que viene.
    await nav(page).getByRole('link', { name: 'Precios y comisiones' }).click()
    await expect(page.getByRole('heading', { name: 'Precios y comisiones' })).toBeVisible()
    await page.getByLabel('Plan', { exact: true }).selectOption({ label: `${plan.name} (${plan.code})` })
    await expect(page.getByText('Este plan aún no tiene precio')).toBeVisible()
    await page.getByRole('button', { name: 'Publicar una versión' }).click()
    dialog = page.getByRole('dialog')
    await dialog.getByLabel(/Cuota mensual/).fill('99.90')
    await dialog.getByLabel(/Comprobantes incluidos/).fill('200')
    await dialog.getByLabel(/Precio de cada comprobante adicional/).fill('0.15')
    await dialog.getByLabel('Nota').fill('Lanzamiento')
    expect(await seriousViolations(page)).toEqual([])
    await dialog.getByRole('button', { name: 'Publicar precio' }).click()
    await expect(page.getByRole('row', { name: /Lanzamiento/ })).toContainText('S/ 99.90')

    // Una fecha que no es el primer día del mes lo rechaza el servidor, con su código.
    await page.getByRole('button', { name: 'Publicar una versión' }).click()
    dialog = page.getByRole('dialog')
    await dialog.getByLabel(/Rige desde/).fill('2099-01-15')
    await dialog.getByLabel(/Cuota mensual/).fill('120')
    await dialog.getByLabel(/Comprobantes incluidos/).fill('300')
    await dialog.getByLabel(/Precio de cada comprobante adicional/).fill('0.1')
    await dialog.getByRole('button', { name: 'Publicar precio' }).click()
    await expect(dialog.getByRole('alert')).toContainText('SF-SUB-001')
    await dialog.getByRole('button', { name: 'Cancelar' }).click()

    // La política y los términos de comisión iniciales están a la vista.
    await page.getByRole('tab', { name: 'Política de cobranza' }).click()
    await expect(page.getByRole('row', { name: /^1 / })).toContainText('10 días')
    await expect(page.getByRole('row', { name: /^1 / })).toContainText('15 días')
    await page.getByRole('tab', { name: 'Comisiones' }).click()
    await expect(page.getByRole('row', { name: /^1 / })).toContainText('desde 0: 20 % · desde 10: 25 % · desde 25: 30 %')
    expect(await seriousViolations(page)).toEqual([])

    // Se asigna a la cuenta: el detalle y el propietario ven el precio que conserva; todavía no tiene cargos.
    await nav(page).getByRole('link', { name: 'Inquilinos' }).click()
    await page.getByLabel('Buscar por nombre').fill(tenant.name)
    await page.getByRole('link', { name: tenant.name }).click()
    await page.getByLabel('Cambiar a').selectOption({ label: `${plan.name} (${plan.code})` })
    await page.getByRole('button', { name: 'Cambiar plan' }).click()
    await expect(page.getByText(`Plan ${plan.name}`)).toBeVisible()
    await expect(page.getByText('S/ 99.90 más IGV')).toBeVisible()
    await expect(page.getByText('200 por mes')).toBeVisible()
    await expect(page.getByText('Aún no tiene cargos.')).toBeVisible()

    const context = await browser.newContext()
    const owner = await context.newPage()
    await signIn(owner, tenant.owner)
    await owner.getByRole('link', { name: 'Plan y consumo' }).click()
    await expect(owner.getByRole('heading', { name: 'Precio y cargos' })).toBeVisible()
    await expect(owner.getByText('S/ 99.90 más IGV')).toBeVisible()
    await expect(owner.getByText('Cada comprobante adicional')).toBeVisible()
    await expect(owner.getByText('Aún no tiene cargos.')).toBeVisible()
    expect(await seriousViolations(owner)).toEqual([])
    await context.close()
  })

  test('la cobranza se ejecuta a pedido y la lista de cargos es accesible', async ({ page }) => {
    await signInAsAdmin(page)
    await nav(page).getByRole('link', { name: 'Cobranza' }).click()
    await expect(page.getByRole('heading', { name: 'Cobranza' })).toBeVisible()
    await page.getByRole('button', { name: 'Ejecutar la cobranza ahora' }).click()
    await expect(page.getByText(/Cobranza ejecutada: \d+ cargos nuevos/)).toBeVisible()
    await page.getByLabel('Estado').selectOption('Overdue')
    await expect(page.getByText('No hay cargos con esos filtros.')).toBeVisible()
    expect(await seriousViolations(page)).toEqual([])
  })

  test('el revendedor ve sus comisiones y no las pantallas de precios de la plataforma', async ({ page }) => {
    const reseller = await createReseller('Comisiones')
    await signIn(page, reseller.admin)
    await expect(page.getByRole('heading', { name: 'Mis cuentas' })).toBeVisible()

    await nav(page).getByRole('link', { name: 'Comisiones' }).click()
    await expect(page.getByRole('heading', { name: 'Comisiones' })).toBeVisible()
    await expect(page.getByText('Cuentas activas')).toBeVisible()
    await expect(page.getByText('desde 0: 20 % · desde 10: 25 % · desde 25: 30 %')).toBeVisible()
    await expect(page.getByText('Aún no hay comisiones.')).toBeVisible()
    expect(await seriousViolations(page)).toEqual([])

    await expect(nav(page).getByRole('link', { name: 'Precios y comisiones' })).toHaveCount(0)
    await expect(nav(page).getByRole('link', { name: 'Cobranza' })).toHaveCount(0)
    await page.goto('/plataforma/precios')
    await expect(page.getByRole('alert')).toBeVisible()
  })

  test('la plataforma ve las comisiones de un revendedor desde su fila', async ({ page }) => {
    const reseller = await createReseller('Fila')
    await signInAsAdmin(page)
    await nav(page).getByRole('link', { name: 'Revendedores' }).click()
    await page.getByRole('button', { name: `Comisiones de ${reseller.name}` }).click()
    const dialog = page.getByRole('dialog')
    await expect(dialog.getByText('Aún no hay comisiones.')).toBeVisible()
    await expect(dialog.getByText('Porcentaje actual')).toBeVisible()
    expect(await seriousViolations(page)).toEqual([])
  })
})

test.describe('la factura de la plataforma por sus cobros', () => {
  test('la plataforma configura la cuenta con la que factura y una cuenta completa sus datos de facturación', async ({ page, world, browser }) => {
    // The account of the platform: a company ready to issue, with the series of the invoices, the receipts and the credit notes of both.
    await world.tenant.api.post('/api/v1/series', { companyId: world.company.id, documentTypeCode: '07', code: 'BC01' })
    await signInAsAdmin(page)

    await nav(page).getByRole('link', { name: 'Cobranza' }).click()
    await expect(page.getByRole('heading', { name: 'Facturación de la plataforma' })).toBeVisible()
    await page.getByRole('button', { name: /Configurar la facturación|Cambiar la configuración/ }).click()
    const dialog = page.getByRole('dialog')
    await dialog.getByLabel('Cuenta emisora').selectOption({ label: world.tenant.name })
    await dialog.getByLabel('Empresa emisora').selectOption({ index: 1 })
    await dialog.getByLabel(/Serie de facturas/).selectOption({ label: 'F001' })
    await dialog.getByLabel(/Serie de boletas de venta/).selectOption({ label: 'B001' })
    await dialog.getByLabel(/Serie de notas de crédito de facturas/).selectOption({ label: 'FC01' })
    await dialog.getByLabel(/Serie de notas de crédito de boletas/).selectOption({ label: 'BC01' })
    expect(await seriousViolations(page)).toEqual([])
    await dialog.getByRole('button', { name: 'Guardar' }).click()
    await expect(page.getByText('Configuración guardada.')).toBeVisible()
    await expect(page.getByText('Factura lo que cobra')).toBeVisible()

    // A customer account: it is asked for its billing data, gives them, and the platform sees them in its detail.
    const customer = await createTenant('Cliente factura')
    const context = await browser.newContext()
    const owner = await context.newPage()
    await signIn(owner, customer.owner)
    await owner.getByRole('link', { name: 'Plan y consumo' }).click()
    await expect(owner.getByText(/Aún no hay datos de facturación/)).toBeVisible()
    await owner.getByRole('button', { name: 'Completar los datos de facturación' }).click()
    const form = owner.getByRole('dialog')
    const ruc = newRuc()
    await form.getByLabel('RUC', { exact: true }).fill(ruc)
    await form.getByLabel('Razón social').fill('Cliente Facturado SAC')
    await form.getByLabel(/Correo para el comprobante/).fill('facturas@cliente.test')
    expect(await seriousViolations(owner)).toEqual([])
    await form.getByRole('button', { name: 'Guardar' }).click()
    await expect(owner.getByText('Datos de facturación guardados.')).toBeVisible()
    await expect(owner.getByText('Cliente Facturado SAC')).toBeVisible()
    await expect(owner.getByText('Factura', { exact: true })).toBeVisible()
    await context.close()

    await nav(page).getByRole('link', { name: 'Inquilinos' }).click()
    await page.getByLabel('Buscar por nombre').fill(customer.name)
    await page.getByRole('link', { name: customer.name }).click()
    await expect(page.getByText('Cliente Facturado SAC')).toBeVisible()
    await expect(page.getByText(ruc)).toBeVisible()
  })
})
