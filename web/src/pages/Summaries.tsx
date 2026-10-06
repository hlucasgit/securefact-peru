import { useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { useCompanies, useCreateSummary } from '../api/queries'
import type { SummaryResult } from '../api/types'
import { ErrorAlert, Loading, PageHeader, SelectField, StateBadge, TextField, useToast } from '../components/ui'
import { date, todayInLima } from '../lib/format'

export function Summaries() {
  const companies = useCompanies()
  const create = useCreateSummary()
  const toast = useToast()
  const [companyId, setCompanyId] = useState('')
  const [referenceDate, setReferenceDate] = useState(todayInLima())
  const [results, setResults] = useState<SummaryResult[] | null>(null)

  if (companies.isPending) return <Loading />
  const active = companies.data?.filter((company) => company.status === 'Active') ?? []
  const effective = companyId || active[0]?.id || ''

  function submit(event: FormEvent) {
    event.preventDefault()
    create.mutate({ companyId: effective, referenceDate }, { onSuccess: (created) => { setResults(created); toast.ok('Resumen diario generado.') } })
  }

  return (
    <>
      <PageHeader title="Resumen diario" subtitle="Reporta a SUNAT las boletas de un día. Los workers también lo hacen solos al cerrar el día." />
      <form className="card stack" onSubmit={submit}>
        <ErrorAlert error={create.error} />
        <div className="form-grid">
          <SelectField label="Empresa" value={effective} onChange={(event) => setCompanyId(event.target.value)}>
            {active.map((company) => <option key={company.id} value={company.id}>{company.ruc} · {company.legalName}</option>)}
          </SelectField>
          <TextField label="Fecha de las boletas" type="date" required max={todayInLima()} value={referenceDate} onChange={(event) => setReferenceDate(event.target.value)} />
        </div>
        <div><button className="btn primary" type="submit" disabled={create.isPending || !effective}>{create.isPending ? 'Generando…' : 'Generar resumen'}</button></div>
      </form>
      {results && (
        <div className="card">
          <h2>Resultado</h2>
          {results.length === 0 ? <p className="muted">No había boletas por reportar en esa fecha.</p> : (
            <div className="table-wrap">
              <table className="table">
                <thead><tr><th>Archivo</th><th>Fecha</th><th className="num">Boletas</th><th>Estado</th></tr></thead>
                <tbody>
                  {results.map((result) => (
                    <tr key={result.document.id}>
                      <td className="mono">{result.document.fileBaseName}</td>
                      <td>{date(result.referenceDate)}</td>
                      <td className="num">{result.electronicDocumentIds.length}</td>
                      <td><StateBadge state={result.document.state} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          <p className="muted" style={{ marginTop: 12 }}>El envío y la consulta del resultado los hacen los workers en segundo plano. Revise el estado de cada boleta en <Link to="/documentos">Documentos</Link>.</p>
        </div>
      )}
    </>
  )
}
