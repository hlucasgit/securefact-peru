import { expect, seriousViolations, signIn, test } from './fixtures.ts'
import { adminCredentials } from './helpers/api.ts'

test.describe('acceso de soporte', () => {
  test('el propietario autoriza, soporte entra solo a mirar, el propietario lo ve y quita la autorización', async ({ tenant, browser }) => {
    const ownerContext = await browser.newContext()
    const owner = await ownerContext.newPage()
    await signIn(owner, tenant.owner)
    await expect(owner.getByRole('heading', { name: 'Panel' })).toBeVisible()

    // Without the authorization of the account, support has no way in.
    const staffContext = await browser.newContext()
    const staff = await staffContext.newPage()
    await signIn(staff, adminCredentials())
    await expect(staff.getByRole('heading', { name: 'Inquilinos' })).toBeVisible()
    await staff.getByLabel('Buscar por nombre').fill(tenant.name)
    await staff.getByRole('link', { name: tenant.name }).click()
    await expect(staff.getByRole('heading', { name: tenant.name })).toBeVisible()
    await expect(staff.getByText(/no autorizó el acceso de soporte/)).toBeVisible()
    await expect(staff.getByRole('button', { name: 'Entrar como soporte', exact: true })).toHaveCount(0)

    // The owner authorizes for a few hours.
    await owner.getByRole('link', { name: 'Acceso de soporte' }).click()
    await expect(owner.getByRole('heading', { name: 'Acceso de soporte' })).toBeVisible()
    await expect(owner.getByText('Nunca autorizó el acceso de soporte.')).toBeVisible()
    await owner.getByRole('button', { name: 'Autorizar acceso' }).click()
    const dialog = owner.getByRole('dialog')
    await dialog.getByLabel('Durante').selectOption('4')
    await dialog.getByLabel(/Nota/).fill('Revisión de series')
    expect(await seriousViolations(owner)).toEqual([])
    await dialog.getByRole('button', { name: 'Autorizar', exact: true }).click()
    await expect(owner.getByText('Vigente', { exact: true })).toBeVisible()
    await expect(owner.getByRole('button', { name: 'Autorizar acceso' })).toBeDisabled()

    // Support enters with a reason and finds the account in read-only mode.
    await staff.reload()
    await expect(staff.getByText(/La cuenta autorizó el acceso hasta/)).toBeVisible()
    await staff.getByRole('button', { name: 'Entrar como soporte', exact: true }).click()
    const reason = staff.getByRole('dialog')
    await reason.getByLabel(/Motivo/).fill('Revisar por qué no ve sus series')
    await reason.getByRole('button', { name: 'Entrar', exact: true }).click()

    await expect(staff.getByText(/Modo soporte\./)).toBeVisible()
    await expect(staff.getByText(`Está viendo «${tenant.name}» en solo lectura`)).toBeVisible()
    await expect(staff.getByRole('heading', { name: 'Panel' })).toBeVisible()
    const nav = staff.getByRole('navigation', { name: 'Navegación principal' })
    await expect(nav.getByRole('link', { name: 'Documentos', exact: true })).toBeVisible()
    for (const hidden of ['Emitir', 'Inquilinos', 'Acceso de soporte']) await expect(nav.getByRole('link', { name: hidden, exact: true })).toHaveCount(0)
    expect(await seriousViolations(staff)).toEqual([])

    // It reads the data of the account, and nothing else.
    await nav.getByRole('link', { name: 'Empresas', exact: true }).click()
    await expect(staff.getByRole('heading', { name: 'Empresas' })).toBeVisible()

    // The owner sees the entry.
    await owner.reload()
    await expect(owner.getByRole('row', { name: /Revisión de series/ })).toContainText('Vigente')
    await expect(owner.getByRole('row', { name: /Revisión de series/ }).getByRole('cell').nth(3)).toHaveText('1')

    // Leaving takes the person back to their own session.
    await staff.getByRole('button', { name: 'Salir del modo soporte' }).click()
    await expect(staff.getByRole('heading', { name: 'Inquilinos' })).toBeVisible()
    await expect(staff.getByText(/Modo soporte\./)).toHaveCount(0)

    // The owner takes the authorization back: support cannot enter again.
    owner.once('dialog', (confirm) => void confirm.accept())
    await owner.getByRole('button', { name: 'Quitar la autorización de soporte' }).click()
    await expect(owner.getByText('Quitada', { exact: true })).toBeVisible()
    await staff.getByLabel('Buscar por nombre').fill(tenant.name)
    await staff.getByRole('link', { name: tenant.name }).click()
    await expect(staff.getByText(/no autorizó el acceso de soporte/)).toBeVisible()

    await ownerContext.close()
    await staffContext.close()
  })
})
