import { useState } from 'react'
import { fetchBlob } from '../api/http'
import { ErrorAlert, PageHeader } from '../components/ui'
import { save } from '../lib/download'
import { todayInLima } from '../lib/format'

/** The path of the package of the account (ADR-074). */
export const ACCOUNT_EXPORT_PATH = '/api/v1/account/export'

const INCLUDED = [
  'Su cuenta, sus empresas y establecimientos, sus series y sus certificados (solo quién es el titular y cuándo vencen).',
  'Sus clientes, sus productos y servicios.',
  'Cada comprobante con sus líneas y la respuesta de SUNAT, y el XML firmado y el CDR tal como se guardan.',
  'Sus guías de remisión, con su XML y su CDR.',
  'Sus usuarios y roles, sus llaves de API (sin la llave), sus webhooks (sin el secreto) y la auditoría de la cuenta.',
  'Lo que se le cobró y lo que pagó.',
]

/** The owner takes everything the account has with it: spreadsheets and the signed files, in one ZIP. */
export function AccountData() {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<unknown>(null)

  async function download() {
    setBusy(true)
    setError(null)
    try {
      save(await fetchBlob(ACCOUNT_EXPORT_PATH), `cuenta-${todayInLima().replaceAll('-', '')}.zip`)
    } catch (failure) {
      setError(failure)
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <PageHeader title="Datos de la cuenta" subtitle="Lléveselos cuando quiera: son suyos" />
      <div className="card stack">
        <p style={{ marginTop: 0 }}>
          Un solo archivo ZIP con las tablas de su cuenta, que se abren en Excel, y los comprobantes firmados con su constancia de SUNAT. Sirve como copia de respaldo y para cambiar de proveedor. Conservar los comprobantes y su CDR es una
          obligación de su empresa: guarde el archivo en un lugar seguro.
        </p>
        <h3>Qué trae</h3>
        <ul>
          {INCLUDED.map((item) => (
            <li key={item}>{item}</li>
          ))}
        </ul>
        <p className="hint">
          No trae contraseñas, certificados digitales ni sus claves, credenciales SOL ni de la API de SUNAT, llaves de API ni secretos de webhooks: esos no salen de la plataforma. Tampoco trae los PDF, que se generan por comprobante. Cada descarga queda en
          la auditoría. Una cuenta con muchos comprobantes puede tardar unos minutos.
        </p>
        <ErrorAlert error={error} />
        <div className="actions">
          <button className="btn primary" type="button" disabled={busy} onClick={() => void download()}>
            {busy ? 'Preparando el archivo…' : 'Descargar los datos de la cuenta'}
          </button>
        </div>
      </div>
    </>
  )
}
