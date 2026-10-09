import { Navigate, Route, Routes, useLocation } from 'react-router-dom'
import { useSession } from './auth/session'
import { PLATFORM_ROLES, RESELLER_ROLES } from './lib/format'
import { Layout } from './components/Layout'
import { Loading } from './components/ui'
import { Rules, Security, Users } from './pages/Account'
import { Audit, DeadEmails, DeadMessages } from './pages/Audit'
import { CompanyDetail } from './pages/CompanyDetail'
import { Companies } from './pages/Companies'
import { Dashboard } from './pages/Dashboard'
import { DocumentDetail } from './pages/DocumentDetail'
import { Documents } from './pages/Documents'
import { GuideDetail } from './pages/GuideDetail'
import { Guides } from './pages/Guides'
import { Integrations } from './pages/Integrations'
import { Login } from './pages/Login'
import { RecoverPassword } from './pages/RecoverPassword'
import { ResetPassword } from './pages/ResetPassword'
import { Customers, Products } from './pages/MasterData'
import { NewDocument } from './pages/NewDocument'
import { NewCarrierGuide } from './pages/NewCarrierGuide'
import { NewGuide } from './pages/NewGuide'
import { NewNote } from './pages/NewNote'
import { ResellerBrand } from './pages/Brand'
import { Collections, Pricing, ResellerCommissions } from './pages/Billing'
import { MyPlan, Plans } from './pages/Plans'
import { ResellerAccountDetail, ResellerAccounts, Resellers } from './pages/Resellers'
import { TenantDetail, Tenants } from './pages/Platform'
import { Summaries } from './pages/Summaries'

/** The home of a user: platform staff administer tenants and have no companies or documents of their own. */
function Home() {
  const { hasRole } = useSession()
  if (hasRole(...PLATFORM_ROLES)) return <Navigate to="/plataforma/inquilinos" replace />
  return hasRole(...RESELLER_ROLES) ? <Navigate to="/revendedor/cuentas" replace /> : <Dashboard />
}

function Protected() {
  const { principal, restoring } = useSession()
  const location = useLocation()
  if (restoring) return <Loading label="Restaurando la sesión…" />
  if (!principal) return <Navigate to="/ingresar" replace state={{ from: location.pathname }} />
  return <Layout />
}

export function App() {
  return (
    <Routes>
      <Route path="/ingresar" element={<Login />} />
      <Route path="/recuperar" element={<RecoverPassword />} />
      <Route path="/restablecer" element={<ResetPassword />} />
      <Route element={<Protected />}>
        <Route index element={<Home />} />
        <Route path="plataforma/inquilinos" element={<Tenants />} />
        <Route path="plataforma/inquilinos/:id" element={<TenantDetail />} />
        <Route path="plataforma/planes" element={<Plans />} />
        <Route path="plataforma/precios" element={<Pricing />} />
        <Route path="plataforma/cobranza" element={<Collections />} />
        <Route path="plataforma/revendedores" element={<Resellers />} />
        <Route path="plataforma/correos" element={<DeadEmails />} />
        <Route path="revendedor/cuentas" element={<ResellerAccounts />} />
        <Route path="revendedor/marca" element={<ResellerBrand />} />
        <Route path="revendedor/comisiones" element={<ResellerCommissions />} />
        <Route path="revendedor/cuentas/:id" element={<ResellerAccountDetail />} />
        <Route path="plan" element={<MyPlan />} />
        <Route path="auditoria" element={<Audit />} />
        <Route path="mensajes" element={<DeadMessages />} />
        <Route path="documentos" element={<Documents />} />
        <Route path="documentos/nuevo" element={<NewDocument />} />
        <Route path="documentos/:id" element={<DocumentDetail />} />
        <Route path="documentos/:id/nota" element={<NewNote />} />
        <Route path="resumenes" element={<Summaries />} />
        <Route path="guias" element={<Guides />} />
        <Route path="guias/nueva" element={<NewGuide />} />
        <Route path="guias/transportista/nueva" element={<NewCarrierGuide />} />
        <Route path="guias/:id" element={<GuideDetail />} />
        <Route path="clientes" element={<Customers />} />
        <Route path="productos" element={<Products />} />
        <Route path="empresas" element={<Companies />} />
        <Route path="empresas/:id" element={<CompanyDetail />} />
        <Route path="usuarios" element={<Users />} />
        <Route path="integraciones" element={<Integrations />} />
        <Route path="seguridad" element={<Security />} />
        <Route path="reglas" element={<Rules />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes>
  )
}
