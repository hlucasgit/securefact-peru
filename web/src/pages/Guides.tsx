import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useCompanies, useGuides } from '../api/queries'
import type { GreState } from '../api/types'
import { useSession } from '../auth/session'
import { GreBadge } from '../components/GreBadge'
import { Empty, ErrorAlert, Loading, PageHeader, SelectField } from '../components/ui'
import { BILLING_ROLES, GRE_LABELS, date } from '../lib/format'

const PAGE = 25

export function Guides() {
  const { hasRole } = useSession()
  const navigate = useNavigate()
  const companies = useCompanies()
  const [companyId, setCompanyId] = useState('')
  const [state, setState] = useState<GreState | ''>('')
  const [page, setPage] = useState(0)
  const guides = useGuides(companyId || null, state, page * PAGE, PAGE)
  const names = new Map(companies.data?.map((company) => [company.id, company.legalName]))
  const several = (companies.data?.length ?? 0) > 1

  return (
    <>
      <PageHeader title="Guías de remisión" subtitle="Guías de remisión remitente, de la más reciente a la más antigua">
        {hasRole(...BILLING_ROLES) && <Link className="btn primary" to="/guias/nueva">Emitir guía</Link>}
      </PageHeader>
      <div className="card">
        <div className="form-grid" style={{ marginBottom: 12 }}>
          {several && (
            <SelectField label="Empresa" value={companyId} onChange={(event) => { setCompanyId(event.target.value); setPage(0) }}>
              <option value="">Todas</option>
              {companies.data?.map((company) => <option key={company.id} value={company.id}>{company.ruc} · {company.legalName}</option>)}
            </SelectField>
          )}
          <SelectField label="Estado" value={state} onChange={(event) => { setState(event.target.value as GreState | ''); setPage(0) }}>
            <option value="">Todos</option>
            {Object.entries(GRE_LABELS).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
          </SelectField>
        </div>
        <ErrorAlert error={guides.error} />
        {guides.isPending ? <Loading /> : (guides.data?.length ?? 0) === 0 ? <Empty>Sin guías. Cree una serie «T…» en la empresa y emita la primera.</Empty> : (
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th>Guía</th><th>Fecha</th><th>Destinatario</th>{several && <th>Empresa</th>}<th>Estado</th></tr></thead>
              <tbody>
                {guides.data?.map((guide) => (
                  <tr key={guide.id} className="clickable" onClick={() => void navigate(`/guias/${guide.id}`)}>
                    <td className="tight"><a href={`/guias/${guide.id}`} onClick={(event) => event.preventDefault()}>{guide.name}</a></td>
                    <td className="tight">{date(guide.issueDate)}</td>
                    <td>{guide.recipientName}<div className="muted">{guide.recipientDocument}</div></td>
                    {several && <td>{names.get(guide.companyId) ?? '—'}</td>}
                    <td><GreBadge state={guide.state} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <div className="row spread" style={{ marginTop: 12 }}>
          <button className="btn small" type="button" disabled={page === 0} onClick={() => setPage(page - 1)}>← Anteriores</button>
          <span className="muted">Página {page + 1}</span>
          <button className="btn small" type="button" disabled={(guides.data?.length ?? 0) < PAGE} onClick={() => setPage(page + 1)}>Siguientes →</button>
        </div>
      </div>
    </>
  )
}
