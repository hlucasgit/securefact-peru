import { expect, test } from './fixtures.ts'
import { issueInvoice, taxedLine } from './helpers/api.ts'

/** The page does not scroll sideways: wide tables scroll inside their own box. */
async function noHorizontalScroll(page: import('@playwright/test').Page): Promise<boolean> {
  // A string: the code runs in the browser, which this file's types (Node) do not describe.
  return page.evaluate<boolean>('document.documentElement.scrollWidth <= window.innerWidth + 1')
}

test.describe('en un teléfono', () => {
  test('el menú se abre, se navega y ninguna pantalla desborda el ancho', async ({ app, world }) => {
    await issueInvoice(world, [taxedLine('Servicio', 1)])
    const nav = app.getByRole('navigation', { name: 'Navegación principal' })
    await expect(nav).not.toBeInViewport()

    await app.getByRole('button', { name: 'Menú' }).click()
    await expect(nav).toBeInViewport()
    await nav.getByRole('link', { name: 'Documentos', exact: true }).click()
    await expect(app.getByRole('heading', { name: 'Documentos' })).toBeVisible()
    await expect(nav).not.toBeInViewport()
    expect(await noHorizontalScroll(app)).toBe(true)

    for (const path of ['/', '/documentos/nuevo', '/empresas', '/clientes', '/productos', '/resumenes']) {
      await app.goto(path)
      await expect(app.getByRole('heading').first()).toBeVisible()
      expect(await noHorizontalScroll(app), path).toBe(true)
    }
  })

  test('se emite una factura desde el teléfono', async ({ app }) => {
    await app.goto('/documentos/nuevo')
    await app.getByLabel('Número', { exact: true }).fill('20100070970')
    await app.getByLabel('Nombre o razón social').fill('DISTRIBUIDORA ANDINA SAC')
    await app.getByLabel('Descripción').fill('Servicio')
    await app.getByLabel('Valor unitario').fill('100')

    await app.getByRole('button', { name: 'Emitir', exact: true }).click()

    await expect(app.getByRole('heading', { name: 'Factura F001-1' })).toBeVisible()
  })
})
