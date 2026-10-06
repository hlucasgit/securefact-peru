import { readFileSync } from 'node:fs'
import type { Download } from '@playwright/test'
import { expect, seriousViolations, signIn, test } from './fixtures.ts'
import { newRuc } from './helpers/api.ts'
import { issueFromForm, prepareAndSend, totalsText } from './pages.ts'

const BUYER = { number: '20100070970', name: 'DISTRIBUIDORA ANDINA SAC' }

test.describe('emisión de comprobantes', () => {
  test('una factura con ISC e ICBPER se emite, se firma, se envía, queda aceptada y se descarga', async ({ app, world }) => {
    await issueFromForm(app, { buyer: BUYER, lines: [{ description: 'Bebida con impuestos', quantity: 3, unitValue: 100, iscPercent: 10, bags: true }] })

    await expect(app.getByRole('heading', { name: 'Factura F001-1' })).toBeVisible()
    const totals = await totalsText(app)
    // Valor 300, ISC 10 % = 30, IGV 18 % de 330 = 59.40, tres bolsas a 0.50 = 1.50.
    for (const amount of ['S/ 300.00', 'S/ 30.00', 'S/ 59.40', 'S/ 1.50', 'S/ 390.90']) expect(totals).toContain(amount)
    await expect(app.getByText('3 bolsas')).toBeVisible()
    await expect(app.getByText('ISC 10 %')).toBeVisible()

    await prepareAndSend(app)
    await expect(app.locator('.alert.ok')).toContainText('ha sido aceptada')

    const xmlDownload = app.waitForEvent('download')
    await app.getByRole('button', { name: 'Descargar XML' }).click()
    const xml = readFileSync((await (await xmlDownload).path())!, 'utf8')
    expect(xml).toContain(`${world.company.ruc}`)
    expect(xml).toMatch(/>2000<\/cbc:ID>/)
    expect(xml).toMatch(/>7152<\/cbc:ID>/)
    expect(xml).toMatch(/<Signature [^>]*xmldsig#/)

    const cdrDownload = app.waitForEvent('download')
    await app.getByRole('button', { name: 'Descargar CDR' }).click()
    const cdr = readFileSync((await (await cdrDownload).path())!)
    expect(cdr.subarray(0, 2).toString('latin1')).toBe('PK')

    // The PDF opens in a new tab as a blob. Headless Chromium has no PDF viewer, so it hands the file over as a download of that tab.
    const pdfDownload = new Promise<Download>((resolve) => app.context().on('page', (tab) => tab.on('download', resolve)))
    await app.getByRole('button', { name: 'Ver PDF' }).click()
    const pdf = readFileSync((await (await pdfDownload).path())!)
    expect(pdf.subarray(0, 4).toString('latin1')).toBe('%PDF')
  })

  test('el XML firmado y el CDR quedan archivados con su huella', async ({ app }) => {
    await issueFromForm(app, { buyer: BUYER, lines: [{ description: 'Servicio de consultoría', quantity: 1, unitValue: 500 }] })
    await prepareAndSend(app)

    // The archive is written in the background (outbox and workers): reload until the files are listed.
    await expect(async () => {
      await app.reload()
      await expect(app.getByRole('link', { name: 'XML firmado' })).toBeVisible({ timeout: 2_000 })
      await expect(app.getByRole('link', { name: 'CDR', exact: true })).toBeVisible({ timeout: 2_000 })
    }).toPass({ timeout: 40_000, intervals: [2_000] })
    await expect(app.getByText(/SHA-256/).first()).toBeVisible()
  })

  test('una boleta se emite con DNI y la lista de documentos la muestra', async ({ app }) => {
    await issueFromForm(app, { type: 'Boleta de venta', buyer: { documentType: 'Documento Nacional de Identidad', number: '45678912', name: 'MARIA LOPEZ RAMOS' }, lines: [{ description: 'Bebida gaseosa', quantity: 6, unitValue: 3.5, bags: true }] })

    await expect(app.getByRole('heading', { name: 'Boleta de venta B001-1' })).toBeVisible()
    expect(await totalsText(app)).toContain('S/ 27.78')

    await app.getByRole('link', { name: 'Documentos', exact: true }).click()
    const row = app.getByRole('row', { name: /B001-1/ })
    await expect(row).toContainText('MARIA LOPEZ RAMOS')
    await expect(row).toContainText('Sin generar')
  })

  test('una factura al crédito cuyas cuotas no suman el total es refusada con el motivo de la API', async ({ app }) => {
    await app.goto('/documentos/nuevo')
    await app.getByLabel('Número', { exact: true }).fill(BUYER.number)
    await app.getByLabel('Nombre o razón social').fill(BUYER.name)
    await app.getByLabel('Descripción').fill('Servicio')
    await app.getByLabel('Valor unitario').fill('100')
    await app.getByLabel('Venta al crédito (cuotas)').check()
    await app.getByLabel('Cuota 1: monto').fill('10')
    await app.getByLabel('Vencimiento').fill(new Date(Date.now() + 30 * 86_400_000).toISOString().slice(0, 10))

    await app.getByRole('button', { name: 'Emitir', exact: true }).click()

    await expect(app.getByRole('alert')).toContainText('SF-BIL')
    await expect(app).toHaveURL(/\/documentos\/nuevo$/)
  })

  test('sin series activas del tipo, el formulario lo dice y no deja emitir', async ({ page, tenant }) => {
    await tenant.api.post('/api/v1/companies', { ruc: newRuc(), details: { legalName: 'Sin Series SAC', tradeName: null, fiscalAddress: 'Av. Sin Series 1', ubigeo: '150101', taxRegime: null, contactEmail: null, timeZone: 'America/Lima', defaultCurrency: 'PEN' } })
    await signIn(page, tenant.owner)
    await expect(page.getByRole('heading', { name: 'Panel' })).toBeVisible()

    await page.getByRole('link', { name: 'Emitir', exact: true }).click()

    await expect(page.getByText('No hay series activas de este tipo')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Emitir', exact: true })).toBeDisabled()
  })

  test('el formulario de emisión y el detalle del documento no tienen problemas graves de accesibilidad', async ({ app }) => {
    await app.goto('/documentos/nuevo')
    await expect(app.getByRole('heading', { name: 'Emitir comprobante' })).toBeVisible()
    expect(await seriousViolations(app)).toEqual([])

    await issueFromForm(app, { buyer: BUYER, lines: [{ description: 'Servicio', quantity: 1, unitValue: 100 }] })
    await expect(app.getByRole('heading', { name: 'Factura F001-1' })).toBeVisible()
    expect(await seriousViolations(app)).toEqual([])
  })
})
