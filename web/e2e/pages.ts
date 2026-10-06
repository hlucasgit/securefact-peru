import { expect, type Page } from '@playwright/test'

// Actions of the interface that several specs repeat.

export interface LineInput {
  description: string
  quantity: number
  unitValue: number
  /** ISC ad valorem, in percent. */
  iscPercent?: number
  bags?: boolean
}

export interface InvoiceInput {
  type?: 'Factura' | 'Boleta de venta'
  buyer: { documentType?: string; number: string; name: string }
  lines: LineInput[]
}

/** Fills the issue form and submits it; ends on the detail of the new document. */
export async function issueFromForm(page: Page, input: InvoiceInput): Promise<void> {
  await page.goto('/documentos/nuevo')
  await expect(page.getByRole('heading', { name: 'Emitir comprobante' })).toBeVisible()
  if (input.type) await page.getByLabel('Tipo', { exact: true }).selectOption({ label: input.type })
  if (input.buyer.documentType) await page.getByLabel('Tipo de documento', { exact: true }).selectOption({ label: input.buyer.documentType })
  await page.getByLabel('Número', { exact: true }).fill(input.buyer.number)
  await page.getByLabel('Nombre o razón social').fill(input.buyer.name)

  for (const [index, line] of input.lines.entries()) {
    if (index > 0) await page.getByRole('button', { name: 'Agregar ítem' }).click()
    const card = page.locator('fieldset.line-card').nth(index)
    await card.getByLabel('Descripción').fill(line.description)
    await card.getByLabel('Cantidad').fill(String(line.quantity))
    await card.getByLabel('Valor unitario').fill(String(line.unitValue))
    if (line.iscPercent !== undefined) {
      await card.getByLabel('ISC', { exact: true }).selectOption('AdValorem')
      await card.getByLabel('Tasa del ISC (%)').fill(String(line.iscPercent))
    }
    if (line.bags) await card.getByLabel(/Bolsas de plástico/).check()
  }

  await page.getByRole('button', { name: 'Emitir', exact: true }).click()
  await expect(page).toHaveURL(/\/documentos\/[0-9a-f-]{36}$/)
}

/**
 * On the detail of a document: generates and signs the XML, sends it, and waits for the state. The workers also send what is ready, so the button may be gone already; the page
 * looks again by itself while the document is on its way.
 */
export async function prepareAndSend(page: Page, finalState: string | RegExp = 'Aceptado'): Promise<void> {
  await page.getByRole('button', { name: 'Generar y firmar XML' }).click()
  await expect(page.locator('.page-head .badge')).toBeVisible()
  await page.getByRole('button', { name: 'Enviar a SUNAT' }).click({ timeout: 3_000 }).catch(() => undefined)
  await expect(page.locator('.page-head .badge')).toHaveText(finalState, { timeout: 25_000 })
}

/** The text of the totals card of a document detail. */
export async function totalsText(page: Page): Promise<string> {
  return (await page.locator('.card', { has: page.getByRole('heading', { name: 'Totales' }) }).textContent()) ?? ''
}
