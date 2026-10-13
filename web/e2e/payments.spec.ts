import { expect, seriousViolations, signIn, test } from './fixtures.ts'
import { adminCredentials } from './helpers/api.ts'

test.describe('pago con tarjeta', () => {
  test('la plataforma configura la pasarela: las llaves se validan, la secreta no se vuelve a mostrar y el simulador se activa', async ({ page }) => {
    await signIn(page, adminCredentials())
    await expect(page.getByRole('heading', { name: 'Inquilinos' })).toBeVisible()
    await page.getByRole('navigation', { name: 'Navegación principal' }).getByRole('link', { name: 'Cobranza' }).click()
    await expect(page.getByRole('heading', { name: 'Pago con tarjeta' })).toBeVisible()
    expect(await seriousViolations(page)).toEqual([])

    // Una llave que no es de Culqi se rechaza con su código.
    await page.getByLabel('Pasarela', { exact: true }).selectOption('Culqi')
    await page.getByLabel(/^Llave pública/).fill('no-es-una-llave')
    await page.getByLabel(/^Llave secreta/).fill('sk' + '_test_AbCdEf0123456789')
    await page.getByRole('button', { name: 'Guardar pasarela' }).click()
    await expect(page.getByRole('alert').filter({ hasText: 'SF-PAY-002' })).toBeVisible()

    // Llaves de prueba de las dos: se guardan, activa, y la secreta queda vacía en el formulario.
    await page.getByLabel(/^Llave pública/).fill('pk_test_AbCdEf0123456789')
    await page.getByLabel('Activar el pago con tarjeta').check()
    await page.getByRole('button', { name: 'Guardar pasarela' }).click()
    await expect(page.getByText('Pasarela guardada.')).toBeVisible()
    await expect(page.getByLabel(/^Llave secreta/)).toHaveValue('')
    await expect(page.getByText(/Estado: activa \(pruebas\)/)).toBeVisible()
    await page.reload()
    await expect(page.getByLabel(/^Llave pública/)).toHaveValue('pk_test_AbCdEf0123456789')
    await expect(page.getByLabel(/^Llave secreta/)).toHaveValue('')
    await expect(page.getByLabel(/^Llave secreta/)).toHaveAttribute('type', 'password')

    // El simulador no pide llaves y se puede apagar.
    await page.getByLabel('Pasarela', { exact: true }).selectOption('Sandbox')
    await expect(page.getByLabel(/^Llave pública/)).toHaveCount(0)
    await page.getByRole('button', { name: 'Guardar pasarela' }).click()
    await expect(page.getByText('Pasarela guardada.')).toBeVisible()
    await expect(page.getByText(/Estado: activa \(simulador\)/)).toBeVisible()
    await page.getByLabel('Activar el pago con tarjeta').uncheck()
    await page.getByRole('button', { name: 'Guardar pasarela' }).click()
    await expect(page.getByText(/Estado: apagada \(simulador\)/)).toBeVisible()
    await expect(page.getByText('No hay pagos por revisar.')).toBeVisible()
  })
})
