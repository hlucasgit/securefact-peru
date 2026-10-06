import { useState } from 'react'
import { NavLink, Outlet } from 'react-router-dom'
import { useSession } from '../auth/session'
import { BrandMark, useBrand } from '../branding/BrandingProvider'
import { ADMIN_ROLES, AUDIT_ROLES, BILLING_ROLES, PLAN_ROLES, PLATFORM_ROLES, QUEUE_ROLES, RESELLER_ROLES, ROLE_LABELS } from '../lib/format'

interface Item {
  to: string
  label: string
  roles?: string[]
}

const PLATFORM_GROUPS: { title: string; items: Item[] }[] = [
  { title: 'Plataforma', items: [{ to: '/plataforma/inquilinos', label: 'Inquilinos' }, { to: '/plataforma/planes', label: 'Planes' }, { to: '/plataforma/revendedores', label: 'Revendedores' }, { to: '/auditoria', label: 'Auditoría' }] },
  { title: 'Cuenta', items: [{ to: '/seguridad', label: 'Seguridad' }, { to: '/reglas', label: 'Reglas' }] },
]

const RESELLER_GROUPS: { title: string; items: Item[] }[] = [
  { title: 'Revendedor', items: [{ to: '/revendedor/cuentas', label: 'Mis cuentas' }, { to: '/revendedor/marca', label: 'Marca' }] },
  { title: 'Cuenta', items: [{ to: '/seguridad', label: 'Seguridad' }] },
]

const GROUPS: { title: string; items: Item[] }[] = [
  { title: 'Operación', items: [{ to: '/', label: 'Panel' }, { to: '/documentos', label: 'Documentos' }, { to: '/documentos/nuevo', label: 'Emitir', roles: BILLING_ROLES }, { to: '/resumenes', label: 'Resumen diario', roles: BILLING_ROLES }] },
  { title: 'Datos', items: [{ to: '/clientes', label: 'Clientes' }, { to: '/productos', label: 'Productos' }, { to: '/empresas', label: 'Empresas' }] },
  { title: 'Cuenta', items: [{ to: '/usuarios', label: 'Usuarios', roles: ADMIN_ROLES }, { to: '/plan', label: 'Plan y consumo', roles: PLAN_ROLES }, { to: '/auditoria', label: 'Auditoría', roles: AUDIT_ROLES }, { to: '/mensajes', label: 'Mensajes fallidos', roles: QUEUE_ROLES }, { to: '/seguridad', label: 'Seguridad' }, { to: '/reglas', label: 'Reglas' }] },
]

export function Layout() {
  const { principal, hasRole, logout } = useSession()
  const [open, setOpen] = useState(false)
  const role = principal?.roles[0]
  const brand = useBrand()

  return (
    <div className="shell">
      <aside className={`sidebar${open ? ' open' : ''}`}>
        <div className="brand">
          <BrandMark />
          <small>{brand ? 'Facturación electrónica · con tecnología SecureFact' : 'Facturación electrónica'}</small>
        </div>
        <nav aria-label="Navegación principal">
          {(hasRole(...PLATFORM_ROLES) ? PLATFORM_GROUPS : hasRole(...RESELLER_ROLES) ? RESELLER_GROUPS : GROUPS).map((group) => {
            const items = group.items.filter((item) => !item.roles || hasRole(...item.roles))
            return (
              items.length > 0 && (
                <div key={group.title}>
                  <div className="nav-section">{group.title}</div>
                  {items.map((item) => (
                    <NavLink key={item.to} to={item.to} end={item.to === '/' || item.to === '/documentos'} className={({ isActive }) => `nav-link${isActive ? ' active' : ''}`} onClick={() => setOpen(false)}>
                      {item.label}
                    </NavLink>
                  ))}
                </div>
              )
            )
          })}
        </nav>
        <div className="sidebar-foot">
          <div>{role ? (ROLE_LABELS[role] ?? role) : 'Sesión'}</div>
          <button className="btn small" type="button" onClick={() => void logout()}>
            Cerrar sesión
          </button>
        </div>
      </aside>
      <main className="main">
        <button className="btn menu-toggle" type="button" aria-expanded={open} onClick={() => setOpen((value) => !value)}>
          Menú
        </button>
        <Outlet />
      </main>
    </div>
  )
}
