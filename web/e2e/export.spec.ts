import { readFileSync } from 'node:fs'
import { expect, seriousViolations, test } from './fixtures.ts'
import { issueInvoice, taxedLine } from './helpers/api.ts'

test.describe('exportar comprobantes', () => {
  test('el propietario baja un ZIP del mes con el XML firmado, el CDR y el índice', async ({ app, world }) => {
    const invoice = await issueInvoice(world, [taxedLine('Servicio de consultoría', 1)], { send: true })
    const base = `${world.company.ruc}-01-${invoice.series}-${invoice.number}`

    await app.getByRole('link', { name: 'Documentos', exact: true }).click()
    await expect(app.getByRole('heading', { name: 'Documentos' })).toBeVisible()
    await app.getByRole('button', { name: 'Exportar', exact: true }).click()
    const dialog = app.getByRole('dialog')
    await expect(dialog.getByRole('heading', { name: 'Exportar comprobantes' })).toBeVisible()
    expect(await seriousViolations(app)).toEqual([])

    const download = app.waitForEvent('download')
    await dialog.getByRole('button', { name: 'Descargar ZIP' }).click()
    const file = await download
    expect(file.suggestedFilename()).toMatch(new RegExp(`^comprobantes-${world.company.ruc}-\\d{8}-\\d{8}\\.zip$`))

    // A ZIP: the local-file headers of its entries carry their names in clear.
    const bytes = readFileSync(await file.path())
    expect(bytes.subarray(0, 2).toString('latin1')).toBe('PK')
    const names = bytes.toString('latin1')
    expect(names).toContain(`xml/${base}.xml`)
    expect(names).toContain(`cdr/R-${base}.zip`)
    expect(names).toContain('indice.csv')
    await expect(dialog).toBeHidden()
  })

  test('un período de más de un año se rechaza con su motivo y el diálogo sigue abierto', async ({ app }) => {
    await app.getByRole('link', { name: 'Documentos', exact: true }).click()
    await app.getByRole('button', { name: 'Exportar', exact: true }).click()
    const dialog = app.getByRole('dialog')
    await dialog.getByLabel('Desde').fill('2020-01-01')
    await dialog.getByRole('button', { name: 'Descargar ZIP' }).click()

    await expect(dialog.getByRole('alert')).toContainText('366 días')
    await expect(dialog).toBeVisible()
  })
})
