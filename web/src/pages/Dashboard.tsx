import { Link } from 'react-router-dom'
import { useCompanies, useDocuments, useExpiringCertificates } from '../api/queries'
import { DocumentsTable } from '../components/DocumentsTable'
import { ErrorAlert, Loading, PageHeader } from '../components/ui'
import { useSession } from '../auth/session'
import { BILLING_ROLES, date } from '../lib/format'

export function Dashboard() {
  const { hasRole, support } = useSession()
  const companies = useCompanies()
  const documents = useDocuments(null, 0, 10)
  const expiring = useExpiringCertificates()

  if (companies.isPending || documents.isPending) return <Loading />
  const error = companies.error ?? documents.error
  const activeCompanies = companies.data?.filter((company) => company.status === 'Active') ?? []

  return (
    <>
      <PageHeader title="Panel" subtitle="Resumen de su operación">
        {hasRole(...BILLING_ROLES) && (
          <Link className="btn primary" to="/documentos/nuevo">
            Emitir comprobante
          </Link>
        )}
      </PageHeader>
      <ErrorAlert error={error} />
      {companies.data && activeCompanies.length === 0 && (
        <div className="alert info">
          {support ? (
            'La cuenta aún no tiene empresas.'
          ) : (
            <>
              Aún no tiene empresas. <Link to="/empresas">Registre la primera</Link> para configurar su certificado digital, sus credenciales SOL y sus series.
            </>
          )}
        </div>
      )}
      {expiring.data && expiring.data.length > 0 && (
        <div className="alert warn" role="alert">
          {expiring.data.length === 1 ? 'Un certificado digital vence' : `${expiring.data.length} certificados digitales vencen`} en los próximos 30 días (el primero, el {date(expiring.data[0].notAfter)}). Súbalo renovado desde la empresa
          correspondiente.
        </div>
      )}
      <div className="grid cols-3" style={{ marginBottom: 18 }}>
        <div className="stat">
          <div className="label">Empresas activas</div>
          <div className="value">{activeCompanies.length}</div>
        </div>
        <div className="stat">
          <div className="label">Documentos recientes</div>
          <div className="value">{documents.data?.length ?? 0}</div>
        </div>
        <div className="stat">
          <div className="label">Certificados por vencer (30 días)</div>
          <div className="value">{expiring.data?.length ?? '—'}</div>
        </div>
      </div>
      <div className="card">
        <div className="row spread" style={{ marginBottom: 8 }}>
          <h2>Últimos documentos</h2>
          <Link to="/documentos">Ver todos</Link>
        </div>
        <DocumentsTable documents={documents.data ?? []} companies={companies.data} />
      </div>
    </>
  )
}
