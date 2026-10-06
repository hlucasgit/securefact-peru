import { AxeBuilder } from '@axe-core/playwright'
import { test as base, expect, type Page } from '@playwright/test'
import { createReadyCompany, createTenant, type Credentials, type ReadyCompany, type Tenant } from './helpers/api.ts'

export async function signIn(page: Page, credentials: Credentials): Promise<void> {
  await page.goto('/ingresar')
  await page.getByLabel('Correo electrónico').fill(credentials.email)
  await page.getByLabel('Contraseña').fill(credentials.password)
  await page.getByRole('button', { name: 'Ingresar' }).click()
}

interface Fixtures {
  /** A fresh tenant with its owner. */
  tenant: Tenant
  /** The tenant, with a company that is ready to issue. */
  world: { tenant: Tenant } & ReadyCompany
  /** The page, already signed in as the owner of the world. */
  app: Page
}

export const test = base.extend<Fixtures>({
  // Playwright reads the dependencies of a fixture from the destructured first argument, so it must be a pattern, here an empty one.
  // oxlint-disable-next-line no-empty-pattern
  tenant: async ({}, use, info) => {
    await use(await createTenant(info.title.slice(0, 24).replace(/[^A-Za-z0-9 ]/g, '')))
  },
  world: async ({ tenant }, use) => {
    await use({ tenant, ...(await createReadyCompany(tenant.api)) })
  },
  app: async ({ page, world }, use) => {
    await signIn(page, world.tenant.owner)
    await expect(page.getByRole('heading', { name: 'Panel' })).toBeVisible()
    await use(page)
  },
})

export { expect }

/** Serious and critical accessibility violations of the page as it is now. */
export async function seriousViolations(page: Page): Promise<string[]> {
  const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze()
  return result.violations.filter((violation) => violation.impact === 'serious' || violation.impact === 'critical').map((violation) => `${violation.id}: ${violation.help} (${violation.nodes.length})`)
}
