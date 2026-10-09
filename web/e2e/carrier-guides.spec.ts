import { readFileSync } from 'node:fs'
import type { Download, Page } from '@playwright/test'
import { expect, test } from './fixtures.ts'

/** Fills the form of a guide of the carrier that lists its own goods: sender, recipient, addresses, vehicle, driver and one good. */
async function fillTransfer(page: Page, options: { note?: string; senderGuide?: string } = {}): Promise<void> {
  await page.goto('/guias/transportista/nueva')
  await expect(page.getByRole('heading', { name: 'Emitir guía de remisión del transportista' })).toBeVisible()
  await page.getByLabel('Peso bruto total').fill('1500.5')
  if (options.note) await page.getByLabel('Observaciones').fill(options.note)

  // Parties: the form has two (sender, recipient); the labels repeat.
  await page.getByLabel('Número de documento', { exact: true }).nth(0).fill('20100070970')
  await page.getByLabel('Nombre o razón social').nth(0).fill('REMITENTE DEMO SAC')
  await page.getByLabel('Número de documento', { exact: true }).nth(1).fill('20100066603')
  await page.getByLabel('Nombre o razón social').nth(1).fill('CLIENTE DEMO SAC')
  await page.getByLabel('Ubigeo').nth(0).fill('150101')
  await page.getByLabel('Ubigeo').nth(1).fill('040101')

  if (options.senderGuide) {
    await page.getByLabel('Guía de remisión del remitente').fill(options.senderGuide)
  } else {
    await page.getByLabel('Dirección').nth(0).fill('Av. Argentina 123, Lima')
    await page.getByLabel('Dirección').nth(1).fill('Calle Mercaderes 45, Arequipa')
    await page.getByLabel('Descripción del bien 1').fill('Cajas de repuestos')
  }

  await page.getByLabel('Placa del vehículo principal').fill('ABC123')
  await page.getByLabel('Tarjeta de circulación del vehículo principal').fill('1234567890')
  await page.getByLabel('Número de documento del conductor principal').fill('12345678')
  await page.getByLabel('Nombres del conductor principal').fill('JUAN CARLOS')
  await page.getByLabel('Apellidos del conductor principal').fill('PEREZ GOMEZ')
  await page.getByLabel('Licencia del conductor principal').fill('Q12345678')
}

test.describe('guías de remisión del transportista', () => {
  test.beforeEach(async ({ world }) => {
    await world.tenant.api.post('/api/v1/gre/series', { companyId: world.company.id, code: 'V001' })
    await world.tenant.api.put(`/api/v1/sol-credentials/${world.company.id}/api`, { clientId: 'e2e-client-id', clientSecret: 'e2e-client-secret-0001' })
  })

  test('una guía del transportista se prepara, se envía, queda aceptada y se descargan su XML y su CDR', async ({ app, world }) => {
    await fillTransfer(app)
    await app.getByRole('button', { name: 'Preparar guía' }).click()

    await expect(app.getByRole('heading', { name: 'Guía de remisión del transportista V001-1' })).toBeVisible()
    await expect(app.getByText('Preparada', { exact: true })).toBeVisible()

    await app.getByRole('button', { name: 'Enviar a SUNAT' }).click()
    await expect(app.locator('.alert.ok')).toContainText('ha sido aceptada', { timeout: 30_000 })

    const xmlDownload = app.waitForEvent('download')
    await app.getByRole('button', { name: 'Descargar XML' }).click()
    const xml = readFileSync((await (await xmlDownload).path())!, 'utf8')
    expect(xml).toContain('DespatchAdvice')
    expect(xml).toContain('V001-1')
    expect(xml).toContain(world.company.ruc)
    expect(xml).toMatch(/>31<\/cbc:DespatchAdviceTypeCode>/)
    expect(xml).toMatch(/<Signature [^>]*xmldsig#/)

    const cdrDownload = app.waitForEvent('download')
    await app.getByRole('button', { name: 'Descargar CDR' }).click()
    expect(readFileSync((await (await cdrDownload).path())!).subarray(0, 2).toString('latin1')).toBe('PK')

    // The PDF opens in a new tab as a blob. Headless Chromium has no PDF viewer, so it hands the file over as a download of that tab.
    const pdfDownload = new Promise<Download>((resolve) => app.context().on('page', (tab) => tab.on('download', resolve)))
    await app.getByRole('button', { name: 'Ver PDF' }).click()
    expect(readFileSync((await (await pdfDownload).path())!).subarray(0, 4).toString('latin1')).toBe('%PDF')

    await app.getByRole('link', { name: 'Guías de remisión', exact: true }).click()
    const row = app.getByRole('row', { name: /V001-1/ })
    await expect(row).toContainText('Transportista')
    await expect(row).toContainText('CLIENTE DEMO SAC')
    await expect(row).toContainText('Aceptada')
  })

  test('con la guía del remitente relacionada no se piden los bienes ni las direcciones', async ({ app }) => {
    await fillTransfer(app, { senderGuide: 'T001-45' })
    await expect(app.getByLabel('Descripción del bien 1')).toHaveCount(0)
    await app.getByRole('button', { name: 'Preparar guía' }).click()

    await expect(app.getByRole('heading', { name: 'Guía de remisión del transportista V001-1' })).toBeVisible()
    await app.getByRole('button', { name: 'Enviar a SUNAT' }).click()
    await expect(app.locator('.alert.ok')).toContainText('ha sido aceptada', { timeout: 30_000 })
  })

  test('el simulador observa la guía que lleva la marca', async ({ app }) => {
    await fillTransfer(app, { note: '[sandbox:observar]' })
    await app.getByRole('button', { name: 'Preparar guía' }).click()
    await app.getByRole('button', { name: 'Enviar a SUNAT' }).click()

    await expect(app.getByText('Aceptada con observaciones')).toBeVisible({ timeout: 30_000 })
    await expect(app.getByText(/4030/)).toBeVisible()
  })

  test('una guía sin tarjeta de circulación es refusada con el motivo', async ({ app }) => {
    await fillTransfer(app)
    await app.getByLabel('Tarjeta de circulación del vehículo principal').fill('')
    await app.getByRole('button', { name: 'Preparar guía' }).click()

    await expect(app.getByRole('alert').filter({ hasText: '4399' })).toBeVisible()
    await expect(app).toHaveURL(/\/guias\/transportista\/nueva$/)
  })

  test('las series de guía se crean con T para el remitente y con V para el transportista', async ({ app, world }) => {
    await app.goto(`/empresas/${world.company.id}`)
    await app.getByRole('tab', { name: 'Guías de remisión' }).click()
    await expect(app.getByRole('row', { name: /V001/ })).toContainText('Transportista')

    await app.getByLabel('Serie', { exact: false }).last().fill('T005')
    await app.getByRole('button', { name: 'Registrar serie de guía' }).click()
    await expect(app.getByRole('row', { name: /T005/ })).toContainText('Remitente')
  })
})
