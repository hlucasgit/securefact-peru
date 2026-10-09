import { readFileSync } from 'node:fs'
import type { Download, Page } from '@playwright/test'
import { expect, test } from './fixtures.ts'

/** Fills the form of a sale in private transport: one good, one vehicle, one driver and the invoice of the sale. */
async function fillSale(page: Page, ruc: string, note = ''): Promise<void> {
  await page.goto('/guias/nueva')
  await expect(page.getByRole('heading', { name: 'Emitir guía de remisión' })).toBeVisible()
  await page.getByLabel('Peso bruto total').fill('12.5')
  await page.getByLabel('Número de bultos').fill('3')
  if (note) await page.getByLabel('Observaciones').fill(note)

  await page.getByLabel('Número de documento').nth(0).fill('20100070970')
  await page.getByLabel('Nombre o razón social').fill('DISTRIBUIDORA ANDINA SAC')
  await page.getByLabel('Ubigeo').nth(0).fill('150101')
  await page.getByLabel('Dirección').nth(0).fill('Av. Argentina 123, Lima')
  await page.getByLabel('Ubigeo').nth(1).fill('150122')
  await page.getByLabel('Dirección').nth(1).fill('Calle Los Pinos 456, Miraflores')

  await page.getByLabel('Placa del vehículo').fill('ABC123')
  await page.getByLabel('Número de documento').nth(1).fill('12345678')
  await page.getByLabel('Nombres').fill('JUAN CARLOS')
  await page.getByLabel('Apellidos').fill('PEREZ GOMEZ')
  await page.getByLabel('Licencia de conducir').fill('Q12345678')

  await page.getByLabel('Descripción del bien 1').fill('Caja de repuestos')

  await page.getByRole('button', { name: 'Agregar documento relacionado' }).click()
  await page.getByLabel('Número del documento relacionado 1').fill('F001-123')
  await page.getByLabel('RUC del emisor').fill(ruc)
}

test.describe('guías de remisión', () => {
  test.beforeEach(async ({ world }) => {
    await world.tenant.api.post('/api/v1/gre/series', { companyId: world.company.id, code: 'T001' })
    await world.tenant.api.put(`/api/v1/sol-credentials/${world.company.id}/api`, { clientId: 'e2e-client-id', clientSecret: 'e2e-client-secret-0001' })
  })

  test('una guía se prepara, se envía, queda aceptada y se descargan su XML y su CDR', async ({ app, world }) => {
    await fillSale(app, world.company.ruc)
    await app.getByRole('button', { name: 'Preparar guía' }).click()

    await expect(app.getByRole('heading', { name: 'Guía de remisión del remitente T001-1' })).toBeVisible()
    await expect(app.getByText('Preparada', { exact: true })).toBeVisible()

    await app.getByRole('button', { name: 'Enviar a SUNAT' }).click()
    await expect(app.locator('.alert.ok')).toContainText('ha sido aceptada', { timeout: 30_000 })
    await expect(app.getByText('Aceptada', { exact: true })).toBeVisible()

    const xmlDownload = app.waitForEvent('download')
    await app.getByRole('button', { name: 'Descargar XML' }).click()
    const xml = readFileSync((await (await xmlDownload).path())!, 'utf8')
    expect(xml).toContain('DespatchAdvice')
    expect(xml).toContain('T001-1')
    expect(xml).toContain(world.company.ruc)
    expect(xml).toMatch(/<Signature [^>]*xmldsig#/)

    const cdrDownload = app.waitForEvent('download')
    await app.getByRole('button', { name: 'Descargar CDR' }).click()
    expect(readFileSync((await (await cdrDownload).path())!).subarray(0, 2).toString('latin1')).toBe('PK')

    // The PDF opens in a new tab as a blob. Headless Chromium has no PDF viewer, so it hands the file over as a download of that tab.
    const pdfDownload = new Promise<Download>((resolve) => app.context().on('page', (tab) => tab.on('download', resolve)))
    await app.getByRole('button', { name: 'Ver PDF' }).click()
    expect(readFileSync((await (await pdfDownload).path())!).subarray(0, 4).toString('latin1')).toBe('%PDF')

    await app.getByRole('link', { name: 'Guías de remisión', exact: true }).click()
    const row = app.getByRole('row', { name: /T001-1/ })
    await expect(row).toContainText('DISTRIBUIDORA ANDINA SAC')
    await expect(row).toContainText('Aceptada')
  })

  test('el simulador observa la guía que lleva la marca y la pantalla lo muestra', async ({ app, world }) => {
    await fillSale(app, world.company.ruc, '[sandbox:observar]')
    await app.getByRole('button', { name: 'Preparar guía' }).click()
    await app.getByRole('button', { name: 'Enviar a SUNAT' }).click()

    await expect(app.getByText('Aceptada con observaciones')).toBeVisible({ timeout: 30_000 })
    await expect(app.getByText(/4030/)).toBeVisible()
  })

  test('una guía con datos que no cumplen las reglas de SUNAT es refusada con el motivo', async ({ app }) => {
    await app.goto('/guias/nueva')
    await app.getByLabel('Peso bruto total').fill('0')
    await app.getByLabel('Número de documento').nth(0).fill('20100070970')
    await app.getByLabel('Nombre o razón social').fill('DISTRIBUIDORA ANDINA SAC')
    await app.getByLabel('Ubigeo').nth(0).fill('150101')
    await app.getByLabel('Dirección').nth(0).fill('Av. Argentina 123, Lima')
    await app.getByLabel('Ubigeo').nth(1).fill('150122')
    await app.getByLabel('Dirección').nth(1).fill('Calle Los Pinos 456, Miraflores')
    await app.getByLabel('Placa del vehículo').fill('ABC123')
    await app.getByLabel('Número de documento').nth(1).fill('12345678')
    await app.getByLabel('Nombres').fill('JUAN CARLOS')
    await app.getByLabel('Apellidos').fill('PEREZ GOMEZ')
    await app.getByLabel('Licencia de conducir').fill('Q12345678')
    await app.getByLabel('Descripción del bien 1').fill('Caja de repuestos')
    await app.getByRole('button', { name: 'Preparar guía' }).click()

    await expect(app.getByRole('alert').filter({ hasText: 'peso' })).toBeVisible()
    await expect(app).toHaveURL(/\/guias\/nueva$/)
  })

  test('sin credenciales de API la guía queda preparada y el envío explica qué falta', async ({ app, world }) => {
    await world.tenant.api.del(`/api/v1/sol-credentials/${world.company.id}/api`)
    await fillSale(app, world.company.ruc)
    await app.getByRole('button', { name: 'Preparar guía' }).click()
    await app.getByRole('button', { name: 'Enviar a SUNAT' }).click()

    await expect(app.getByRole('alert')).toContainText('client_id')
    await expect(app.getByText('Preparada', { exact: true })).toBeVisible()
  })
})
