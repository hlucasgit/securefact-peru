import { useNavigate } from 'react-router-dom'
import { useElectronic } from '../api/queries'
import type { Company, Document } from '../api/types'
import { date, documentName, money } from '../lib/format'
import { Badge, Empty, StateBadge } from './ui'

/** The state is a property of the electronic document, fetched per row (the document list does not carry it). */
function StateCell({ documentId }: { documentId: string }) {
  const { data, isPending } = useElectronic(documentId)
  if (isPending) return <span className="muted">…</span>
  return data ? <StateBadge state={data.state} voided={data.voided} /> : <Badge tone="neutral">Sin generar</Badge>
}

export function DocumentsTable({ documents, companies }: { documents: Document[]; companies?: Company[] }) {
  const navigate = useNavigate()
  if (documents.length === 0) return <Empty>No hay documentos todavía.</Empty>
  const names = new Map(companies?.map((company) => [company.id, company.tradeName ?? company.legalName]))
  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Documento</th>
            <th>Fecha</th>
            <th>Cliente</th>
            {companies && <th>Empresa</th>}
            <th className="num">Total</th>
            <th>Estado</th>
          </tr>
        </thead>
        <tbody>
          {documents.map((document) => (
            <tr key={document.id} className="clickable" onClick={() => void navigate(`/documentos/${document.id}`)}>
              <td className="tight">
                <a href={`/documentos/${document.id}`} onClick={(event) => event.preventDefault()}>
                  {document.series}-{document.number}
                </a>
                <div className="muted">{documentName(document.documentTypeCode)}</div>
              </td>
              <td className="tight">{date(document.issueDate)}</td>
              <td>{document.buyer.name}</td>
              {companies && <td>{names.get(document.companyId) ?? '—'}</td>}
              <td className="num tight">{money(document.totals.payableAmount, document.currency)}</td>
              <td>
                <StateCell documentId={document.id} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
