import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useCompanies, useDocuments } from '../api/queries'
import { useSession } from '../auth/session'
import { DocumentsTable } from '../components/DocumentsTable'
import { ErrorAlert, Loading, PageHeader, SelectField } from '../components/ui'
import { BILLING_ROLES } from '../lib/format'

const PAGE = 25

export function Documents() {
  const { hasRole } = useSession()
  const companies = useCompanies()
  const [companyId, setCompanyId] = useState('')
  const [page, setPage] = useState(0)
  const documents = useDocuments(companyId || null, page * PAGE, PAGE)

  return (
    <>
      <PageHeader title="Documentos" subtitle="Comprobantes emitidos, del más reciente al más antiguo">
        {hasRole(...BILLING_ROLES) && (
          <Link className="btn primary" to="/documentos/nuevo">
            Emitir comprobante
          </Link>
        )}
      </PageHeader>
      <div className="card">
        {(companies.data?.length ?? 0) > 1 && (
          <div style={{ maxWidth: 360, marginBottom: 12 }}>
            <SelectField
              label="Empresa"
              value={companyId}
              onChange={(event) => {
                setCompanyId(event.target.value)
                setPage(0)
              }}
            >
              <option value="">Todas</option>
              {companies.data?.map((company) => (
                <option key={company.id} value={company.id}>
                  {company.ruc} · {company.legalName}
                </option>
              ))}
            </SelectField>
          </div>
        )}
        <ErrorAlert error={documents.error} />
        {documents.isPending ? <Loading /> : <DocumentsTable documents={documents.data ?? []} companies={companies.data} />}
        <div className="row spread" style={{ marginTop: 12 }}>
          <button className="btn small" type="button" disabled={page === 0} onClick={() => setPage(page - 1)}>
            ← Anteriores
          </button>
          <span className="muted">Página {page + 1}</span>
          <button className="btn small" type="button" disabled={(documents.data?.length ?? 0) < PAGE} onClick={() => setPage(page + 1)}>
            Siguientes →
          </button>
        </div>
      </div>
    </>
  )
}
