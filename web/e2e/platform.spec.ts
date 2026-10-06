import type { Page } from '@playwright/test'
import { expect, seriousViolations, signIn, test } from './fixtures.ts'
import { adminCredentials, createTenant, login, newPassword } from './helpers/api.ts'

async function signInAsAdmin(page: Page): Promise<void> {
  await signIn(page, adminCredentials())
  await expect(page.getByRole('heading', { name: 'Inquilinos' })).toBeVisible()
}

async function openTenant(page: Page, name: string): Promise<void> {
  await page.getByLabel('Buscar por nombre').fill(name)
  await page.getByRole('link', { name }).click()
  await expect(page.getByRole('heading', { name })).toBeVisible()
}

test.describe('administración de plataforma', () => {
  test('el administrador cae en los inquilinos y solo ve el menú de la plataforma', async ({ page }) => {
    await signInAsAdmin(page)

    const nav = page.getByRole('navigation', { name: 'Navegación principal' })
    await expect(nav.getByRole('link', { name: 'Inquilinos' })).toBeVisible()
    await expect(nav.getByRole('link', { name: 'Auditoría' })).toBeVisible()
    for (const hidden of ['Documentos', 'Emitir', 'Clientes', 'Empresas']) await expect(nav.getByRole('link', { name: hidden, exact: true })).toHaveCount(0)
    await expect(page).toHaveURL(/\/plataforma\/inquilinos$/)
  })

  test('crear un inquilino con su propietario desde la interfaz: el propietario ya puede ingresar', async ({ page, browser }) => {
    const name = `Nueva cuenta ${Date.now()}`
    const owner = { email: `propietario-${Date.now()}@e2e.test`, password: newPassword() }
    await signInAsAdmin(page)

    await page.getByRole('button', { name: 'Nuevo inquilino' }).click()
    const dialog = page.getByRole('dialog')
    await dialog.getByLabel('Nombre de la cuenta').fill(name)
    await dialog.getByLabel('Nombre del propietario').fill('Propietaria Nueva')
    await dialog.getByLabel('Correo del propietario').fill(owner.email)
    await dialog.getByLabel('Contraseña inicial').fill(owner.password)
    await dialog.getByRole('button', { name: 'Crear inquilino' }).click()

    await expect(page.getByRole('heading', { name })).toBeVisible()
    await expect(page.getByRole('row', { name: /Propietaria Nueva/ })).toContainText('Propietario')
    await expect(page.getByText('Activo', { exact: true }).first()).toBeVisible()

    const context = await browser.newContext()
    const ownerPage = await context.newPage()
    await signIn(ownerPage, owner)
    await expect(ownerPage.getByRole('heading', { name: 'Panel' })).toBeVisible()
    await context.close()
  })

  test('suspender expulsa al inquilino y reactivar lo devuelve; todo queda en la auditoría con su motivo', async ({ page, tenant, browser }) => {
    await signInAsAdmin(page)
    const context = await browser.newContext()
    const ownerPage = await context.newPage()
    await signIn(ownerPage, tenant.owner)
    await expect(ownerPage.getByRole('heading', { name: 'Panel' })).toBeVisible()

    await openTenant(page, tenant.name)
    await page.getByRole('button', { name: 'Suspender' }).click()
    await page.getByRole('dialog').getByLabel('Motivo').fill('Falta de pago de la suscripción')
    await page.getByRole('dialog').getByRole('button', { name: 'Suspender' }).click()
    await expect(page.getByText('Cuenta suspendida')).toBeVisible()
    await expect(page.locator('.page-head .badge')).toHaveText('Suspendido')

    // La sesión abierta del propietario deja de servir y, al ingresar, la pantalla dice por qué.
    await ownerPage.getByRole('link', { name: 'Clientes' }).click()
    await expect(ownerPage).toHaveURL(/\/ingresar$/)
    await signIn(ownerPage, tenant.owner)
    await expect(ownerPage.getByRole('alert')).toContainText('suspendida')
    await expect(ownerPage.getByRole('alert')).toContainText('SF-TEN-002')

    await page.getByRole('button', { name: 'Reactivar' }).click()
    await page.getByRole('dialog').getByLabel('Motivo').fill('Pago regularizado')
    await page.getByRole('dialog').getByRole('button', { name: 'Reactivar' }).click()
    await expect(page.locator('.page-head .badge')).toHaveText('Activo')
    // Al volver a ingresar, regresa a la pantalla en la que estaba.
    await signIn(ownerPage, tenant.owner)
    await expect(ownerPage.getByRole('heading', { name: 'Clientes' })).toBeVisible()
    await context.close()

    await page.getByRole('link', { name: 'Ver la auditoría de esta cuenta' }).click()
    await expect(page.getByRole('heading', { name: 'Auditoría' })).toBeVisible()
    const suspension = page.getByRole('row', { name: /tenancy\.tenant\.suspended/ })
    await expect(suspension).toBeVisible()
    await suspension.getByText('Ver').click()
    await expect(suspension).toContainText('Falta de pago de la suscripción')
    await expect(page.getByRole('row', { name: /tenancy\.tenant\.reactivated/ })).toBeVisible()
    await page.getByRole('button', { name: 'Verificar integridad' }).click()
    await expect(page.getByRole('status').filter({ hasText: 'es íntegra' })).toBeVisible()
  })

  test('cerrar una cuenta pide escribir su nombre, es definitivo y no ofrece más acciones', async ({ page, tenant }) => {
    await signInAsAdmin(page)
    await openTenant(page, tenant.name)

    await page.getByRole('button', { name: 'Cerrar cuenta' }).click()
    const dialog = page.getByRole('dialog')
    await dialog.getByLabel('Motivo').fill('Cierre a pedido del contribuyente')
    const confirm = dialog.getByRole('button', { name: 'Cerrar cuenta' })
    await expect(confirm).toBeDisabled()
    await dialog.getByLabel(/Escriba/).fill(tenant.name)
    await expect(confirm).toBeEnabled()
    await confirm.click()

    await expect(page.locator('.page-head .badge')).toHaveText('Cerrado')
    await expect(page.getByText('Cuenta cerrada')).toBeVisible()
    for (const action of ['Suspender', 'Reactivar', 'Cerrar cuenta']) await expect(page.getByRole('button', { name: action })).toHaveCount(0)
    await expect(page.getByRole('button', { name: 'Agregar usuario' })).toHaveCount(0)
  })

  test('la lista de inquilinos se busca por nombre y se filtra por estado', async ({ page, tenant }) => {
    await signInAsAdmin(page)

    await page.getByLabel('Buscar por nombre').fill(tenant.name)
    await expect(page.getByRole('link', { name: tenant.name })).toBeVisible()
    await page.getByLabel('Estado').selectOption({ label: 'Suspendido' })
    await expect(page.getByText('No hay inquilinos que coincidan.')).toBeVisible()
    await page.getByLabel('Estado').selectOption({ label: 'Activo' })
    await expect(page.getByRole('link', { name: tenant.name })).toBeVisible()
  })

  test('el administrador agrega un usuario a la cuenta y puede cerrar sus sesiones y desactivarlo', async ({ page, tenant }) => {
    const user = { email: `contador-${Date.now()}@e2e.test`, password: newPassword() }
    await signInAsAdmin(page)
    await openTenant(page, tenant.name)

    await page.getByRole('button', { name: 'Agregar usuario' }).click()
    const dialog = page.getByRole('dialog')
    await dialog.getByLabel('Nombre').fill('Contador de la Cuenta')
    await dialog.getByLabel('Correo electrónico').fill(user.email)
    await dialog.getByLabel('Contraseña inicial').fill(user.password)
    await dialog.getByLabel('Rol').selectOption({ label: 'Contador' })
    await dialog.getByRole('button', { name: 'Crear usuario' }).click()
    const row = page.getByRole('row', { name: /Contador de la Cuenta/ })
    await expect(row).toContainText('Contador')

    page.on('dialog', (confirm) => void confirm.accept())
    await row.getByRole('button', { name: 'Cerrar sesiones' }).click()
    await expect(page.getByText('Sesiones cerradas.')).toBeVisible()
    await row.getByRole('button', { name: 'Desactivar' }).click()
    await expect(row).toContainText('Inactivo')
  })

  test('el soporte de la plataforma mira pero no cambia', async ({ page, tenant }) => {
    const admin = await login(adminCredentials())
    const support = { email: `soporte-${Date.now()}@e2e.test`, password: newPassword() }
    await admin.post('/api/v1/users', { ...support, displayName: 'Soporte de Prueba', roles: ['PlatformSupport'] })
    await signIn(page, support)
    await expect(page.getByRole('heading', { name: 'Inquilinos' })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Nuevo inquilino' })).toHaveCount(0)

    await openTenant(page, tenant.name)

    for (const action of ['Suspender', 'Cerrar cuenta', 'Agregar usuario']) await expect(page.getByRole('button', { name: action })).toHaveCount(0)
    await expect(page.getByRole('row', { name: /Propietario/ })).toBeVisible()
  })

  test('un propietario ve la auditoría de su cuenta y los mensajes fallidos, y nada de la plataforma', async ({ app }) => {
    const nav = app.getByRole('navigation', { name: 'Navegación principal' })
    await expect(nav.getByRole('link', { name: 'Inquilinos' })).toHaveCount(0)

    await nav.getByRole('link', { name: 'Auditoría' }).click()
    await expect(app.getByRole('heading', { name: 'Auditoría' })).toBeVisible()
    await expect(app.getByRole('row', { name: /tenancy\.tenant\.created|identity\.|organizations\./ }).first()).toBeVisible()
    await expect(app.getByLabel('Cuenta')).toHaveCount(0)
    await app.getByRole('button', { name: 'Verificar integridad' }).click()
    await expect(app.getByRole('status').filter({ hasText: 'es íntegra' })).toBeVisible()

    await nav.getByRole('link', { name: 'Mensajes fallidos' }).click()
    await expect(app.getByRole('heading', { name: 'Mensajes fallidos' })).toBeVisible()
    await expect(app.getByText('No hay mensajes fallidos.')).toBeVisible()
  })

  test('las pantallas de la plataforma no tienen problemas graves de accesibilidad', async ({ page, tenant }) => {
    await signInAsAdmin(page)
    expect(await seriousViolations(page), 'lista').toEqual([])

    await openTenant(page, tenant.name)
    expect(await seriousViolations(page), 'detalle').toEqual([])

    await page.getByRole('button', { name: 'Suspender' }).click()
    expect(await seriousViolations(page), 'diálogo').toEqual([])
    await page.keyboard.press('Escape')

    await page.getByRole('link', { name: 'Auditoría', exact: true }).click()
    await expect(page.getByRole('heading', { name: 'Auditoría' })).toBeVisible()
    expect(await seriousViolations(page), 'auditoría').toEqual([])
  })

  test('se puede crear un inquilino por la API y verlo en la lista sin recargar la sesión', async ({ page }) => {
    const created = await createTenant('Visible en la lista')
    await signInAsAdmin(page)

    await page.getByLabel('Buscar por nombre').fill(created.name)

    await expect(page.getByRole('link', { name: created.name })).toBeVisible()
  })
})
