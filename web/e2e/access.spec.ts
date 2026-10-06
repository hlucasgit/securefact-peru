import { expect, signIn, test } from './fixtures.ts'
import { createTenant, issueInvoice, newPassword, taxedLine } from './helpers/api.ts'

test.describe('roles y aislamiento entre cuentas', () => {
  test('el propietario crea un usuario de solo lectura desde la interfaz y ese usuario no ve las acciones de emisión', async ({ app, world, browser }) => {
    const reader = { email: `lector-${Date.now()}@e2e.test`, password: newPassword() }
    await app.getByRole('link', { name: 'Usuarios' }).click()
    await app.getByRole('button', { name: 'Nuevo usuario' }).click()
    const dialog = app.getByRole('dialog')
    await dialog.getByLabel('Nombre').fill('Lectora de Prueba')
    await dialog.getByLabel('Correo electrónico').fill(reader.email)
    await dialog.getByLabel('Contraseña inicial').fill(reader.password)
    await dialog.getByLabel('Rol').selectOption({ label: 'Solo lectura' })
    await dialog.getByRole('button', { name: 'Crear usuario' }).click()
    await expect(app.getByRole('row', { name: /Lectora de Prueba/ })).toContainText('Solo lectura')

    const context = await browser.newContext()
    const page = await context.newPage()
    await signIn(page, reader)
    await expect(page.getByRole('heading', { name: 'Panel' })).toBeVisible()
    const nav = page.getByRole('navigation', { name: 'Navegación principal' })
    await expect(nav.getByRole('link', { name: 'Documentos' })).toBeVisible()
    for (const hidden of ['Emitir', 'Resumen diario', 'Usuarios']) await expect(nav.getByRole('link', { name: hidden, exact: true })).toHaveCount(0)
    await expect(page.getByRole('link', { name: 'Emitir comprobante' })).toHaveCount(0)

    // Los menús son comodidad: quien decide es la API. Emitir por la dirección directa es refusado.
    await page.goto('/documentos/nuevo')
    await page.getByLabel('Número', { exact: true }).fill('20100070970')
    await page.getByLabel('Nombre o razón social').fill('DISTRIBUIDORA ANDINA SAC')
    await page.getByLabel('Descripción').fill('Servicio')
    await page.getByLabel('Valor unitario').fill('100')
    await page.getByRole('button', { name: 'Emitir', exact: true }).click()
    await expect(page.getByRole('alert')).toBeVisible()
    await expect(page).toHaveURL(/\/documentos\/nuevo$/)
    expect(world.company.id).toBeTruthy()
    await context.close()
  })

  test('otra cuenta no puede abrir los documentos ni las empresas de la primera por su dirección', async ({ world, browser }) => {
    const invoice = await issueInvoice(world, [taxedLine('Servicio confidencial', 1)])
    const other = await createTenant('Otra cuenta')
    const context = await browser.newContext()
    const page = await context.newPage()
    await signIn(page, other.owner)
    await expect(page.getByRole('heading', { name: 'Panel' })).toBeVisible()

    await page.goto(`/documentos/${invoice.id}`)
    await expect(page.getByRole('alert')).toBeVisible()
    await expect(page.getByText('DISTRIBUIDORA ANDINA SAC')).toHaveCount(0)
    await expect(page.getByText('Servicio confidencial')).toHaveCount(0)

    await page.goto(`/empresas/${world.company.id}`)
    await expect(page.getByRole('alert')).toBeVisible()
    await expect(page.getByText(world.company.legalName)).toHaveCount(0)

    await page.getByRole('link', { name: 'Documentos', exact: true }).click()
    await expect(page.getByText('No hay documentos todavía.')).toBeVisible()
    await context.close()
  })
})
