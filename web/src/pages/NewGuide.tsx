import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { useCatalog, useCompanies, useCreateGuide, useGreSeries } from '../api/queries'
import { ErrorAlert, Loading, PageHeader, SelectField, TextAreaField, TextField, useToast } from '../components/ui'
import { todayInLima } from '../lib/format'
import {
  buildGuide,
  emptyGood,
  emptyGuide,
  emptyRelated,
  needsBuyer,
  needsSupplier,
  type AddressState,
  type GuideForm,
  type PartyState,
} from '../lib/guide'
import { GRE_MOTIVES } from './GuideDetail'

export function PartyFields({ title, value, onChange, identityTypes }: { title: string; value: PartyState; onChange: (value: PartyState) => void; identityTypes: { code: string; description: string }[] }) {
  return (
    <>
      <h3>{title}</h3>
      <div className="form-grid">
        <SelectField label="Tipo de documento" value={value.documentTypeCode} onChange={(event) => onChange({ ...value, documentTypeCode: event.target.value })}>
          {identityTypes.map((type) => <option key={type.code} value={type.code}>{type.code} · {type.description}</option>)}
        </SelectField>
        <TextField label="Número de documento" value={value.documentNumber} onChange={(event) => onChange({ ...value, documentNumber: event.target.value })} />
        <TextField label="Nombre o razón social" value={value.name} onChange={(event) => onChange({ ...value, name: event.target.value })} />
      </div>
    </>
  )
}

export function AddressFields({ title, value, onChange, annex, addressRequired = true }: { title: string; value: AddressState; onChange: (value: AddressState) => void; annex: boolean; addressRequired?: boolean }) {
  return (
    <>
      <h3>{title}</h3>
      <div className="form-grid">
        <TextField label="Ubigeo" required pattern="\d{6}" maxLength={6} value={value.ubigeoCode} onChange={(event) => onChange({ ...value, ubigeoCode: event.target.value })} />
        <TextField label="Dirección" required={addressRequired} value={value.address} onChange={(event) => onChange({ ...value, address: event.target.value })} />
        {annex && (
          <>
            <TextField label="RUC del establecimiento" hint="Del titular, si es un establecimiento anexo" pattern="\d{11}" maxLength={11} value={value.establishmentRuc} onChange={(event) => onChange({ ...value, establishmentRuc: event.target.value })} />
            <TextField label="Código del establecimiento anexo" maxLength={4} value={value.establishmentCode} onChange={(event) => onChange({ ...value, establishmentCode: event.target.value })} />
          </>
        )}
      </div>
    </>
  )
}

export function NewGuide() {
  const navigate = useNavigate()
  const toast = useToast()
  const companies = useCompanies()
  const create = useCreateGuide()
  const identityCatalog = useCatalog('06')
  const relatedCatalog = useCatalog('61')
  const [form, setForm] = useState<GuideForm>(() => emptyGuide(todayInLima()))
  const active = companies.data?.filter((company) => company.status === 'Active') ?? []
  const companyId = form.companyId || active[0]?.id || ''
  const series = useGreSeries(companyId || null)
  const activeSeries = series.data?.filter((item) => item.isActive && item.documentTypeCode === '09') ?? []
  const seriesId = activeSeries.some((item) => item.id === form.seriesId) ? form.seriesId : (activeSeries[0]?.id ?? '')

  if (companies.isPending) return <Loading />
  const identityTypes = identityCatalog.data ?? []
  const relatedTypes = (relatedCatalog.data ?? []).filter((entry) => (entry.metadata['GRE Aplicable'] ?? '').toLowerCase().includes('remitente'))
  const isPrivate = form.modalityCode === '02'
  const set = <K extends keyof GuideForm>(key: K, value: GuideForm[K]) => setForm((current) => ({ ...current, [key]: value }))

  function submit(event: FormEvent) {
    event.preventDefault()
    create.mutate(buildGuide({ ...form, companyId, seriesId }), {
      onSuccess: (guide) => {
        toast.ok(`Guía ${guide.name} preparada.`)
        void navigate(`/guias/${guide.id}`)
      },
    })
  }

  return (
    <>
      <PageHeader title="Emitir guía de remisión" subtitle="Guía de remisión remitente (09). Se numera, se firma y queda preparada; luego se envía a SUNAT." />
      <form className="stack" onSubmit={submit}>
        <ErrorAlert error={create.error} />
        <div className="card stack">
          <h2>Datos del traslado</h2>
          <div className="form-grid">
            <SelectField label="Empresa" value={companyId} onChange={(event) => set('companyId', event.target.value)}>
              {active.map((company) => <option key={company.id} value={company.id}>{company.ruc} · {company.legalName}</option>)}
            </SelectField>
            <SelectField label="Serie" value={seriesId} hint={activeSeries.length === 0 ? 'La empresa no tiene series de guía: créelas en Empresas, Series.' : undefined} onChange={(event) => set('seriesId', event.target.value)}>
              {activeSeries.map((item) => <option key={item.id} value={item.id}>{item.code}</option>)}
            </SelectField>
            <TextField label="Fecha de emisión" type="date" required max={todayInLima()} value={form.issueDate} onChange={(event) => set('issueDate', event.target.value)} />
            <SelectField label="Motivo del traslado" value={form.motiveCode} onChange={(event) => set('motiveCode', event.target.value)}>
              {Object.entries(GRE_MOTIVES).map(([code, label]) => <option key={code} value={code}>{code} · {label}</option>)}
            </SelectField>
            {form.motiveCode === '13' && <TextField label="Descripción del motivo" required maxLength={100} value={form.motiveDescription} onChange={(event) => set('motiveDescription', event.target.value)} />}
            <SelectField label="Modalidad de transporte" value={form.modalityCode} onChange={(event) => set('modalityCode', event.target.value)}>
              <option value="02">02 · Transporte privado (vehículo propio)</option>
              <option value="01">01 · Transporte público (transportista)</option>
            </SelectField>
            <TextField label={isPrivate ? 'Inicio del traslado' : 'Entrega al transportista'} type="date" required value={form.startDate} onChange={(event) => set('startDate', event.target.value)} />
            <TextField label="Peso bruto total" required inputMode="decimal" value={form.grossWeight} onChange={(event) => set('grossWeight', event.target.value)} />
            <SelectField label="Unidad del peso" value={form.weightUnit} onChange={(event) => set('weightUnit', event.target.value)}>
              <option value="KGM">KGM · Kilogramos</option>
              <option value="TNE">TNE · Toneladas</option>
            </SelectField>
            <TextField label="Número de bultos" inputMode="numeric" value={form.packageCount} onChange={(event) => set('packageCount', event.target.value)} />
          </div>
          <TextAreaField label="Observaciones" maxLength={250} value={form.note} onChange={(event) => set('note', event.target.value)} />
        </div>

        <div className="card stack">
          <h2>Partes</h2>
          <PartyFields title="Destinatario" value={form.recipient} onChange={(value) => set('recipient', value)} identityTypes={identityTypes} />
          {needsSupplier(form.motiveCode) && <PartyFields title="Proveedor" value={form.supplier} onChange={(value) => set('supplier', value)} identityTypes={identityTypes} />}
          {needsBuyer(form.motiveCode) && <PartyFields title="Comprador" value={form.buyer} onChange={(value) => set('buyer', value)} identityTypes={identityTypes} />}
          <AddressFields title="Punto de partida" value={form.origin} onChange={(value) => set('origin', value)} annex={form.motiveCode === '04'} />
          <AddressFields title="Punto de llegada" value={form.destination} onChange={(value) => set('destination', value)} annex={form.motiveCode === '04'} />
        </div>

        <div className="card stack">
          <h2>Transporte</h2>
          {isPrivate ? (
            <>
              <div className="form-grid">
                <TextField label="Placa del vehículo" required maxLength={8} value={form.plate} onChange={(event) => set('plate', event.target.value)} />
                <TextField label="Tarjeta de circulación o certificado de habilitación" maxLength={15} value={form.circulationCard} onChange={(event) => set('circulationCard', event.target.value)} />
              </div>
              <h3>Conductor</h3>
              <div className="form-grid">
                <SelectField label="Tipo de documento" value={form.driver.documentTypeCode} onChange={(event) => set('driver', { ...form.driver, documentTypeCode: event.target.value })}>
                  {identityTypes.map((type) => <option key={type.code} value={type.code}>{type.code} · {type.description}</option>)}
                </SelectField>
                <TextField label="Número de documento" required value={form.driver.documentNumber} onChange={(event) => set('driver', { ...form.driver, documentNumber: event.target.value })} />
                <TextField label="Nombres" required value={form.driver.firstNames} onChange={(event) => set('driver', { ...form.driver, firstNames: event.target.value })} />
                <TextField label="Apellidos" required value={form.driver.lastNames} onChange={(event) => set('driver', { ...form.driver, lastNames: event.target.value })} />
                <TextField label="Licencia de conducir" required maxLength={10} value={form.driver.licenseNumber} onChange={(event) => set('driver', { ...form.driver, licenseNumber: event.target.value })} />
              </div>
            </>
          ) : (
            <div className="form-grid">
              <TextField label="RUC del transportista" required pattern="\d{11}" maxLength={11} value={form.carrierRuc} onChange={(event) => set('carrierRuc', event.target.value)} />
              <TextField label="Razón social del transportista" required value={form.carrierName} onChange={(event) => set('carrierName', event.target.value)} />
              <TextField label="Registro MTC" maxLength={20} value={form.carrierMtc} onChange={(event) => set('carrierMtc', event.target.value)} />
            </div>
          )}
        </div>

        <div className="card stack">
          <h2>Bienes</h2>
          {form.goods.map((good, index) => (
            <div className="form-grid" key={good.key}>
              <TextField label={`Descripción del bien ${index + 1}`} required value={good.description} onChange={(event) => set('goods', form.goods.map((item) => (item.key === good.key ? { ...item, description: event.target.value } : item)))} />
              <TextField label="Unidad" required maxLength={3} hint="Código de unidad (NIU, KGM, ZZ…)" value={good.unitCode} onChange={(event) => set('goods', form.goods.map((item) => (item.key === good.key ? { ...item, unitCode: event.target.value } : item)))} />
              <TextField label="Cantidad" required inputMode="decimal" value={good.quantity} onChange={(event) => set('goods', form.goods.map((item) => (item.key === good.key ? { ...item, quantity: event.target.value } : item)))} />
              <TextField label="Código del bien" value={good.code} onChange={(event) => set('goods', form.goods.map((item) => (item.key === good.key ? { ...item, code: event.target.value } : item)))} />
              {form.goods.length > 1 && <div><button className="btn small danger" type="button" onClick={() => set('goods', form.goods.filter((item) => item.key !== good.key))}>Quitar bien {index + 1}</button></div>}
            </div>
          ))}
          <div><button className="btn small" type="button" onClick={() => set('goods', [...form.goods, emptyGood()])}>Agregar bien</button></div>
        </div>

        <div className="card stack">
          <h2>Documentos relacionados</h2>
          <p className="muted">Por ejemplo la factura o boleta de la venta. Son opcionales según el motivo.</p>
          {form.related.map((item, index) => (
            <div className="form-grid" key={item.key}>
              <SelectField label={`Tipo del documento ${index + 1}`} value={item.typeCode} onChange={(event) => set('related', form.related.map((other) => (other.key === item.key ? { ...other, typeCode: event.target.value } : other)))}>
                {relatedTypes.map((type) => <option key={type.code} value={type.code}>{type.code} · {type.description}</option>)}
              </SelectField>
              <TextField label={`Número del documento relacionado ${index + 1}`} hint="Por ejemplo F001-123" value={item.number} onChange={(event) => set('related', form.related.map((other) => (other.key === item.key ? { ...other, number: event.target.value } : other)))} />
              <TextField label="RUC del emisor" pattern="\d{11}" maxLength={11} value={item.issuerRuc} onChange={(event) => set('related', form.related.map((other) => (other.key === item.key ? { ...other, issuerRuc: event.target.value } : other)))} />
              <div><button className="btn small danger" type="button" onClick={() => set('related', form.related.filter((other) => other.key !== item.key))}>Quitar documento {index + 1}</button></div>
            </div>
          ))}
          <div><button className="btn small" type="button" onClick={() => set('related', [...form.related, emptyRelated()])}>Agregar documento relacionado</button></div>
        </div>

        <div><button className="btn primary" type="submit" disabled={create.isPending || !seriesId}>{create.isPending ? 'Preparando…' : 'Preparar guía'}</button></div>
      </form>
    </>
  )
}
