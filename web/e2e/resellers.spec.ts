import type { Page } from '@playwright/test'
import { expect, seriousViolations, signIn, test } from './fixtures.ts'
import { adminCredentials, newPassword } from './helpers/api.ts'
import { createPrivatePlan, createReseller } from './helpers/resellers.ts'

async function signInAsAdmin(page: Page): Promise<void> {
  await signIn(page, adminCredentials())
  await expect(page.getByRole('heading', { name: 'Inquilinos' })).toBeVisible()
}

test.describe('revendedores', () => {
  test('la plataforma crea un revendedor y su administrador, que ingresa y solo ve sus cuentas', async ({ page, browser }) => {
    const name = `Distribuidora ${Date.now()}`
    const admin = { email: `admin-${Date.now()}@e2e.test`, password: newPassword() }
    await signInAsAdmin(page)

    await page.getByRole('navigation', { name: 'Navegación principal' }).getByRole('link', { name: 'Revendedores' }).click()
    await page.getByRole('button', { name: 'Nuevo revendedor' }).click()
    await page.getByRole('dialog').getByLabel('Nombre').fill(name)
    await page.getByRole('dialog').getByRole('button', { name: 'Crear revendedor' }).click()
    await expect(page.getByRole('row', { name: new RegExp(name) })).toBeVisible()
    expect(await seriousViolations(page)).toEqual([])

    await page.getByRole('button', { name: `Agregar administrador a ${name}` }).click()
    const dialog = page.getByRole('dialog')
    await dialog.getByLabel('Nombre').fill('Administrador de la distribuidora')
    await dialog.getByLabel('Correo electrónico').fill(admin.email)
    await dialog.getByLabel('Contraseña inicial').fill(admin.password)
    await dialog.getByRole('button', { name: 'Crear administrador' }).click()
    await expect(page.getByRole('dialog')).toHaveCount(0)

    const context = await browser.newContext()
    const reseller = await context.newPage()
    await signIn(reseller, admin)
    await expect(reseller.getByRole('heading', { name: 'Mis cuentas' })).toBeVisible()
    const nav = reseller.getByRole('navigation', { name: 'Navegación principal' })
    await expect(nav.getByRole('link', { name: 'Mis cuentas' })).toBeVisible()
    for (const hidden of ['Inquilinos', 'Planes', 'Documentos', 'Empresas', 'Auditoría']) await expect(nav.getByRole('link', { name: hidden, exact: true })).toHaveCount(0)
    await expect(reseller.getByText('Aún no tiene cuentas.')).toBeVisible()
    expect(await seriousViolations(reseller)).toEqual([])
    await context.close()
  })

  test('el revendedor abre una cuenta con su propietario, le da un plan propio y otro revendedor no la ve', async ({ page, browser }) => {
    const mine = await createReseller('uno')
    const other = await createReseller('dos')
    const plan = await createPrivatePlan(mine.id, `Plan de ${mine.name}`.slice(0, 40), 2)
    const account = `Cliente ${Date.now()}`
    const owner = { email: `cliente-${Date.now()}@e2e.test`, password: newPassword() }

    await signIn(page, mine.admin)
    await page.getByRole('button', { name: 'Nueva cuenta' }).click()
    const dialog = page.getByRole('dialog')
    await dialog.getByLabel('Nombre de la cuenta').fill(account)
    await dialog.getByLabel('Plan').selectOption({ label: `${plan.name} (${plan.code})` })
    await dialog.getByLabel('Nombre del propietario').fill('Propietaria del cliente')
    await dialog.getByLabel('Correo del propietario').fill(owner.email)
    await dialog.getByLabel('Contraseña inicial').fill(owner.password)
    await dialog.getByRole('button', { name: 'Crear cuenta' }).click()

    await expect(page.getByRole('heading', { name: account })).toBeVisible()
    await expect(page.getByText(`Plan ${plan.name}`)).toBeVisible()
    expect(await seriousViolations(page)).toEqual([])
    const detailPath = new URL(page.url()).pathname

    // El propietario ya puede ingresar a su cuenta y ve su plan.
    const ownerContext = await browser.newContext()
    const ownerPage = await ownerContext.newPage()
    await signIn(ownerPage, owner)
    await expect(ownerPage.getByRole('heading', { name: 'Panel' })).toBeVisible()
    await ownerPage.getByRole('link', { name: 'Plan y consumo' }).click()
    await expect(ownerPage.getByText(`Plan ${plan.name}`)).toBeVisible()
    await ownerContext.close()

    // El propietario se crea una sola vez.
    await page.getByRole('button', { name: 'Crear propietario' }).click()
    await page.getByRole('dialog').getByLabel('Nombre del propietario').fill('Otra persona')
    await page.getByRole('dialog').getByLabel('Correo del propietario').fill(`otra-${Date.now()}@e2e.test`)
    await page.getByRole('dialog').getByLabel('Contraseña inicial').fill(newPassword())
    await page.getByRole('dialog').getByRole('button', { name: 'Crear propietario' }).click()
    await expect(page.getByRole('dialog').getByRole('alert')).toContainText('se crea una sola vez')
    await page.keyboard.press('Escape')

    // Otro revendedor no ve la cuenta, ni abriéndola por su dirección.
    const otherContext = await browser.newContext()
    const otherPage = await otherContext.newPage()
    await signIn(otherPage, other.admin)
    await expect(otherPage.getByText('Aún no tiene cuentas.')).toBeVisible()
    await otherPage.goto(detailPath)
    await expect(otherPage.getByRole('alert')).toContainText('no existe')
    await otherContext.close()
  })

  test('la plataforma ve el revendedor de una cuenta y la mueve; apagar al revendedor lo saca sin afectar a sus clientes', async ({ page, browser, tenant }) => {
    const reseller = await createReseller('tres')
    await signInAsAdmin(page)
    await page.getByLabel('Buscar por nombre').fill(tenant.name)
    await page.getByRole('link', { name: tenant.name }).click()
    await expect(page.getByText('Cuenta directa de la plataforma')).toBeVisible()

    await page.getByLabel('Asignar a').selectOption({ label: reseller.name })
    await page.getByRole('button', { name: 'Cambiar revendedor' }).click()
    await expect(page.getByText('Esta cuenta la administra')).toBeVisible()

    const context = await browser.newContext()
    const resellerPage = await context.newPage()
    await signIn(resellerPage, reseller.admin)
    await expect(resellerPage.getByRole('link', { name: tenant.name })).toBeVisible()

    await page.getByRole('navigation', { name: 'Navegación principal' }).getByRole('link', { name: 'Revendedores' }).click()
    await page.getByRole('button', { name: `Editar ${reseller.name}` }).click()
    await page.getByRole('dialog').getByLabel('Activo').uncheck()
    await page.getByRole('dialog').getByRole('button', { name: 'Guardar' }).click()
    await expect(page.getByRole('row', { name: new RegExp(reseller.name) })).toContainText('Desactivado')

    // La sesión del revendedor deja de servir y no puede volver a ingresar; su cliente sigue trabajando.
    await resellerPage.reload()
    await expect(resellerPage).toHaveURL(/\/ingresar$/)
    await signIn(resellerPage, reseller.admin)
    await expect(resellerPage.getByRole('alert')).toBeVisible()
    await context.close()
    await tenant.api.get('/api/v1/companies')
  })
})
