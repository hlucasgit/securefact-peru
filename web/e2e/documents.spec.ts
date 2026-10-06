import { expect, test } from './fixtures.ts'
import { issueInvoice, taxedLine } from './helpers/api.ts'
import { prepareAndSend, totalsText } from './pages.ts'

const ISC_AND_BAGS = { isc: { system: 'AdValorem', rateOrUnitAmount: 0.1 }, plasticBagCount: 3 }

test.describe('seguimiento de comprobantes con el simulador de SUNAT', () => {
  test('una nota de crédito parte del documento aceptado, repite el ISC y las bolsas, y queda aceptada', async ({ app, world }) => {
    const invoice = await issueInvoice(world, [taxedLine('Bebida con impuestos', 3, ISC_AND_BAGS)], { send: true })
    await app.goto(`/documentos/${invoice.id}`)
    await expect(app.locator('.page-head .badge')).toHaveText('Aceptado')

    await app.getByRole('link', { name: 'Emitir nota' }).click()
    await expect(app.getByRole('heading', { name: 'Emitir nota' })).toBeVisible()
    // The lines start as the ones of the original document, with its ISC and its bags.
    const card = app.locator('fieldset.line-card').first()
    await expect(card.getByLabel('Cantidad')).toHaveValue('3')
    await expect(card.getByLabel('Tasa del ISC (%)')).toHaveValue('10')
    await expect(card.getByLabel(/Bolsas de plástico/)).toBeChecked()
    await app.getByLabel('Sustento').fill('Devolución total de la mercadería')
    await app.getByRole('button', { name: 'Emitir nota' }).click()

    await expect(app.getByRole('heading', { name: 'Nota de crédito FC01-1' })).toBeVisible()
    const totals = await totalsText(app)
    for (const amount of ['S/ 30.00', 'S/ 59.40', 'S/ 1.50', 'S/ 390.90']) expect(totals).toContain(amount)
    await expect(app.getByRole('link', { name: 'F001-1' })).toBeVisible()
    await prepareAndSend(app)
  })

  test('una nota de débito se emite con su serie y su motivo', async ({ app, world }) => {
    const invoice = await issueInvoice(world, [taxedLine('Servicio', 1)], { send: true })
    await app.goto(`/documentos/${invoice.id}/nota`)

    await app.getByLabel('Tipo de nota').selectOption({ label: 'Nota de débito' })
    await app.getByLabel('Motivo', { exact: true }).selectOption({ index: 1 })
    await app.getByLabel('Sustento').fill('Intereses por mora')
    await app.getByRole('button', { name: 'Emitir nota' }).click()

    await expect(app.getByRole('heading', { name: 'Nota de débito FD01-1' })).toBeVisible()
  })

  test('el simulador observa, falla y rechaza según la marca de la descripción, y la pantalla lo explica', async ({ app, world }) => {
    const observed = await issueInvoice(world, [taxedLine('Servicio [sandbox:observar]', 1)])
    await app.goto(`/documentos/${observed.id}`)
    await prepareAndSend(app, 'Aceptado con observaciones')
    await expect(app.locator('.alert.warn')).toContainText('4030')

    // A rejection as a fault (SUNAT's SOAP fault): the sending fails for good, with the code and the reason.
    const failed = await issueInvoice(world, [taxedLine('Servicio [sandbox:rechazar]', 1)])
    await app.goto(`/documentos/${failed.id}`)
    await prepareAndSend(app, 'Falló')
    await expect(app.locator('.alert.bad').filter({ hasText: '2800' })).toContainText('Rechazo simulado')

    // A rejection in the CDR: the document is rejected and nothing else can be done with it.
    const rejected = await issueInvoice(world, [taxedLine('Servicio [sandbox:rechazar-cdr]', 1)])
    await app.goto(`/documentos/${rejected.id}`)
    await prepareAndSend(app, 'Rechazado')
    await expect(app.locator('.alert.bad').filter({ hasText: 'ha sido rechazada' })).toBeVisible()
    await expect(app.getByRole('button', { name: 'Enviar a SUNAT' })).toHaveCount(0)
    await expect(app.getByRole('button', { name: 'Dar de baja' })).toHaveCount(0)
  })

  test('dar de baja un comprobante aceptado lo marca como anulado', async ({ app, world }) => {
    // SUNAT answers a voiding through a ticket that the workers poll once a minute.
    test.setTimeout(150_000)
    const invoice = await issueInvoice(world, [taxedLine('Servicio', 1)], { send: true })
    await app.goto(`/documentos/${invoice.id}`)
    await expect(app.locator('.page-head .badge')).toHaveText('Aceptado')

    await app.getByRole('button', { name: 'Dar de baja' }).click()
    await app.getByLabel('Motivo').fill('Error en la emisión')
    await app.getByRole('dialog').getByRole('button', { name: 'Dar de baja' }).click()
    await expect(app.getByText('Comunicación de baja generada.')).toBeVisible()
    await expect(app.getByRole('status').filter({ hasText: 'Baja solicitada' })).toBeVisible()

    // The workers send the voiding and read its answer in the background; the page looks again by itself until it shows as voided.
    await expect(app.locator('.page-head .badge', { hasText: 'Anulado' })).toBeVisible({ timeout: 120_000 })
    await expect(app.getByRole('status').filter({ hasText: 'Baja solicitada' })).toHaveCount(0)
    await expect(app.getByRole('button', { name: 'Dar de baja' })).toHaveCount(0)
  })

  test('el resumen diario reporta las boletas del día', async ({ app, world }) => {
    await issueInvoice(world, [taxedLine('Bebida', 2)], { receipt: true })
    await issueInvoice(world, [taxedLine('Galletas', 1)], { receipt: true })

    await app.getByRole('link', { name: 'Resumen diario' }).click()
    await app.getByRole('button', { name: 'Generar resumen' }).click()

    await expect(app.getByText('Resumen diario generado.')).toBeVisible()
    const row = app.getByRole('row', { name: /-RC-/ })
    await expect(row).toContainText('2')
  })
})
