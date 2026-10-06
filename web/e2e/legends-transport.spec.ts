import type { Locator, Page } from '@playwright/test'
import { expect, seriousViolations, test } from './fixtures.ts'
import { prepareAndSend } from './pages.ts'

const BUYER = { number: '20100070970', name: 'DISTRIBUIDORA ANDINA SAC' }

async function fillBuyer(page: Page): Promise<void> {
  await page.goto('/documentos/nuevo')
  await expect(page.getByRole('heading', { name: 'Emitir comprobante' })).toBeVisible()
  await page.getByLabel('Número', { exact: true }).fill(BUYER.number)
  await page.getByLabel('Nombre o razón social').fill(BUYER.name)
}

async function fillLine(card: Locator, description: string, unitValue = '100'): Promise<void> {
  await card.getByLabel('Descripción').fill(description)
  await card.getByLabel('Cantidad').fill('1')
  await card.getByLabel('Valor unitario').fill(unitValue)
}

async function fillTransport(card: Locator, trip: string): Promise<void> {
  const transport = card.locator('fieldset.transport')
  await transport.getByLabel(/^Ubigeo de origen/).fill('150101')
  await transport.getByLabel(/^Dirección de origen/).fill('Av. Argentina 123, Callao')
  await transport.getByLabel(/^Ubigeo de destino/).fill('040101')
  await transport.getByLabel(/^Dirección de destino/).fill('Parque Industrial, Arequipa')
  await transport.getByLabel(/^Detalle del viaje/).fill(trip)
  await transport.getByLabel(/^Valor referencial del servicio/).fill('1500')
  await transport.getByLabel(/^Valor referencial por carga efectiva/).fill('1.5')
  await transport.getByLabel(/^Valor referencial por carga útil nominal/).fill('2.25')
}

const operationCard = (page: Page) => page.locator('.card', { has: page.getByRole('heading', { name: 'Operación' }) })

test.describe('leyendas y transporte de carga', () => {
  test('una venta exonerada de la Amazonía lleva su leyenda, que el comprobante muestra y SUNAT acepta', async ({ app }) => {
    await fillBuyer(app)
    await fillLine(app.locator('fieldset.line-card').first(), 'Arroz de la selva')
    // Con una línea gravada no hay leyendas que elegir.
    await expect(app.getByRole('heading', { name: 'Leyendas de venta exonerada' })).toHaveCount(0)

    await app.locator('fieldset.line-card').first().getByLabel('Afectación al IGV').selectOption('20')
    await expect(app.getByRole('heading', { name: 'Leyendas de venta exonerada' })).toBeVisible()
    await app.getByLabel(/^2001 ·/).check()
    expect(await seriousViolations(app)).toEqual([])
    await app.getByRole('button', { name: 'Emitir', exact: true }).click()

    await expect(app).toHaveURL(/\/documentos\/[0-9a-f-]{36}$/)
    await expect(operationCard(app)).toContainText('Leyendas')
    await expect(operationCard(app)).toContainText('2001')
    await prepareAndSend(app)
  })

  test('una leyenda sin operaciones exoneradas no se ofrece, y la que quedó marcada no viaja si la línea vuelve a ser gravada', async ({ app }) => {
    await fillBuyer(app)
    const card = app.locator('fieldset.line-card').first()
    await fillLine(card, 'Mercadería')
    await card.getByLabel('Afectación al IGV').selectOption('20')
    await app.getByLabel(/^2008 ·/).check()
    await card.getByLabel('Afectación al IGV').selectOption('10')
    await expect(app.getByRole('heading', { name: 'Leyendas de venta exonerada' })).toHaveCount(0)

    await app.getByRole('button', { name: 'Emitir', exact: true }).click()
    await expect(app).toHaveURL(/\/documentos\/[0-9a-f-]{36}$/) // el servidor la habría rechazado si hubiera viajado
    await expect(app.getByRole('heading', { name: 'Operación' })).toHaveCount(0)
  })

  test('un servicio de transporte de carga con detracción 027 pide los datos del viaje en cada ítem, con sus tramos', async ({ app }) => {
    await fillBuyer(app)
    await fillLine(app.locator('fieldset.line-card').first(), 'Flete Lima - Arequipa', '1000')
    await app.getByLabel('Esta factura tiene').selectOption('detraction')
    await app.getByLabel('Bien o servicio').selectOption('027')
    await app.getByLabel('Porcentaje', { exact: false }).fill('4')
    await app.getByLabel('Cuenta en el Banco de la Nación').fill('00012345678')

    const first = app.locator('fieldset.line-card').first()
    await expect(first.locator('fieldset.transport')).toBeVisible()
    await fillTransport(first, 'Carga seca Lima - Arequipa')

    // Un segundo ítem pide lo mismo; se copia del primero.
    await app.getByRole('button', { name: 'Agregar ítem' }).click()
    const second = app.locator('fieldset.line-card').nth(1)
    await fillLine(second, 'Flete de retorno', '500')
    await app.getByRole('button', { name: 'Usar el transporte del ítem 1 en todos' }).click()
    await expect(second.locator('fieldset.transport').getByLabel(/^Detalle del viaje/)).toHaveValue('Carga seca Lima - Arequipa')

    // Un tramo con su vehículo.
    await first.getByRole('button', { name: 'Agregar tramo y vehículo' }).click()
    await first.getByLabel(/^Tramo 1: ubigeo de origen/).fill('150101')
    await first.getByLabel(/^Tramo 1: ubigeo de destino/).fill('040101')
    await first.getByLabel(/^Tramo 1: configuración vehicular/).fill('T3S3')
    await first.getByLabel(/^Tramo 1: carga útil/).fill('28')
    await first.getByLabel(/^Tramo 1: retorno vacío|Retorno vacío/).check()

    await app.getByRole('button', { name: 'Calcular el monto' }).click()
    await expect(app.getByRole('status')).toContainText('S/ 1,770.00') // 1500 + 18 % de IGV
    await expect(app.getByLabel('Monto de la detracción')).toHaveValue('70.8')
    expect(await seriousViolations(app)).toEqual([])
    await app.getByRole('button', { name: 'Emitir', exact: true }).click()

    await expect(app).toHaveURL(/\/documentos\/[0-9a-f-]{36}$/)
    await expect(operationCard(app)).toContainText('Servicio de transporte de carga (027)')
    await expect(operationCard(app)).toContainText('Operación Sujeta a Detracción- Servicios de Transporte Carga')
    await expect(app.getByText('Transporte: Av. Argentina 123, Callao (150101)').first()).toBeVisible()
    await expect(app.getByText('1 tramo').first()).toBeVisible()
    await prepareAndSend(app)
  })

  test('los datos del transporte aparecen solo con la detracción 027 y no viajan con otra', async ({ app }) => {
    await fillBuyer(app)
    const card = app.locator('fieldset.line-card').first()
    await fillLine(card, 'Servicio', '100')
    await app.getByLabel('Esta factura tiene').selectOption('detraction')
    await app.getByLabel('Porcentaje', { exact: false }).fill('12')
    await app.getByLabel('Cuenta en el Banco de la Nación').fill('00012345678')

    await app.getByLabel('Bien o servicio').selectOption('027')
    await expect(card.locator('fieldset.transport')).toBeVisible()
    await fillTransport(card, 'Carga seca')
    await app.getByLabel('Bien o servicio').selectOption('037')
    await expect(card.locator('fieldset.transport')).toHaveCount(0)

    // Con otro código el servidor habría rechazado una línea con datos de transporte: no viajan.
    await app.getByRole('button', { name: 'Calcular el monto' }).click()
    await expect(app.getByLabel('Monto de la detracción')).toHaveValue('14.16')
    await app.getByRole('button', { name: 'Emitir', exact: true }).click()
    await expect(app).toHaveURL(/\/documentos\/[0-9a-f-]{36}$/)
    await expect(app.getByText('Transporte:')).toHaveCount(0)
  })
})
