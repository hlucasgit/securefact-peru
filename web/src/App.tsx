import { Navigate, Route, Routes, useLocation } from 'react-router-dom'
import { useSession } from './auth/session'
import { Layout } from './components/Layout'
import { Loading } from './components/ui'
import { Rules, Security, Users } from './pages/Account'
import { CompanyDetail } from './pages/CompanyDetail'
import { Companies } from './pages/Companies'
import { Dashboard } from './pages/Dashboard'
import { DocumentDetail } from './pages/DocumentDetail'
import { Documents } from './pages/Documents'
import { Login } from './pages/Login'
import { Customers, Products } from './pages/MasterData'
import { NewDocument } from './pages/NewDocument'
import { NewNote } from './pages/NewNote'
import { Summaries } from './pages/Summaries'

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
        <Route index element={<Dashboard />} />
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
