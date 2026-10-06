import { Navigate, Route, Routes, useLocation } from 'react-router-dom'
import { useSession } from './auth/session'
import { PLATFORM_ROLES } from './lib/format'
import { Layout } from './components/Layout'
import { Loading } from './components/ui'
import { Rules, Security, Users } from './pages/Account'
import { Audit, DeadMessages } from './pages/Audit'
import { CompanyDetail } from './pages/CompanyDetail'
import { Companies } from './pages/Companies'
import { Dashboard } from './pages/Dashboard'
import { DocumentDetail } from './pages/DocumentDetail'
import { Documents } from './pages/Documents'
import { Login } from './pages/Login'
import { Customers, Products } from './pages/MasterData'
import { NewDocument } from './pages/NewDocument'
import { NewNote } from './pages/NewNote'
import { TenantDetail, Tenants } from './pages/Platform'
import { Summaries } from './pages/Summaries'

/** The home of a user: platform staff administer tenants and have no companies or documents of their own. */
function Home() {
  const { hasRole } = useSession()
  return hasRole(...PLATFORM_ROLES) ? <Navigate to="/plataforma/inquilinos" replace /> : <Dashboard />
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
      <Route element={<Protected />}>
        <Route index element={<Home />} />
        <Route path="plataforma/inquilinos" element={<Tenants />} />
        <Route path="plataforma/inquilinos/:id" element={<TenantDetail />} />
        <Route path="auditoria" element={<Audit />} />
        <Route path="mensajes" element={<DeadMessages />} />
        <Route path="documentos" element={<Documents />} />
        <Route path="documentos/nuevo" element={<NewDocument />} />
        <Route path="documentos/:id" element={<DocumentDetail />} />
        <Route path="documentos/:id/nota" element={<NewNote />} />
        <Route path="resumenes" element={<Summaries />} />
        <Route path="clientes" element={<Customers />} />
        <Route path="productos" element={<Products />} />
        <Route path="empresas" element={<Companies />} />
        <Route path="empresas/:id" element={<CompanyDetail />} />
        <Route path="usuarios" element={<Users />} />
        <Route path="seguridad" element={<Security />} />
        <Route path="reglas" element={<Rules />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes>
  )
}
