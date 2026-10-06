import type { Page } from '@playwright/test'
import { expect, seriousViolations, test } from './fixtures.ts'
import { prepareAndSend } from './pages.ts'

const COMPANY_BUYER = { type: '6', number: '20100070970', name: 'DISTRIBUIDORA ANDINA SAC' }

async function fillBasics(page: Page, buyer = COMPANY_BUYER): Promise<void> {
  await page.goto('/documentos/nuevo')
  await expect(page.getByRole('heading', { name: 'Emitir comprobante' })).toBeVisible()
  await page.getByLabel('Tipo de documento', { exact: true }).selectOption(buyer.type)
  await page.getByLabel('Número', { exact: true }).fill(buyer.number)
  await page.getByLabel('Nombre o razón social').fill(buyer.name)
  const card = page.locator('fieldset.line-card').first()
  await card.getByLabel('Descripción').fill('Servicio de consultoría')
  await card.getByLabel('Cantidad').fill('2')
  await card.getByLabel('Valor unitario').fill('100')
}

const operationCard = (page: Page) => page.locator('.card', { has: page.getByRole('heading', { name: 'Operación' }) })

test.describe('detracción, retención y exportación', () => {
  test('una factura con detracción: el sistema calcula el monto, se emite, se muestra y SUNAT la acepta', async ({ app }) => {
    await fillBasics(app)
    await app.getByLabel('Esta factura tiene').selectOption('detraction')
    // Los códigos que piden datos en cada línea no se ofrecen en el formulario.
    await expect(app.getByRole('option', { name: /^004 ·/ })).toHaveCount(0)
    await expect(app.getByRole('option', { name: /^027 ·/ })).toHaveCount(0)
    await app.getByLabel('Bien o servicio').selectOption('037')
    await app.getByLabel('Porcentaje', { exact: false }).fill('12')
    await app.getByLabel('Cuenta en el Banco de la Nación').fill('00012345678')

    await app.getByRole('button', { name: 'Calcular el monto' }).click()
    await expect(app.getByRole('status')).toContainText('S/ 236.00')
    await expect(app.getByRole('status')).toContainText('S/ 28.32')
    await expect(app.getByLabel('Monto de la detracción')).toHaveValue('28.32')
    expect(await seriousViolations(app)).toEqual([])

    await app.getByRole('button', { name: 'Emitir', exact: true }).click()
    await expect(app).toHaveURL(/\/documentos\/[0-9a-f-]{36}$/)
    await expect(operationCard(app)).toContainText('Demás servicios gravados con el IGV (037)')
    await expect(operationCard(app)).toContainText('12 % · S/ 28.32')
    await expect(operationCard(app)).toContainText('00012345678')
    await expect(operationCard(app)).toContainText('Operación Sujeta a Detracción')

    await prepareAndSend(app)
    await expect(app.locator('.alert.ok')).toContainText('ha sido aceptada')
  })

  test('una factura con retención del IGV: el sistema da el monto retenido', async ({ app }) => {
    await fillBasics(app)
    await app.getByLabel('Esta factura tiene').selectOption('retention')
    await app.getByLabel('Porcentaje de la retención').fill('3')
    await app.getByRole('button', { name: 'Calcular la retención' }).click()
    await expect(app.getByRole('status')).toContainText('S/ 7.08')

    await app.getByRole('button', { name: 'Emitir', exact: true }).click()
    await expect(app).toHaveURL(/\/documentos\/[0-9a-f-]{36}$/)
    await expect(operationCard(app)).toContainText('3 % de S/ 236.00 · S/ 7.08')
    await prepareAndSend(app)
  })

  test('el cálculo se descarta si el comprobante cambia, y un monto fuera de lo aceptado lo rechaza la API', async ({ app }) => {
    await fillBasics(app)
    await app.getByLabel('Esta factura tiene').selectOption('detraction')
    await app.getByLabel('Bien o servicio').selectOption('037')
    await app.getByLabel('Porcentaje', { exact: false }).fill('12')
    await app.getByLabel('Cuenta en el Banco de la Nación').fill('00012345678')
    await app.getByRole('button', { name: 'Calcular el monto' }).click()
    await expect(app.getByRole('status')).toContainText('S/ 236.00')

    await app.locator('fieldset.line-card').first().getByLabel('Cantidad').fill('3')
    await expect(app.getByRole('status')).toHaveCount(0) // ya no corresponde al comprobante

    await app.getByLabel('Monto de la detracción').fill('5')
    await app.getByRole('button', { name: 'Emitir', exact: true }).click()
    await expect(app.getByRole('alert')).toContainText('SF-BIL-006')
    await expect(app).toHaveURL(/\/documentos\/nuevo$/)
  })

  test('una exportación fija la afectación 40, pide un adquirente del exterior y no ofrece detracción ni retención', async ({ app }) => {
    await fillBasics(app)
    await app.getByLabel('Tipo de operación').selectOption('0200')
    const card = app.locator('fieldset.line-card').first()
    await expect(card.getByLabel('Afectación al IGV')).toBeDisabled()
    await expect(card.getByLabel('Afectación al IGV')).toHaveValue('40')
    await expect(app.getByRole('heading', { name: 'Detracción o retención' })).toHaveCount(0)
    await expect(app.getByRole('option', { name: 'Registro Unico de Contributentes' })).toHaveCount(0)
    await expect(app.getByText('el adquirente de esta operación está en el exterior', { exact: false })).toBeVisible()

    await app.getByLabel('Tipo de documento', { exact: true }).selectOption('7')
    await app.getByLabel('Número', { exact: true }).fill('P1234567')
    await app.getByLabel('Nombre o razón social').fill('Foreign Buyer Ltd')
    expect(await seriousViolations(app)).toEqual([])
    await app.getByRole('button', { name: 'Emitir', exact: true }).click()

    await expect(app).toHaveURL(/\/documentos\/[0-9a-f-]{36}$/)
    await expect(operationCard(app)).toContainText('Exportación de Bienes')
    await expect(app.locator('.card', { has: app.getByRole('heading', { name: 'Totales' }) })).toContainText('Exportación')
    await prepareAndSend(app)
  })

  test('una exportación de servicios usados en el exterior pide el país del uso', async ({ app }) => {
    await fillBasics(app, { type: '7', number: 'P7654321', name: 'Foreign Client Inc' })
    await app.getByLabel('Tipo de operación').selectOption('0201')
    await expect(app.getByLabel('País del uso o aprovechamiento', { exact: false })).toBeVisible()
    await app.getByLabel('País del uso o aprovechamiento', { exact: false }).fill('us')
    await expect(app.getByLabel('País del uso o aprovechamiento', { exact: false })).toHaveValue('US')

    await app.getByRole('button', { name: 'Emitir', exact: true }).click()
    await expect(app).toHaveURL(/\/documentos\/[0-9a-f-]{36}$/)
    await expect(operationCard(app)).toContainText('País del uso o aprovechamiento')
    await expect(operationCard(app)).toContainText('US')
    await prepareAndSend(app)
  })
})
