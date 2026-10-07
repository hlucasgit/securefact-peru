import { expect, seriousViolations, signIn, test } from './fixtures.ts'
import { adminCredentials, login, newPassword } from './helpers/api.ts'
import { mailTo, resetLinkFor } from './helpers/mail.ts'
import { createReseller } from './helpers/resellers.ts'

const inPage = (link: string) => {
  const url = new URL(link)
  return `${url.pathname}${url.hash}`
}

test.describe('recuperación de la contraseña', () => {
  test('se pide el enlace, llega por correo, se elige otra contraseña y la anterior deja de valer', async ({ page }) => {
    const reseller = await createReseller('recupera')

    await page.goto('/ingresar')
    await page.getByRole('link', { name: '¿Olvidó su contraseña?' }).click()
    await expect(page).toHaveURL(/\/recuperar$/)
    expect(await seriousViolations(page)).toEqual([])
    await page.getByLabel('Correo electrónico').fill(reseller.admin.email)
    await page.getByRole('button', { name: 'Enviar el enlace' }).click()
    await expect(page.getByRole('status')).toContainText('Si la dirección tiene una cuenta activa')

    const { link, mail } = await resetLinkFor(reseller.admin.email)
    expect(mail.subject).toContain('Recupere su contraseña')
    expect(link).toMatch(/\/restablecer#token=/) // the token is in the fragment, never in the query

    await page.goto(inPage(link))
    await expect(page.getByRole('heading', { name: 'SecureFact Perú' })).toBeVisible()
    await expect(page).toHaveURL(/\/restablecer$/) // the fragment was removed from the address bar
    expect(await seriousViolations(page)).toEqual([])
    const fresh = newPassword()
    await page.getByLabel(/Contraseña nueva/).fill(fresh)
    await page.getByLabel('Repita la contraseña').fill(`${fresh}-distinta`)
    await page.getByRole('button', { name: 'Cambiar la contraseña' }).click()
    await expect(page.getByRole('alert')).toContainText('no coinciden')
    await page.getByLabel('Repita la contraseña').fill(fresh)
    await page.getByRole('button', { name: 'Cambiar la contraseña' }).click()
    await expect(page.getByRole('status')).toContainText('Su contraseña cambió')

    // The new password signs in; the old one does not; and the link works once.
    await signIn(page, { email: reseller.admin.email, password: fresh })
    await expect(page.getByRole('navigation', { name: 'Navegación principal' })).toBeVisible()
    await page.getByRole('button', { name: 'Cerrar sesión' }).click()
    await signIn(page, reseller.admin)
    await expect(page.getByRole('alert')).toBeVisible()
    await expect(page).toHaveURL(/\/ingresar$/)

    await page.goto(inPage(link))
    const another = newPassword()
    await page.getByLabel(/Contraseña nueva/).fill(another)
    await page.getByLabel('Repita la contraseña').fill(another)
    await page.getByRole('button', { name: 'Cambiar la contraseña' }).click()
    await expect(page.getByRole('alert')).toContainText('El enlace no es válido')
    await expect(page.getByRole('link', { name: 'Pedir un enlace nuevo' })).toBeVisible()
  })

  test('una dirección sin cuenta recibe la misma respuesta y ningún correo; sin enlace la pantalla ofrece pedir otro', async ({ page }) => {
    const ghost = `nadie-${Date.now()}@e2e.test`
    await page.goto('/recuperar')
    await page.getByLabel('Correo electrónico').fill(ghost)
    await page.getByRole('button', { name: 'Enviar el enlace' }).click()
    await expect(page.getByRole('status')).toContainText('Si la dirección tiene una cuenta activa')
    expect(mailTo(ghost)).toHaveLength(0)

    await page.goto('/restablecer')
    await expect(page.getByRole('alert')).toContainText('El enlace no es válido')
    await page.getByRole('link', { name: 'Pedir un enlace nuevo' }).click()
    await expect(page).toHaveURL(/\/recuperar$/)
  })

  test('el correo de un revendedor va con su nombre, su soporte y el enlace a su dominio verificado', async ({ page }) => {
    const stamp = Date.now()
    const reseller = await createReseller('correo')
    const host = `correo-${stamp}.e2e.test`
    const brand = `Distribuidora ${stamp}`
    const admin = await login(adminCredentials())
    await admin.put(`/api/v1/platform/resellers/${reseller.id}/branding`, { brandName: brand, primaryColor: '#0b5394', supportEmail: 'soporte@marca.e2e.test' })
    await admin.put(`/api/v1/platform/resellers/${reseller.id}/host`, { host })
    await admin.post(`/api/v1/platform/resellers/${reseller.id}/domain/verify`) // the simulator of the DNS (Domains__Dns__Provider=Sandbox)

    await page.goto('/recuperar')
    await page.getByLabel('Correo electrónico').fill(reseller.admin.email)
    await page.getByRole('button', { name: 'Enviar el enlace' }).click()
    await expect(page.getByRole('status')).toBeVisible()

    const { link, mail } = await resetLinkFor(reseller.admin.email)
    expect(link.startsWith(`https://${host}/restablecer#token=`)).toBe(true)
    expect(mail.from).toContain(brand)
    expect(mail.replyTo).toContain('soporte@marca.e2e.test')
    expect(mail.subject).toContain(brand)
    expect(mail.body).toContain('Con tecnología SecureFact')
  })
})
