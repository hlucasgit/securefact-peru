import { readFileSync } from 'node:fs'
import { expect, seriousViolations, test } from './fixtures.ts'
import { issueInvoice, taxedLine } from './helpers/api.ts'

test.describe('datos de la cuenta', () => {
  test('el propietario baja un ZIP con las tablas de su cuenta y los comprobantes firmados', async ({ app, world }) => {
    const invoice = await issueInvoice(world, [taxedLine('Servicio de consultoría', 1)], { send: true })
    const base = `${world.company.ruc}-01-${invoice.series}-${invoice.number}`

    await app.getByRole('link', { name: 'Datos de la cuenta', exact: true }).click()
    await expect(app.getByRole('heading', { name: 'Datos de la cuenta' })).toBeVisible()
    await expect(app.getByText(/No trae contraseñas, certificados digitales/)).toBeVisible()
    expect(await seriousViolations(app)).toEqual([])

    const download = app.waitForEvent('download')
    await app.getByRole('button', { name: 'Descargar los datos de la cuenta' }).click()
    const file = await download
    expect(file.suggestedFilename()).toMatch(/^cuenta-\d{8}\.zip$/)

    // A ZIP: the local-file headers of its entries carry their names in clear.
    const bytes = readFileSync(await file.path())
    expect(bytes.subarray(0, 2).toString('latin1')).toBe('PK')
    const names = bytes.toString('latin1')
    for (const name of ['LEEME.txt', 'datos/empresas.csv', 'datos/comprobantes.csv', 'datos/usuarios.csv', 'datos/contenido.csv', `xml/${base}.xml`, `cdr/R-${base}.zip`]) expect(names).toContain(name)
  })
})
