import { expect, seriousViolations, signIn, test } from './fixtures.ts'
import { makePfx, newRuc } from './helpers/api.ts'

test.describe('puesta en marcha de una empresa desde la interfaz', () => {
  test('una cuenta nueva registra su empresa, sus series, su certificado y sus credenciales SOL y queda lista para emitir', async ({ page, tenant }) => {
    const ruc = newRuc()
    await signIn(page, tenant.owner)
    await expect(page.getByText('Aún no tiene empresas.')).toBeVisible()

    // Empresa
    await page.getByRole('link', { name: 'Registre la primera' }).click()
    await page.getByRole('button', { name: 'Nueva empresa' }).click()
    const dialog = page.getByRole('dialog')
    await dialog.getByLabel('RUC').fill(ruc)
    await dialog.getByLabel('Razón social').fill('Bodega Los Andes SAC')
    await dialog.getByLabel('Dirección fiscal').fill('Jr. Cusco 456, Cercado de Lima')
    await dialog.getByLabel('Ubigeo').fill('150101')
    await dialog.getByRole('button', { name: 'Registrar empresa' }).click()
    await expect(page.getByRole('heading', { name: 'Bodega Los Andes SAC' })).toBeVisible()
    await expect(page.getByText(`RUC ${ruc}`)).toBeVisible()

    // Series
    await page.getByRole('tab', { name: 'Series' }).click()
    await expect(page.getByText('Sin series. Cree una para poder emitir.')).toBeVisible()
    await page.getByLabel('Serie', { exact: false }).last().fill('F001')
    await page.getByRole('button', { name: 'Crear serie' }).click()
    await expect(page.getByRole('row', { name: /Factura F001/ })).toBeVisible()
    await page.getByLabel('Tipo de documento').selectOption('03')
    await page.getByLabel('Serie', { exact: false }).last().fill('B001')
    await page.getByRole('button', { name: 'Crear serie' }).click()
    await expect(page.getByRole('row', { name: /Boleta de venta B001/ })).toBeVisible()

    // Certificado digital: a PFX whose subject carries the RUC
    await page.getByRole('tab', { name: 'Certificado digital' }).click()
    await page.getByLabel('Archivo .pfx / .p12').setInputFiles({ name: 'certificado.pfx', mimeType: 'application/x-pkcs12', buffer: makePfx(ruc, 'pw') })
    await page.getByLabel('Contraseña del certificado').fill('pw')
    await page.getByRole('button', { name: 'Cargar certificado' }).click()
    await expect(page.getByText('Certificado cargado.', { exact: true })).toBeVisible()
    await expect(page.getByRole('row', { name: /Representante E2E/ })).toContainText('Activo')
    await expect(page.getByText(/Sin el RUC/)).toHaveCount(0)

    // Credenciales SOL: la contraseña nunca vuelve a mostrarse
    await page.getByRole('tab', { name: 'Credenciales SOL' }).click()
    await page.getByLabel('Usuario SOL').fill('MODDATOS')
    await page.getByLabel('Clave SOL').fill('Sol-Clave-Secreta-77')
    await page.getByRole('button', { name: 'Guardar', exact: true }).click()
    await expect(page.getByText('Guardada (no se muestra)')).toBeVisible()
    await expect(page.getByText('Sol-Clave-Secreta-77')).toHaveCount(0)
    expect(await page.content()).not.toContain('Sol-Clave-Secreta-77')

    // La empresa ya puede emitir
    await page.getByRole('link', { name: 'Emitir', exact: true }).click()
    await expect(page.getByLabel('Serie', { exact: true })).toHaveValue(/.+/)
    await expect(page.getByRole('button', { name: 'Emitir', exact: true })).toBeEnabled()
  })

  test('un certificado de otro contribuyente es refusado y no queda guardado', async ({ app, world }) => {
    await app.goto(`/empresas/${world.company.id}`)
    await app.getByRole('tab', { name: 'Certificado digital' }).click()
    await expect(app.getByRole('row', { name: /Representante E2E/ })).toHaveCount(1)

    await app.getByLabel('Archivo .pfx / .p12').setInputFiles({ name: 'otro.pfx', mimeType: 'application/x-pkcs12', buffer: makePfx(newRuc(), 'pw') })
    await app.getByLabel('Contraseña del certificado').fill('pw')
    await app.getByRole('button', { name: 'Cargar certificado' }).click()

    await expect(app.getByRole('alert')).toContainText('otro contribuyente')
    await expect(app.getByRole('alert')).toContainText('SF-CRT-001')
    await expect(app.getByRole('row', { name: /Representante E2E/ })).toHaveCount(1)
  })

  test('un certificado con una contraseña equivocada es refusado con el motivo', async ({ app, world }) => {
    await app.goto(`/empresas/${world.company.id}`)
    await app.getByRole('tab', { name: 'Certificado digital' }).click()

    await app.getByLabel('Archivo .pfx / .p12').setInputFiles({ name: 'c.pfx', mimeType: 'application/x-pkcs12', buffer: makePfx(world.company.ruc, 'correcta') })
    await app.getByLabel('Contraseña del certificado').fill('equivocada')
    await app.getByRole('button', { name: 'Cargar certificado' }).click()

    await expect(app.getByRole('alert')).toContainText('SF-')
  })

  test('las pantallas de la empresa no tienen problemas graves de accesibilidad', async ({ app, world }) => {
    for (const tab of ['Datos', 'Establecimientos', 'Series', 'Certificado digital', 'Credenciales SOL']) {
      await app.goto(`/empresas/${world.company.id}`)
      await app.getByRole('tab', { name: tab }).click()
      expect(await seriousViolations(app), tab).toEqual([])
    }
  })
})

test.describe('clientes y productos', () => {
  test('un cliente se crea, se busca, se edita y se desactiva', async ({ app }) => {
    await app.getByRole('link', { name: 'Clientes' }).click()
    await app.getByRole('button', { name: 'Nuevo cliente' }).click()
    const dialog = app.getByRole('dialog')
    await dialog.getByLabel('Número de documento').fill('20100070970')
    await dialog.getByLabel('Nombre o razón social').fill('DISTRIBUIDORA ANDINA SAC')
    await dialog.getByLabel('Correo').fill('compras@andina.pe')
    await dialog.getByRole('button', { name: 'Guardar' }).click()
    await expect(app.getByRole('row', { name: /DISTRIBUIDORA ANDINA SAC/ })).toBeVisible()

    await app.getByLabel('Buscar por nombre o documento').fill('zzz-no-existe')
    await expect(app.getByText('No hay clientes.')).toBeVisible()
    await app.getByLabel('Buscar por nombre o documento').fill('andina')
    const row = app.getByRole('row', { name: /DISTRIBUIDORA ANDINA SAC/ })
    await expect(row).toBeVisible()

    await row.getByRole('button', { name: 'Editar' }).click()
    await app.getByRole('dialog').getByLabel('Nombre o razón social').fill('DISTRIBUIDORA ANDINA PERU SAC')
    await app.getByRole('dialog').getByRole('button', { name: 'Guardar' }).click()
    await expect(app.getByRole('row', { name: /ANDINA PERU/ })).toBeVisible()

    // A deactivated customer leaves the list (it is never deleted).
    app.once('dialog', (confirm) => void confirm.accept())
    await app.getByRole('row', { name: /ANDINA PERU/ }).getByRole('button', { name: 'Desactivar' }).click()
    await expect(app.getByRole('row', { name: /ANDINA PERU/ })).toHaveCount(0)
  })

  test('un documento de identidad repetido es refusado con el motivo', async ({ app, world }) => {
    await world.tenant.api.post('/api/v1/customers', { documentTypeCode: '6', documentNumber: '20100070970', name: 'YA EXISTE SAC' })
    await app.getByRole('link', { name: 'Clientes' }).click()
    await app.getByRole('button', { name: 'Nuevo cliente' }).click()
    await app.getByRole('dialog').getByLabel('Número de documento').fill('20100070970')
    await app.getByRole('dialog').getByLabel('Nombre o razón social').fill('OTRO NOMBRE SAC')

    await app.getByRole('dialog').getByRole('button', { name: 'Guardar' }).click()

    await expect(app.getByRole('dialog').getByRole('alert')).toContainText('SF-')
  })

  test('un producto del catálogo completa la línea al emitir', async ({ app }) => {
    await app.getByRole('link', { name: 'Productos' }).click()
    await app.getByRole('button', { name: 'Nuevo producto' }).click()
    const dialog = app.getByRole('dialog')
    await dialog.getByLabel('Código interno').fill('SRV-001')
    await dialog.getByLabel('Descripción').fill('Servicio de consultoría')
    await dialog.getByLabel('Tipo', { exact: true }).selectOption('Service')
    await dialog.getByLabel('Unidad de medida').fill('ZZ')
    await dialog.getByLabel('Valor unitario').fill('500')
    await dialog.getByRole('button', { name: 'Guardar' }).click()
    await expect(app.getByRole('row', { name: /SRV-001/ })).toContainText('S/ 500.00')

    await app.getByRole('link', { name: 'Emitir', exact: true }).click()
    await app.getByLabel('Producto del catálogo').selectOption({ label: 'SRV-001 · Servicio de consultoría' })

    const card = app.locator('fieldset.line-card').first()
    await expect(card.getByLabel('Descripción')).toHaveValue('Servicio de consultoría')
    await expect(card.getByLabel(/^Unidad/)).toHaveValue('ZZ')
    await expect(card.getByLabel('Valor unitario')).toHaveValue('500')
  })
})
