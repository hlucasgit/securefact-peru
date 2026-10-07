import type { Page } from '@playwright/test'
import { expect, seriousViolations, test } from './fixtures.ts'
import { issueInvoice, taxedLine } from './helpers/api.ts'
import { prepareAndSend } from './pages.ts'

const days = (n: number) => new Date(Date.now() + n * 86_400_000).toISOString().slice(0, 10)

async function fillInvoice(page: Page): Promise<void> {
  await page.goto('/documentos/nuevo')
  await expect(page.getByRole('heading', { name: 'Emitir comprobante' })).toBeVisible()
  await page.getByLabel('Número', { exact: true }).fill('20100070970')
  await page.getByLabel('Nombre o razón social').fill('DISTRIBUIDORA ANDINA SAC')
  const card = page.locator('fieldset.line-card').first()
  await card.getByLabel('Descripción').fill('Servicio de consultoría')
  await card.getByLabel('Cantidad', { exact: true }).fill('2')
  await card.getByLabel('Valor unitario').fill('100')
}

test.describe('entrega inicial del crédito y nota de débito con detracción', () => {
  test('una venta al crédito con entrega inicial: el sistema da el monto neto pendiente y las cuotas lo suman', async ({ app }) => {
    await fillInvoice(app)
    await app.getByLabel('Venta al crédito (cuotas)').check()
    await app.getByLabel('Entrega inicial').fill('36')

    await app.getByRole('button', { name: 'Calcular el monto neto pendiente' }).click()
    await expect(app.getByRole('status')).toContainText('Monto neto pendiente de pago: S/ 200.00') // 236.00 - 36.00
    expect(await seriousViolations(app)).toEqual([])

    await app.getByLabel('Cuota 1: monto').fill('120')
    await app.getByLabel('Vencimiento').first().fill(days(10))
    await app.getByRole('button', { name: 'Agregar cuota' }).click()
    await app.getByLabel('Cuota 2: monto').fill('80')
    await app.getByLabel('Vencimiento').nth(1).fill(days(40))
    await app.getByRole('button', { name: 'Emitir', exact: true }).click()

    await expect(app).toHaveURL(/\/documentos\/[0-9a-f-]{36}$/)
    await expect(app.getByText('Venta al crédito: S/ 120.00')).toBeVisible()
    await expect(app.getByText('Entrega inicial de S/ 36.00 pagada en la emisión')).toBeVisible()
    await prepareAndSend(app)
  })

  test('cambiar la entrega inicial descarta el cálculo y unas cuotas que no suman lo pendiente las rechaza la API', async ({ app }) => {
    await fillInvoice(app)
    await app.getByLabel('Venta al crédito (cuotas)').check()
    await app.getByRole('button', { name: 'Calcular el monto neto pendiente' }).click()
    await expect(app.getByRole('status')).toContainText('S/ 236.00')

    await app.getByLabel('Entrega inicial').fill('36')
    await expect(app.getByRole('status')).toHaveCount(0)

    // Las cuotas suman el total, no lo pendiente: la API lo rechaza.
    await app.getByLabel('Cuota 1: monto').fill('236')
    await app.getByLabel('Vencimiento').first().fill(days(10))
    await app.getByRole('button', { name: 'Emitir', exact: true }).click()
    await expect(app.getByRole('alert')).toContainText('SF-BIL-006')
    await expect(app).toHaveURL(/\/documentos\/nuevo$/)
  })

  test('una nota de débito sobre una factura puede llevar su propia detracción, que el sistema calcula', async ({ app, world }) => {
    const invoice = await issueInvoice(world, [taxedLine('Servicio', 1)], { send: true })
    await app.goto(`/documentos/${invoice.id}/nota`)

    // Una nota de crédito no ofrece detracción.
    await expect(app.getByRole('heading', { name: 'Detracción' })).toHaveCount(0)
    await app.getByLabel('Tipo de nota').selectOption({ label: 'Nota de débito' })
    await app.getByLabel('Motivo', { exact: true }).selectOption({ index: 1 })
    await app.getByLabel('Sustento').fill('Aumento en el valor del servicio')
    await app.getByLabel('Esta nota de débito está sujeta a detracción').check()
    await app.getByLabel('Bien o servicio').selectOption('037')
    await app.getByLabel('Porcentaje', { exact: false }).fill('12')
    await app.getByLabel('Cuenta en el Banco de la Nación').fill('00012345678')

    await app.getByRole('button', { name: 'Calcular el monto' }).click()
    await expect(app.getByRole('status')).toContainText('S/ 118.00')
    await expect(app.getByRole('status')).toContainText('S/ 14.16')
    await expect(app.getByLabel('Monto de la detracción')).toHaveValue('14.16')
    expect(await seriousViolations(app)).toEqual([])
    await app.getByRole('button', { name: 'Emitir nota' }).click()

    await expect(app.getByRole('heading', { name: 'Nota de débito FD01-1' })).toBeVisible()
    const operation = app.locator('.card', { has: app.getByRole('heading', { name: 'Operación' }) })
    await expect(operation).toContainText('Demás servicios gravados con el IGV (037)')
    await expect(operation).toContainText('12 % · S/ 14.16')
    await expect(operation).not.toContainText('Tipo de operación') // una nota no tiene tipo de operación propio
    await prepareAndSend(app)

    // Una nota de crédito sobre la misma factura no ofrece la detracción.
    await app.goto(`/documentos/${invoice.id}/nota`)
    await expect(app.getByLabel('Esta nota de débito está sujeta a detracción')).toHaveCount(0)
  })

  test('una nota de débito sobre una boleta no ofrece detracción', async ({ app, world }) => {
    const receipt = await issueInvoice(world, [taxedLine('Servicio', 1)], { receipt: true })
    await app.goto(`/documentos/${receipt.id}/nota`)
    await app.getByLabel('Tipo de nota').selectOption({ label: 'Nota de débito' })
    await expect(app.getByLabel('Esta nota de débito está sujeta a detracción')).toHaveCount(0)
  })
})
