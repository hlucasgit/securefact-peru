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

async function fillLine(card: Locator, description: string, unitValue: string): Promise<void> {
  await card.getByLabel('Descripción').fill(description)
  await card.getByLabel('Cantidad', { exact: true }).fill('1')
  await card.getByLabel('Valor unitario').fill(unitValue)
}

async function fillFishing(card: Locator, species: string): Promise<void> {
  const fishing = card.locator('fieldset.transport')
  await fishing.getByLabel(/^Matrícula de la embarcación/).fill('CE-1234-PM')
  await fishing.getByLabel(/^Nombre de la embarcación/).fill('Don Pepe')
  await fishing.getByLabel(/^Especie vendida/).fill(species)
  await fishing.getByLabel(/^Lugar de descarga/).fill('Chimbote')
  await fishing.getByLabel(/^Fecha de descarga/).fill('2026-10-01')
  await fishing.getByLabel(/^Cantidad de la especie/).fill('12.5')
}

async function fillGuest(card: Locator, stay: boolean): Promise<void> {
  const guest = card.locator('fieldset.transport')
  await guest.getByLabel(/^Nombre del huésped/).fill('John Smith')
  await guest.getByLabel(/^Número del documento del huésped/).fill('P1234567')
  await guest.getByLabel(/^País que emitió el pasaporte/).fill('us')
  if (!stay) return
  await guest.getByLabel(/^País de residencia/).fill('us')
  await guest.getByLabel(/^Ingreso al país/).fill('2026-09-28')
  await guest.getByLabel(/^Ingreso al establecimiento/).fill('2026-09-29')
  await guest.getByLabel(/^Salida del establecimiento/).fill('2026-10-02')
  await guest.getByLabel(/^Fecha de consumo/).fill('2026-10-02')
  await guest.getByLabel(/^Días de permanencia/).fill('3')
}

const operationCard = (page: Page) => page.locator('.card', { has: page.getByRole('heading', { name: 'Operación' }) })

test.describe('pesca, hospedaje y paquete turístico', () => {
  test('una venta de recursos hidrobiológicos con detracción 004 pide la embarcación y la especie en cada ítem', async ({ app }) => {
    await fillBuyer(app)
    const first = app.locator('fieldset.line-card').first()
    await fillLine(first, 'Anchoveta fresca', '1000')
    await app.getByLabel('Esta factura tiene').selectOption('detraction')
    await app.getByLabel('Bien o servicio').selectOption('004')
    await app.getByLabel('Porcentaje', { exact: false }).fill('10')
    await app.getByLabel('Cuenta en el Banco de la Nación').fill('00012345678')
    await expect(first.getByRole('group', { name: 'Recursos hidrobiológicos' })).toBeVisible()
    await fillFishing(first, 'Anchoveta')

    await app.getByRole('button', { name: 'Agregar ítem' }).click()
    const second = app.locator('fieldset.line-card').nth(1)
    await fillLine(second, 'Jurel', '500')
    await app.getByRole('button', { name: 'Usar los datos de pesca del ítem 1 en todos' }).click()
    await expect(second.locator('fieldset.transport').getByLabel(/^Nombre de la embarcación/)).toHaveValue('Don Pepe')
    await second.locator('fieldset.transport').getByLabel(/^Especie vendida/).fill('Jurel')

    await app.getByRole('button', { name: 'Calcular el monto' }).click()
    await expect(app.getByRole('status')).toContainText('S/ 1,770.00')
    await expect(app.getByLabel('Monto de la detracción')).toHaveValue('177')
    expect(await seriousViolations(app)).toEqual([])
    await app.getByRole('button', { name: 'Emitir', exact: true }).click()

    await expect(app).toHaveURL(/\/documentos\/[0-9a-f-]{36}$/)
    await expect(operationCard(app)).toContainText('Recursos hidrobiológicos (004)')
    await expect(operationCard(app)).toContainText('Operación Sujeta a Detracción- Recursos Hidrobiológicos')
    await expect(app.getByText('Pesca: Anchoveta · 12.5 t · Don Pepe (CE-1234-PM)').first()).toBeVisible()
    await expect(app.getByText('Pesca: Jurel').first()).toBeVisible()
    await prepareAndSend(app)
  })

  test('el hospedaje de un huésped no domiciliado (0202) lleva su estadía, es solo de facturas y SUNAT lo acepta', async ({ app }) => {
    await fillBuyer(app)
    await app.getByLabel('Tipo de operación').selectOption('0202')
    const card = app.locator('fieldset.line-card').first()
    await fillLine(card, 'Hospedaje de 3 noches', '300')
    await expect(card.getByLabel('Afectación al IGV')).toHaveValue('40')
    await expect(card.getByRole('group', { name: 'Huésped no domiciliado y estadía' })).toBeVisible()
    await fillGuest(card, true)
    expect(await seriousViolations(app)).toEqual([])

    // Una boleta no puede ser de hospedaje: el tipo no se ofrece y la operación vuelve a venta interna.
    await app.getByLabel('Tipo', { exact: true }).selectOption('03')
    await expect(app.getByRole('option', { name: /^0202 ·/ })).toHaveCount(0)
    await expect(app.getByRole('option', { name: /^0205 ·/ })).toHaveCount(0)
    await expect(app.getByLabel('Tipo de operación')).toHaveValue('0101')
    await app.getByLabel('Tipo', { exact: true }).selectOption('01')
    await app.getByLabel('Tipo de operación').selectOption('0202')
    await fillBuyerAgain(app)
    await fillGuest(app.locator('fieldset.line-card').first(), true)
    await app.getByRole('button', { name: 'Emitir', exact: true }).click()

    await expect(app).toHaveURL(/\/documentos\/[0-9a-f-]{36}$/)
    await expect(operationCard(app)).toContainText('Prestación de servicios de hospedaje')
    await expect(app.getByText('Huésped: John Smith (P1234567), pasaporte de US · 3 días de permanencia').first()).toBeVisible()
    await prepareAndSend(app)
  })

  test('un paquete turístico (0205) pide al huésped y no los datos de la estadía', async ({ app }) => {
    await fillBuyer(app)
    await app.getByLabel('Tipo de operación').selectOption('0205')
    const card = app.locator('fieldset.line-card').first()
    await fillLine(card, 'Paquete turístico Cusco', '800')
    await expect(card.getByRole('group', { name: 'Huésped no domiciliado', exact: true })).toBeVisible()
    await expect(card.getByLabel(/^Días de permanencia/)).toHaveCount(0)
    await fillGuest(card, false)
    await app.getByRole('button', { name: 'Emitir', exact: true }).click()

    await expect(app).toHaveURL(/\/documentos\/[0-9a-f-]{36}$/)
    await expect(app.getByText('Huésped: John Smith (P1234567), pasaporte de US').first()).toBeVisible()
    await expect(app.getByText('días de permanencia')).toHaveCount(0)
    await prepareAndSend(app)
  })
})

/** Changing the type of document of the form clears the buyer: it is typed again. */
async function fillBuyerAgain(page: Page): Promise<void> {
  await page.getByLabel('Número', { exact: true }).fill(BUYER.number)
  await page.getByLabel('Nombre o razón social').fill(BUYER.name)
}
