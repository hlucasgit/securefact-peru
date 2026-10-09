import { expect, test } from './fixtures.ts'

/** The driver and the vehicle of private transport, which every guide of the sender in these tests uses. */
async function fillTransport(page: import('@playwright/test').Page): Promise<void> {
  await page.getByLabel('Placa del vehículo').fill('ABC123')
  await page.getByLabel('Tarjeta de circulación o certificado de habilitación').fill('1234567890')
  await page.getByLabel('Nombres').fill('JUAN CARLOS')
  await page.getByLabel('Apellidos').fill('PEREZ GOMEZ')
  await page.getByLabel('Licencia de conducir').fill('Q12345678')
}

test.describe('guías con aduanas y emisor itinerante', () => {
  test.beforeEach(async ({ world }) => {
    await world.tenant.api.post('/api/v1/gre/series', { companyId: world.company.id, code: 'T001' })
    await world.tenant.api.put(`/api/v1/sol-credentials/${world.company.id}/api`, { clientId: 'e2e-client-id', clientSecret: 'e2e-client-secret-0001' })
  })

  test('el emisor itinerante no informa el punto de llegada y su guía queda aceptada', async ({ app, world }) => {
    await app.goto('/guias/nueva')
    await app.getByLabel('Motivo del traslado').selectOption('18')
    await expect(app.getByRole('heading', { name: 'Punto de llegada' })).toHaveCount(0)
    await expect(app.getByText('El emisor itinerante no informa el punto de llegada')).toBeVisible()

    await app.getByLabel('Peso bruto total').fill('80')
    await app.getByLabel('Número de documento').nth(0).fill(world.company.ruc)
    await app.getByLabel('Nombre o razón social').fill('Comercial E2E SAC')
    await app.getByLabel('Ubigeo').fill('150101')
    await app.getByLabel('Dirección').fill('Av. Argentina 123, Lima')
    await app.getByLabel('Número de documento').nth(1).fill('12345678')
    await fillTransport(app)
    await app.getByLabel('Descripción del bien 1').fill('Mercadería de reparto')
    await app.getByRole('button', { name: 'Preparar guía' }).click()

    await expect(app.getByRole('heading', { name: /Guía de remisión del remitente T001-1/ })).toBeVisible()
    await expect(app.getByText('18 · Traslado emisor itinerante CP')).toBeVisible()
    await app.getByRole('button', { name: 'Enviar a SUNAT' }).click()
    await expect(app.locator('.alert.ok')).toContainText('ha sido aceptada', { timeout: 30_000 })
  })

  test('una importación con su declaración, el puerto y el traslado total se prepara, se envía y queda aceptada', async ({ app }) => {
    await app.goto('/guias/nueva')
    await app.getByLabel('Motivo del traslado').selectOption('08')
    await expect(app.getByRole('heading', { name: 'Aduanas' })).toBeVisible()

    await app.getByLabel('Peso bruto total').fill('950')
    await app.getByLabel('Número de bultos').fill('3')
    await app.getByLabel('Número de documento').nth(0).fill('20100070970')
    await app.getByLabel('Nombre o razón social').fill('IMPORTADORA DEMO SAC')
    await app.getByLabel('Ubigeo').nth(0).fill('070101')
    await app.getByLabel('Dirección').nth(0).fill('Terminal portuario del Callao')
    await app.getByLabel('Ubigeo').nth(1).fill('150101')
    await app.getByLabel('Dirección').nth(1).fill('Av. Argentina 123, Lima')
    await app.getByLabel('Número de documento').nth(1).fill('12345678')
    await fillTransport(app)

    await app.getByLabel('Tipo de puerto o aeropuerto').selectOption('1')
    await app.getByLabel('Puerto o aeropuerto', { exact: true }).selectOption('CLL')
    await app.getByLabel('Traslado total de la declaración').check()
    await app.getByRole('button', { name: 'Agregar documento relacionado' }).click()
    await app.getByLabel('Tipo del documento 1').selectOption('50')
    await app.getByLabel('Número del documento relacionado 1').fill('118-2026-10-123456')
    await app.getByRole('button', { name: 'Preparar guía' }).click()

    await expect(app.getByRole('heading', { name: /Guía de remisión del remitente T001-1/ })).toBeVisible()
    await expect(app.getByText('08 · Importación')).toBeVisible()
    await app.getByRole('button', { name: 'Enviar a SUNAT' }).click()
    await expect(app.locator('.alert.ok')).toContainText('ha sido aceptada', { timeout: 30_000 })
  })

  test('una importación sin declaración es refusada con el código de SUNAT', async ({ app }) => {
    await app.goto('/guias/nueva')
    await app.getByLabel('Motivo del traslado').selectOption('08')
    await app.getByLabel('Peso bruto total').fill('950')
    await app.getByLabel('Número de documento').nth(0).fill('20100070970')
    await app.getByLabel('Nombre o razón social').fill('IMPORTADORA DEMO SAC')
    await app.getByLabel('Ubigeo').nth(0).fill('150101')
    await app.getByLabel('Dirección').nth(0).fill('Av. Argentina 123, Lima')
    await app.getByLabel('Ubigeo').nth(1).fill('150101')
    await app.getByLabel('Dirección').nth(1).fill('Av. Argentina 456, Lima')
    await app.getByLabel('Número de documento').nth(1).fill('12345678')
    await fillTransport(app)
    await app.getByRole('button', { name: 'Preparar guía' }).click()

    await expect(app.getByRole('alert').filter({ hasText: '3440' })).toBeVisible()
    await expect(app).toHaveURL(/\/guias\/nueva$/)
  })
})
