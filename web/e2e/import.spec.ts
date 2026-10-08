import { expect, seriousViolations, signIn, test } from './fixtures.ts'

/** A RUC of 11 digits that passes the check digit of SUNAT. */
function ruc(seed: number): string {
  const body = `20${String(seed).padStart(8, '0')}`
  const weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2]
  const sum = [...body].reduce((total, digit, index) => total + Number(digit) * weights[index], 0)
  return body + String((11 - (sum % 11)) % 10)
}

const csvFile = (name: string, text: string) => ({ name, mimeType: 'text/csv', buffer: Buffer.from(text, 'utf-8') })

test.describe('importación desde CSV', () => {
  test('clientes: se revisa, se explican las filas con error, se importa y repetir el archivo no crea nada', async ({ page, tenant }) => {
    const stamp = Date.now() % 100_000_000
    const text = [
      'tipo_documento,numero_documento,nombre,correo',
      `RUC,${ruc(stamp)},Importada Uno SAC ${stamp},uno@importada.pe`,
      `RUC,20123456789,Con RUC malo,`,
      `DNI,${String(stamp).padStart(8, '1').slice(-8)},Ana Importada,`,
    ].join('\n')

    await signIn(page, tenant.owner)
    await page.getByRole('navigation', { name: 'Navegación principal' }).getByRole('link', { name: 'Clientes' }).click()
    await expect(page.getByRole('heading', { name: 'Clientes' })).toBeVisible()
    await page.getByRole('button', { name: 'Importar CSV' }).click()
    const dialog = page.getByRole('dialog', { name: 'Importar clientes' })

    await dialog.getByLabel('Archivo CSV').setInputFiles(csvFile('clientes.csv', text))
    await expect(dialog.getByText('Revisión del archivo.')).toBeVisible()
    await expect(dialog.getByText('2 por crear')).toBeVisible()
    await expect(dialog.getByText('1 con error')).toBeVisible()
    await expect(dialog.getByRole('row', { name: /3.*El RUC no es válido/ })).toBeVisible()
    expect(await seriousViolations(page)).toEqual([])
    // Nothing was written by the review.
    await dialog.getByRole('button', { name: 'Cancelar' }).click()
    await expect(page.getByText('No hay clientes.')).toBeVisible()

    await page.getByRole('button', { name: 'Importar CSV' }).click()
    await dialog.getByLabel('Archivo CSV').setInputFiles(csvFile('clientes.csv', text))
    await dialog.getByRole('button', { name: 'Importar 2 filas' }).click()
    await expect(dialog.getByText('Importación terminada.')).toBeVisible()
    await expect(dialog.getByText('2 creados')).toBeVisible()
    await dialog.getByRole('button', { name: 'Cerrar' }).click()
    await expect(page.getByText(`Importada Uno SAC ${stamp}`)).toBeVisible()
    await expect(page.getByText('Ana Importada')).toBeVisible()

    // The same file again: the customers exist, nothing is created and nothing is overwritten.
    await page.getByRole('button', { name: 'Importar CSV' }).click()
    await dialog.getByLabel('Archivo CSV').setInputFiles(csvFile('clientes.csv', text.replace('Importada Uno SAC', 'Cambiada')))
    await expect(dialog.getByText('0 por crear')).toBeVisible()
    await expect(dialog.getByText('2 ya existían')).toBeVisible()
    await expect(dialog.getByRole('button', { name: /^Importar \d/ })).toHaveCount(0)
    await dialog.getByRole('button', { name: 'Cancelar' }).click()
    await expect(page.getByText(`Importada Uno SAC ${stamp}`)).toBeVisible()
    await expect(page.getByText('Cambiada')).toHaveCount(0)
  })

  test('productos: un archivo de Excel en español (punto y coma, coma decimal) se lee y la afectación nunca se supone', async ({ page, tenant }) => {
    const stamp = Date.now() % 1_000_000
    const text = [
      'código;descripción;tipo;valor unitario;afectación igv',
      `E-${stamp}-1;Cuaderno A4;bien;12,50;10`,
      `E-${stamp}-2;Instalación;servicio;300;10`,
      `E-${stamp}-3;Afectación inexistente;bien;5;99`,
    ].join('\r\n')

    await signIn(page, tenant.owner)
    await page.getByRole('navigation', { name: 'Navegación principal' }).getByRole('link', { name: 'Productos' }).click()
    await page.getByRole('button', { name: 'Importar CSV' }).click()
    const dialog = page.getByRole('dialog', { name: 'Importar productos' })

    await dialog.getByLabel('Archivo CSV').setInputFiles(csvFile('productos.csv', text))
    await expect(dialog.getByText('2 por crear')).toBeVisible()
    await expect(dialog.getByRole('row', { name: /4.*afectación/i })).toBeVisible()
    await dialog.getByRole('button', { name: 'Importar 2 filas' }).click()
    await expect(dialog.getByText('Importación terminada.')).toBeVisible()
    await dialog.getByRole('button', { name: 'Cerrar' }).click()

    await expect(page.getByRole('row', { name: new RegExp(`E-${stamp}-1.*Cuaderno A4.*Bien.*12[.,]50`) })).toBeVisible()
    await expect(page.getByRole('row', { name: new RegExp(`E-${stamp}-2.*Instalación.*Servicio`) })).toBeVisible()
    await expect(page.getByText(`E-${stamp}-3`)).toHaveCount(0)

    // A file without the column of the tax treatment is refused whole, with the reason.
    await page.getByRole('button', { name: 'Importar CSV' }).click()
    await dialog.getByLabel('Archivo CSV').setInputFiles(csvFile('sin-afectacion.csv', 'codigo,descripcion,valor_unitario\nX,Y,1\n'))
    await expect(dialog.getByRole('alert')).toContainText('afectacion_igv')
    await expect(dialog.getByRole('button', { name: /^Importar \d/ })).toHaveCount(0)
  })
})
