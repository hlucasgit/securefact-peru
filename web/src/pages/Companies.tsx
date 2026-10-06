import { useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useCompanies, useCreateCompany } from '../api/queries'
import type { CompanyDetails } from '../api/types'
import { ErrorAlert, Empty, Badge, Loading, Modal, PageHeader, SelectField, TextField, useToast } from '../components/ui'
import { useSession } from '../auth/session'
import { ADMIN_ROLES } from '../lib/format'

export function CompanyForm({ initial, onSubmit, busy, error, withRuc, submitLabel }: { initial: CompanyDetails & { ruc?: string }; onSubmit: (value: CompanyDetails & { ruc: string }) => void; busy: boolean; error: unknown; withRuc: boolean; submitLabel: string }) {
  const [value, setValue] = useState({ ruc: '', ...initial })
  const set = <K extends keyof typeof value>(key: K, next: (typeof value)[K]) => setValue((current) => ({ ...current, [key]: next }))
  const text = (key: 'ruc' | 'legalName' | 'fiscalAddress' | 'ubigeo') => ({ value: value[key], onChange: (event: { target: { value: string } }) => set(key, event.target.value) })
  const optional = (key: 'tradeName' | 'taxRegime' | 'contactEmail' | 'detractionAccount') => ({ value: value[key] ?? '', onChange: (event: { target: { value: string } }) => set(key, event.target.value || null) })

  function submit(event: FormEvent) {
    event.preventDefault()
    onSubmit({ ...value, legalName: value.legalName.trim(), fiscalAddress: value.fiscalAddress.trim() })
  }

  return (
    <form className="stack" onSubmit={submit}>
      <ErrorAlert error={error} />
      <div className="form-grid">
        {withRuc && <TextField label="RUC" required pattern="\d{11}" maxLength={11} inputMode="numeric" title="11 dígitos" {...text('ruc')} />}
        <TextField label="Razón social" required maxLength={200} {...text('legalName')} />
        <TextField label="Nombre comercial" maxLength={200} {...optional('tradeName')} />
        <TextField label="Dirección fiscal" required maxLength={300} {...text('fiscalAddress')} />
        <TextField label="Ubigeo" hint="6 dígitos" required pattern="\d{6}" maxLength={6} inputMode="numeric" {...text('ubigeo')} />
        <TextField label="Régimen tributario" maxLength={60} {...optional('taxRegime')} />
        <TextField label="Correo de contacto" type="email" {...optional('contactEmail')} />
        <SelectField label="Moneda por defecto" value={value.defaultCurrency} onChange={(event) => set('defaultCurrency', event.target.value)}>
          <option value="PEN">Soles (PEN)</option>
          <option value="USD">Dólares (USD)</option>
        </SelectField>
        <TextField label="Cuenta de detracciones" hint="Banco de la Nación" inputMode="numeric" maxLength={20} {...optional('detractionAccount')} />
      </div>
      <div className="actions" style={{ justifyContent: 'flex-end' }}>
        <button className="btn primary" type="submit" disabled={busy}>
          {busy ? 'Guardando…' : submitLabel}
        </button>
      </div>
    </form>
  )
}

export const EMPTY_COMPANY: CompanyDetails = { legalName: '', tradeName: null, fiscalAddress: '', ubigeo: '', taxRegime: null, contactEmail: null, timeZone: 'America/Lima', defaultCurrency: 'PEN', detractionAccount: null }

export function Companies() {
  const { data, isPending, error } = useCompanies()
  const create = useCreateCompany()
  const [creating, setCreating] = useState(false)
  const navigate = useNavigate()
  const toast = useToast()
  const { hasRole } = useSession()

  return (
    <>
      <PageHeader title="Empresas" subtitle="Emisores de comprobantes de su cuenta">
        {hasRole(...ADMIN_ROLES) && (
          <button className="btn primary" type="button" onClick={() => setCreating(true)}>
            Nueva empresa
          </button>
        )}
      </PageHeader>
      <ErrorAlert error={error} />
      {isPending ? (
        <Loading />
      ) : data && data.length > 0 ? (
        <div className="card table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>RUC</th>
                <th>Razón social</th>
                <th>Moneda</th>
                <th>Estado</th>
              </tr>
            </thead>
            <tbody>
              {data.map((company) => (
                <tr key={company.id} className="clickable" onClick={() => void navigate(`/empresas/${company.id}`)}>
                  <td className="mono tight">
                    <Link to={`/empresas/${company.id}`}>{company.ruc}</Link>
                  </td>
                  <td>{company.legalName}</td>
                  <td>{company.defaultCurrency}</td>
                  <td>
                    <Badge tone={company.status === 'Active' ? 'ok' : 'neutral'}>{company.status === 'Active' ? 'Activa' : 'Inactiva'}</Badge>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="card">
          <Empty>No hay empresas registradas.</Empty>
        </div>
      )}
      {creating && (
        <Modal title="Nueva empresa" onClose={() => setCreating(false)}>
          <CompanyForm
            initial={EMPTY_COMPANY}
            withRuc
            busy={create.isPending}
            error={create.error}
            submitLabel="Registrar empresa"
            onSubmit={({ ruc, ...details }) =>
              create.mutate(
                { ruc, details },
                {
                  onSuccess: (company) => {
                    toast.ok('Empresa registrada.')
                    setCreating(false)
                    void navigate(`/empresas/${company.id}`)
                  },
                },
              )
            }
          />
        </Modal>
      )}
    </>
  )
}
