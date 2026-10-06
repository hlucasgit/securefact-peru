import { useState, type FormEvent } from 'react'
import { useParams } from 'react-router-dom'
import {
  useCertificates,
  useCompany,
  useCreateEstablishment,
  useCreateSeries,
  useDeactivateCertificate,
  useDeactivateSeries,
  useEstablishments,
  useRemoveSol,
  useSeries,
  useSetSol,
  useSolCredential,
  useUpdateCompany,
  useUploadCertificate,
} from '../api/queries'
import type { Company } from '../api/types'
import { Badge, ConfirmButton, Empty, ErrorAlert, KeyValues, Loading, PageHeader, SelectField, Tabs, TextField, useToast } from '../components/ui'
import { useSession } from '../auth/session'
import { ADMIN_ROLES, DOCUMENT_TYPES, date, dateTime } from '../lib/format'
import { CompanyForm } from './Companies'

type Tab = 'datos' | 'establecimientos' | 'series' | 'certificado' | 'sol'

export function CompanyDetail() {
  const { id = '' } = useParams()
  const { data: company, isPending, error } = useCompany(id)
  const [tab, setTab] = useState<Tab>('datos')

  if (isPending) return <Loading />
  if (!company) return <ErrorAlert error={error} />

  return (
    <>
      <PageHeader title={company.legalName} subtitle={`RUC ${company.ruc}`}>
        <Badge tone={company.status === 'Active' ? 'ok' : 'neutral'}>{company.status === 'Active' ? 'Activa' : 'Inactiva'}</Badge>
      </PageHeader>
      <Tabs
        value={tab}
        onChange={setTab}
        tabs={[
          { id: 'datos', label: 'Datos' },
          { id: 'establecimientos', label: 'Establecimientos' },
          { id: 'series', label: 'Series' },
          { id: 'certificado', label: 'Certificado digital' },
          { id: 'sol', label: 'Credenciales SOL' },
        ]}
      />
      {tab === 'datos' && <DataTab company={company} />}
      {tab === 'establecimientos' && <EstablishmentsTab companyId={id} />}
      {tab === 'series' && <SeriesTab companyId={id} />}
      {tab === 'certificado' && <CertificateTab companyId={id} ruc={company.ruc} />}
      {tab === 'sol' && <SolTab companyId={id} />}
    </>
  )
}

function DataTab({ company }: { company: Company }) {
  const update = useUpdateCompany(company.id)
  const toast = useToast()
  const { hasRole } = useSession()
  if (!hasRole(...ADMIN_ROLES)) {
    return (
      <div className="card">
        <KeyValues items={[['Dirección fiscal', company.fiscalAddress], ['Ubigeo', company.ubigeo], ['Moneda', company.defaultCurrency], ['Correo', company.contactEmail ?? '—']]} />
      </div>
    )
  }
  return (
    <div className="card">
      <CompanyForm
        initial={company}
        withRuc={false}
        busy={update.isPending}
        error={update.error}
        submitLabel="Guardar cambios"
        onSubmit={({ ruc: _ruc, ...details }) => update.mutate(details, { onSuccess: () => toast.ok('Datos guardados.') })}
      />
    </div>
  )
}

function EstablishmentsTab({ companyId }: { companyId: string }) {
  const { data, isPending, error } = useEstablishments(companyId)
  const create = useCreateEstablishment(companyId)
  const toast = useToast()
  const [form, setForm] = useState({ code: '', name: '', address: '', ubigeo: '' })

  function submit(event: FormEvent) {
    event.preventDefault()
    create.mutate({ code: form.code, details: { name: form.name, address: form.address, ubigeo: form.ubigeo } }, { onSuccess: () => { toast.ok('Establecimiento creado.'); setForm({ code: '', name: '', address: '', ubigeo: '' }) } })
  }

  return (
    <>
      <div className="card">
        <h2>Establecimientos</h2>
        <ErrorAlert error={error} />
        {isPending ? <Loading /> : data && data.length > 0 ? (
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th>Código</th><th>Nombre</th><th>Dirección</th><th>Estado</th></tr></thead>
              <tbody>
                {data.map((item) => (
                  <tr key={item.id}>
                    <td className="mono">{item.code}</td>
                    <td>{item.name}</td>
                    <td>{item.address}</td>
                    <td><Badge tone={item.isActive ? 'ok' : 'neutral'}>{item.isActive ? 'Activo' : 'Inactivo'}</Badge></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : <Empty>Sin establecimientos.</Empty>}
      </div>
      <form className="card stack" onSubmit={submit}>
        <h2>Nuevo establecimiento</h2>
        <ErrorAlert error={create.error} />
        <div className="form-grid">
          <TextField label="Código" hint="4 dígitos, ej. 0000" required maxLength={4} value={form.code} onChange={(event) => setForm({ ...form, code: event.target.value })} />
          <TextField label="Nombre" required value={form.name} onChange={(event) => setForm({ ...form, name: event.target.value })} />
          <TextField label="Dirección" required value={form.address} onChange={(event) => setForm({ ...form, address: event.target.value })} />
          <TextField label="Ubigeo" required pattern="\d{6}" maxLength={6} value={form.ubigeo} onChange={(event) => setForm({ ...form, ubigeo: event.target.value })} />
        </div>
        <div><button className="btn primary" type="submit" disabled={create.isPending}>Crear establecimiento</button></div>
      </form>
    </>
  )
}

function SeriesTab({ companyId }: { companyId: string }) {
  const { data, isPending, error } = useSeries(companyId)
  const create = useCreateSeries(companyId)
  const deactivate = useDeactivateSeries(companyId)
  const toast = useToast()
  const [type, setType] = useState('01')
  const [code, setCode] = useState('')

  function submit(event: FormEvent) {
    event.preventDefault()
    create.mutate({ documentTypeCode: type, code: code.toUpperCase() }, { onSuccess: () => { toast.ok('Serie creada.'); setCode('') } })
  }

  return (
    <>
      <div className="card">
        <h2>Series</h2>
        <ErrorAlert error={error ?? deactivate.error} />
        {isPending ? <Loading /> : data && data.length > 0 ? (
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th>Tipo</th><th>Serie</th><th className="num">Último número</th><th>Estado</th><th /></tr></thead>
              <tbody>
                {data.map((item) => (
                  <tr key={item.id}>
                    <td>{DOCUMENT_TYPES[item.documentTypeCode] ?? item.documentTypeCode}</td>
                    <td className="mono">{item.code}</td>
                    <td className="num">{item.lastNumber}</td>
                    <td><Badge tone={item.isActive ? 'ok' : 'neutral'}>{item.isActive ? 'Activa' : 'Inactiva'}</Badge></td>
                    <td className="right">{item.isActive && <ConfirmButton label="Desactivar" message={`¿Desactivar la serie ${item.code}? Ya no podrá emitir con ella.`} onConfirm={() => deactivate.mutate(item.id)} />}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : <Empty>Sin series. Cree una para poder emitir.</Empty>}
      </div>
      <form className="card stack" onSubmit={submit}>
        <h2>Nueva serie</h2>
        <ErrorAlert error={create.error} />
        <div className="form-grid">
          <SelectField label="Tipo de documento" value={type} onChange={(event) => setType(event.target.value)}>
            {Object.entries(DOCUMENT_TYPES).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
          </SelectField>
          <TextField label="Serie" hint="4 caracteres: F… para facturas, B… para boletas" required maxLength={4} minLength={4} value={code} onChange={(event) => setCode(event.target.value)} />
        </div>
        <div><button className="btn primary" type="submit" disabled={create.isPending}>Crear serie</button></div>
      </form>
    </>
  )
}

function readAsBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(String(reader.result).split(',')[1] ?? '')
    reader.onerror = () => reject(reader.error)
    reader.readAsDataURL(file)
  })
}

function CertificateTab({ companyId, ruc }: { companyId: string; ruc: string }) {
  const { data, isPending, error } = useCertificates(companyId)
  const upload = useUploadCertificate(companyId)
  const deactivate = useDeactivateCertificate(companyId)
  const toast = useToast()
  const [file, setFile] = useState<File | null>(null)
  const [password, setPassword] = useState('')

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!file) return
    const pfxBase64 = await readAsBase64(file)
    upload.mutate({ pfxBase64, password }, { onSuccess: (certificate) => {
      toast.ok(certificate.rucInSubject ? 'Certificado cargado.' : 'Certificado cargado, pero su sujeto no contiene el RUC de la empresa.')
      setFile(null)
      setPassword('')
    } })
  }

  return (
    <>
      <div className="card">
        <h2>Certificados</h2>
        <ErrorAlert error={error ?? deactivate.error} />
        {isPending ? <Loading /> : data && data.length > 0 ? (
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th>Sujeto</th><th>Vigencia</th><th>Estado</th><th /></tr></thead>
              <tbody>
                {data.map((item) => (
                  <tr key={item.id}>
                    <td>
                      {item.subject}
                      <div className="muted mono">{item.thumbprint}</div>
                      {!item.rucInSubject && <Badge tone="warn">Sin el RUC {ruc} en el sujeto</Badge>}
                    </td>
                    <td className="tight">{date(item.notBefore)} – {date(item.notAfter)}</td>
                    <td><Badge tone={item.isActive ? 'ok' : 'neutral'}>{item.isActive ? 'Activo' : 'Inactivo'}</Badge></td>
                    <td className="right">{item.isActive && <ConfirmButton label="Desactivar" message="¿Desactivar este certificado? No se podrá firmar hasta cargar otro." onConfirm={() => deactivate.mutate(item.id)} />}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : <Empty>Sin certificado. Cargue uno para poder firmar los comprobantes.</Empty>}
      </div>
      <form className="card stack" onSubmit={(event) => void submit(event)}>
        <h2>Cargar certificado digital</h2>
        <p className="muted">Archivo PFX/P12 y su contraseña. Se guardan cifrados; la contraseña nunca se muestra ni se registra.</p>
        <ErrorAlert error={upload.error} />
        <div className="form-grid">
          <TextField label="Archivo .pfx / .p12" type="file" accept=".pfx,.p12" required onChange={(event) => setFile(event.target.files?.[0] ?? null)} />
          <TextField label="Contraseña del certificado" type="password" autoComplete="off" required value={password} onChange={(event) => setPassword(event.target.value)} />
        </div>
        <div><button className="btn primary" type="submit" disabled={upload.isPending || !file}>{upload.isPending ? 'Cargando…' : 'Cargar certificado'}</button></div>
      </form>
    </>
  )
}

function SolTab({ companyId }: { companyId: string }) {
  const { data, isPending, error } = useSolCredential(companyId)
  const save = useSetSol(companyId)
  const remove = useRemoveSol(companyId)
  const toast = useToast()
  const [user, setUser] = useState('')
  const [password, setPassword] = useState('')

  function submit(event: FormEvent) {
    event.preventDefault()
    save.mutate({ solUser: user, solPassword: password }, { onSuccess: () => { toast.ok('Credenciales SOL guardadas.'); setPassword('') } })
  }

  return (
    <>
      <div className="card">
        <h2>Credenciales SOL</h2>
        <p className="muted">Usuario secundario de Clave SOL con permiso para enviar comprobantes. Se usan para enviar a SUNAT.</p>
        <ErrorAlert error={error ?? remove.error} />
        {isPending ? <Loading /> : data ? (
          <div className="row spread">
            <KeyValues items={[['Usuario', data.solUser], ['Contraseña', data.hasPassword ? 'Guardada (no se muestra)' : 'No guardada'], ['Actualizadas', dateTime(data.updatedAt)]]} />
            <ConfirmButton label="Quitar" message="¿Quitar las credenciales SOL? No se podrá enviar a SUNAT." onConfirm={() => remove.mutate(undefined)} />
          </div>
        ) : <Empty>No hay credenciales SOL.</Empty>}
      </div>
      <form className="card stack" onSubmit={submit}>
        <h2>{data ? 'Reemplazar credenciales' : 'Guardar credenciales'}</h2>
        <ErrorAlert error={save.error} />
        <div className="form-grid">
          <TextField label="Usuario SOL" required autoComplete="off" value={user} onChange={(event) => setUser(event.target.value)} />
          <TextField label="Clave SOL" type="password" required autoComplete="off" value={password} onChange={(event) => setPassword(event.target.value)} />
        </div>
        <div><button className="btn primary" type="submit" disabled={save.isPending}>Guardar</button></div>
      </form>
    </>
  )
}
