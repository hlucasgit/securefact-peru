import { useState, type ChangeEvent } from 'react'
import { useImportCsv } from '../api/queries'
import type { ImportResult, ImportRow } from '../api/types'
import { decodeCsv, downloadText, IMPORT_TEMPLATES, templateCsv, type ImportKind } from '../lib/csv'
import { Badge, ErrorAlert, Field, Modal } from './ui'

const MAX_CHARACTERS = 1_000_000
const MAX_SHOWN = 100

const LABELS: Record<ImportKind, { title: string; plural: string }> = {
  customers: { title: 'Importar clientes', plural: 'clientes' },
  products: { title: 'Importar productos', plural: 'productos' },
}

const STATUS_TEXT: Record<ImportRow['status'], string> = { Ready: 'Listo', Created: 'Creado', Existing: 'Ya existe', Invalid: 'Con error' }

/** Choose a CSV, see what would happen row by row (nothing is written), then import the valid rows. A customer or product that already exists is never changed (ADR-053). */
export function ImportModal({ kind, onClose }: { kind: ImportKind; onClose: () => void }) {
  const run = useImportCsv(kind)
  const [fileName, setFileName] = useState<string | null>(null)
  const [csv, setCsv] = useState<string | null>(null)
  const [result, setResult] = useState<ImportResult | null>(null)
  const [problem, setProblem] = useState<unknown>(null)
  const label = LABELS[kind]
  const template = IMPORT_TEMPLATES[kind]

  async function choose(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0]
    setResult(null)
    setProblem(null)
    if (!file) return
    setFileName(file.name)
    const text = decodeCsv(await file.arrayBuffer())
    if (text.length > MAX_CHARACTERS) {
      setCsv(null)
      setProblem(new Error('El archivo es demasiado grande (máximo 1 millón de caracteres, unas 2000 filas). Divídalo en varios.'))
      return
    }
    setCsv(text)
    run.mutate({ csv: text, commit: false }, { onSuccess: setResult, onError: setProblem })
  }

  function commit() {
    if (csv) run.mutate({ csv, commit: true }, { onSuccess: setResult, onError: setProblem })
  }

  const done = result?.committed === true
  const attention = (result?.rows ?? []).filter((row) => row.status === 'Invalid' || row.status === 'Existing')

  return (
    <Modal title={label.title} onClose={onClose}>
      <div className="stack">
        <p className="muted">
          Suba un archivo CSV (de Excel: «Guardar como → CSV UTF-8»), con una fila de encabezado. Se revisa fila por fila antes de crear nada.
        </p>
        <p className="muted">{template.help}</p>
        <div>
          <button className="btn small" type="button" onClick={() => downloadText(template.fileName, templateCsv(kind))}>
            Descargar la plantilla
          </button>
        </div>
        {!done && (
          <Field label="Archivo CSV">{(id) => <input id={id} className="input" type="file" accept=".csv,.txt,text/csv" onChange={(event) => void choose(event)} />}</Field>
        )}
        {fileName && !done && <p className="muted">{fileName}</p>}
        <ErrorAlert error={problem} />
        {run.isPending && <p role="status">Revisando el archivo…</p>}
        {result && <Summary result={result} plural={label.plural} />}
        {attention.length > 0 && <Problems rows={attention} />}
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>
            {done ? 'Cerrar' : 'Cancelar'}
          </button>
          {result && !done && result.ready > 0 && (
            <button className="btn primary" type="button" onClick={commit} disabled={run.isPending}>
              {`Importar ${result.ready} ${result.ready === 1 ? 'fila' : 'filas'}`}
            </button>
          )}
        </div>
      </div>
    </Modal>
  )
}

function Summary({ result, plural }: { result: ImportResult; plural: string }) {
  const created = result.committed ? result.created : result.ready
  return (
    <div role="status" className="stack">
      <p>
        <strong>{result.committed ? 'Importación terminada.' : 'Revisión del archivo.'}</strong> {result.total} {result.total === 1 ? 'fila' : 'filas'} leídas.
      </p>
      <p>
        <Badge tone="ok">{`${created} ${result.committed ? 'creados' : 'por crear'}`}</Badge>{' '}
        <Badge tone="neutral">{`${result.existing} ya existían`}</Badge>{' '}
        <Badge tone={result.invalid > 0 ? 'bad' : 'neutral'}>{`${result.invalid} con error`}</Badge>
      </p>
      {!result.committed && result.invalid > 0 && <p className="muted">Las filas con error no se importan; corrija el archivo y vuelva a subirlo, o importe solo las demás.</p>}
      {result.committed && result.created === 0 && <p className="muted">No se creó ningún {plural === 'clientes' ? 'cliente' : 'producto'}.</p>}
    </div>
  )
}

function Problems({ rows }: { rows: ImportRow[] }) {
  const shown = rows.slice(0, MAX_SHOWN)
  return (
    <div className="table-wrap">
      <table className="table">
        <caption className="muted">Filas que no se importan{rows.length > shown.length ? ` (se muestran ${shown.length} de ${rows.length})` : ''}</caption>
        <thead>
          <tr>
            <th>Línea</th>
            <th>Clave</th>
            <th>Estado</th>
            <th>Motivo</th>
          </tr>
        </thead>
        <tbody>
          {shown.map((row) => (
            <tr key={row.line}>
              <td className="tight">{row.line}</td>
              <td className="mono">{row.key ?? '—'}</td>
              <td className="tight">
                <Badge tone={row.status === 'Invalid' ? 'bad' : 'neutral'}>{STATUS_TEXT[row.status]}</Badge>
              </td>
              <td>{row.message}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
