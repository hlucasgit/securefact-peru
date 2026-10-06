import { expect, seriousViolations, signIn, test } from './fixtures.ts'
import { totp } from './helpers/api.ts'

test.describe('autenticación', () => {
  test('una ruta protegida lleva al ingreso y, al ingresar, devuelve a ella', async ({ page, tenant }) => {
    await page.goto('/clientes')
    await expect(page).toHaveURL(/\/ingresar$/)

    await signIn(page, tenant.owner)

    await expect(page).toHaveURL(/\/clientes$/)
    await expect(page.getByRole('heading', { name: 'Clientes' })).toBeVisible()
  })

  test('una contraseña equivocada muestra el error y no entra', async ({ page, tenant }) => {
    await signIn(page, { email: tenant.owner.email, password: 'Incorrecta-123456!' })

    await expect(page.getByRole('alert')).toBeVisible()
    await expect(page).toHaveURL(/\/ingresar$/)
  })

  test('cerrar sesión devuelve al ingreso y la sesión no se restaura al recargar', async ({ page, tenant }) => {
    await signIn(page, tenant.owner)
    await expect(page.getByRole('heading', { name: 'Panel' })).toBeVisible()

    await page.getByRole('button', { name: 'Cerrar sesión' }).click()
    await expect(page).toHaveURL(/\/ingresar$/)
    await page.reload()

    await expect(page).toHaveURL(/\/ingresar$/)
    expect(await page.evaluate(() => sessionStorage.getItem('sf.refresh'))).toBeNull()
  })

  test('recargar la página conserva la sesión', async ({ page, tenant }) => {
    await signIn(page, tenant.owner)
    await expect(page.getByRole('heading', { name: 'Panel' })).toBeVisible()

    await page.reload()

    await expect(page.getByRole('heading', { name: 'Panel' })).toBeVisible()
  })

  test('el segundo factor se activa desde Seguridad y después se exige al ingresar', async ({ page, tenant }) => {
    await signIn(page, tenant.owner)
    await page.getByRole('link', { name: 'Seguridad' }).click()
    await page.getByRole('button', { name: 'Configurar verificación' }).click()
    const secret = (await page.locator('dd .mono').first().textContent())!.trim()
    await page.getByLabel('Código de 6 dígitos').fill(totp(secret))
    await page.getByRole('button', { name: 'Confirmar' }).click()
    await expect(page.getByText(/^Activada./)).toBeVisible()

    await page.getByRole('button', { name: 'Cerrar sesión' }).click()
    await signIn(page, tenant.owner)
    const code = page.getByLabel(/Código de verificación/)
    await expect(code).toBeVisible()
    await code.fill('000000')
    await page.getByRole('button', { name: 'Ingresar' }).click()
    await expect(page.getByRole('alert')).toBeVisible()
    // The code of the step that confirmed the enrolment cannot be used again (replay protection): the next step's code is the one the server accepts.
    await code.fill(totp(secret, Date.now() + 30_000))
    await page.getByRole('button', { name: 'Ingresar' }).click()

    await expect(page.getByRole('heading', { name: 'Panel' })).toBeVisible()
  })

  test('la pantalla de ingreso no tiene problemas graves de accesibilidad', async ({ page }) => {
    await page.goto('/ingresar')
    await expect(page.getByRole('button', { name: 'Ingresar' })).toBeVisible()

    expect(await seriousViolations(page)).toEqual([])
  })
})
