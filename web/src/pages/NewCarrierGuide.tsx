import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { useCatalog, useCompanies, useCreateCarrierGuide, useGreSeries } from '../api/queries'
import type { GreFreightPayer } from '../api/types'
import { ErrorAlert, Loading, PageHeader, SelectField, TextAreaField, TextField, useToast } from '../components/ui'
import { todayInLima } from '../lib/format'
import { buildCarrierGuide, emptyCarrierGuide, emptyDriver, emptyVehicle, type CarrierGuideForm, type DriverState, type VehicleState } from '../lib/carrierGuide'
import { emptyGood, emptyRelated } from '../lib/guide'
import { AddressFields, PartyFields } from './NewGuide'

type IdentityType = { code: string; description: string }

function VehicleFields({ title, value, onChange, onRemove }: { title: string; value: VehicleState; onChange: (value: VehicleState) => void; onRemove?: () => void }) {
  return (
    <div className="form-grid">
      <TextField label={`Placa del ${title}`} maxLength={8} value={value.plate} onChange={(event) => onChange({ ...value, plate: event.target.value })} />
      <TextField label={`Tarjeta de circulación del ${title}`} maxLength={15} value={value.circulationCard} onChange={(event) => onChange({ ...value, circulationCard: event.target.value })} />
      {onRemove && <div><button className="btn small danger" type="button" onClick={onRemove}>Quitar {title}</button></div>}
    </div>
  )
}

function DriverFields({ title, value, onChange, onRemove, identityTypes }: { title: string; value: DriverState; onChange: (value: DriverState) => void; onRemove?: () => void; identityTypes: IdentityType[] }) {
  return (
    <div className="form-grid">
      <SelectField label={`Tipo de documento del ${title}`} value={value.documentTypeCode} onChange={(event) => onChange({ ...value, documentTypeCode: event.target.value })}>
        {identityTypes.map((type) => <option key={type.code} value={type.code}>{type.code} · {type.description}</option>)}
      </SelectField>
      <TextField label={`Número de documento del ${title}`} value={value.documentNumber} onChange={(event) => onChange({ ...value, documentNumber: event.target.value })} />
      <TextField label={`Nombres del ${title}`} value={value.firstNames} onChange={(event) => onChange({ ...value, firstNames: event.target.value })} />
      <TextField label={`Apellidos del ${title}`} value={value.lastNames} onChange={(event) => onChange({ ...value, lastNames: event.target.value })} />
      <TextField label={`Licencia del ${title}`} maxLength={10} value={value.licenseNumber} onChange={(event) => onChange({ ...value, licenseNumber: event.target.value })} />
      {onRemove && <div><button className="btn small danger" type="button" onClick={onRemove}>Quitar {title}</button></div>}
    </div>
  )
}

export function NewCarrierGuide() {
  const navigate = useNavigate()
  const toast = useToast()
  const companies = useCompanies()
  const create = useCreateCarrierGuide()
  const identityCatalog = useCatalog('06')
  const relatedCatalog = useCatalog('61')
  const [form, setForm] = useState<CarrierGuideForm>(() => emptyCarrierGuide(todayInLima()))
  const active = companies.data?.filter((company) => company.status === 'Active') ?? []
  const companyId = form.companyId || active[0]?.id || ''
  const series = useGreSeries(companyId || null)
  const activeSeries = series.data?.filter((item) => item.isActive && item.documentTypeCode === '31') ?? []
  const seriesId = activeSeries.some((item) => item.id === form.seriesId) ? form.seriesId : (activeSeries[0]?.id ?? '')

  if (companies.isPending) return <Loading />
  const identityTypes = identityCatalog.data ?? []
  const relatedTypes = (relatedCatalog.data ?? []).filter((entry) => (entry.metadata['GRE Aplicable'] ?? '').toLowerCase().includes('transportista') && ['01', '03', '04', '12', '48'].includes(entry.code))
  const set = <K extends keyof CarrierGuideForm>(key: K, value: CarrierGuideForm[K]) => setForm((current) => ({ ...current, [key]: value }))
  const listedBySenderGuide = form.senderGuide.trim() !== ''
  const addressRequired = !listedBySenderGuide && !form.plannedTransshipment

  function submit(event: FormEvent) {
    event.preventDefault()
    create.mutate(buildCarrierGuide({ ...form, companyId, seriesId }), {
      onSuccess: (guide) => {
        toast.ok(`Guía ${guide.name} preparada.`)
        void navigate(`/guias/${guide.id}`)
      },
    })
  }

  return (
    <>
      <PageHeader title="Emitir guía de remisión del transportista" subtitle="Guía de remisión transportista (31). Se numera, se firma y queda preparada; luego se envía a SUNAT." />
      <form className="stack" onSubmit={submit}>
        <ErrorAlert error={create.error} />
        <div className="card stack">
          <h2>Datos del traslado</h2>
          <div className="form-grid">
            <SelectField label="Empresa (transportista)" value={companyId} onChange={(event) => set('companyId', event.target.value)}>
              {active.map((company) => <option key={company.id} value={company.id}>{company.ruc} · {company.legalName}</option>)}
            </SelectField>
            <SelectField label="Serie" value={seriesId} hint={activeSeries.length === 0 ? 'La empresa no tiene series de guía del transportista: cree una «V…» en Empresas, Guías de remisión.' : undefined} onChange={(event) => set('seriesId', event.target.value)}>
              {activeSeries.map((item) => <option key={item.id} value={item.id}>{item.code}</option>)}
            </SelectField>
            <TextField label="Fecha de emisión" type="date" required max={todayInLima()} value={form.issueDate} onChange={(event) => set('issueDate', event.target.value)} />
            <TextField label="Inicio del traslado" type="date" required value={form.startDate} onChange={(event) => set('startDate', event.target.value)} />
            <TextField label="Peso bruto total" required inputMode="decimal" value={form.grossWeight} onChange={(event) => set('grossWeight', event.target.value)} />
            <SelectField label="Unidad del peso" value={form.weightUnit} onChange={(event) => set('weightUnit', event.target.value)}>
              <option value="KGM">KGM · Kilogramos</option>
              <option value="TNE">TNE · Toneladas</option>
            </SelectField>
            <TextField label="Número de bultos" inputMode="numeric" value={form.packageCount} onChange={(event) => set('packageCount', event.target.value)} />
            <TextField label="Registro MTC del transportista" maxLength={20} value={form.mtcRegistration} onChange={(event) => set('mtcRegistration', event.target.value)} />
          </div>
          <TextAreaField label="Observaciones" maxLength={250} value={form.note} onChange={(event) => set('note', event.target.value)} />
        </div>

        <div className="card stack">
          <h2>Partes y puntos</h2>
          <PartyFields title="Remitente (quien envía los bienes)" value={form.sender} onChange={(value) => set('sender', value)} identityTypes={identityTypes} />
          <PartyFields title="Destinatario" value={form.recipient} onChange={(value) => set('recipient', value)} identityTypes={identityTypes} />
          <TextField
            label="Guía de remisión del remitente"
            hint="Por ejemplo T001-45. Si la indica, esa guía lista los bienes y las direcciones y no se piden aquí."
            value={form.senderGuide}
            onChange={(event) => set('senderGuide', event.target.value)}
          />
          <AddressFields title="Punto de partida" value={form.origin} onChange={(value) => set('origin', value)} annex={false} addressRequired={addressRequired} />
          <AddressFields title="Punto de llegada" value={form.destination} onChange={(value) => set('destination', value)} annex={false} addressRequired={addressRequired} />
        </div>

        <div className="card stack">
          <h2>Vehículos y conductores</h2>
          <VehicleFields title="vehículo principal" value={form.vehicle} onChange={(value) => set('vehicle', value)} />
          {form.secondaryVehicles.map((vehicle, index) => (
            <VehicleFields
              key={vehicle.key}
              title={`vehículo secundario ${index + 1}`}
              value={vehicle}
              onChange={(value) => set('secondaryVehicles', form.secondaryVehicles.map((item) => (item.key === vehicle.key ? value : item)))}
              onRemove={() => set('secondaryVehicles', form.secondaryVehicles.filter((item) => item.key !== vehicle.key))}
            />
          ))}
          {form.secondaryVehicles.length < 2 && <div><button className="btn small" type="button" onClick={() => set('secondaryVehicles', [...form.secondaryVehicles, emptyVehicle()])}>Agregar vehículo secundario</button></div>}
          <DriverFields title="conductor principal" value={form.driver} onChange={(value) => set('driver', value)} identityTypes={identityTypes} />
          {form.secondaryDrivers.map((driver, index) => (
            <DriverFields
              key={driver.key}
              title={`conductor secundario ${index + 1}`}
              value={driver}
              identityTypes={identityTypes}
              onChange={(value) => set('secondaryDrivers', form.secondaryDrivers.map((item) => (item.key === driver.key ? value : item)))}
              onRemove={() => set('secondaryDrivers', form.secondaryDrivers.filter((item) => item.key !== driver.key))}
            />
          ))}
          {form.secondaryDrivers.length < 2 && <div><button className="btn small" type="button" onClick={() => set('secondaryDrivers', [...form.secondaryDrivers, emptyDriver()])}>Agregar conductor secundario</button></div>}
        </div>

        {!listedBySenderGuide && (
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
        )}

        <div className="card stack">
          <h2>Documentos relacionados</h2>
          <p className="muted">Sin guía del remitente solo se admite un documento relacionado (por ejemplo la factura de la venta).</p>
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

        <div className="card stack">
          <h2>Flete e indicadores</h2>
          <div className="form-grid">
            <SelectField label="Quién paga el flete" value={form.freightPayer} onChange={(event) => set('freightPayer', event.target.value as GreFreightPayer)}>
              <option value="Sender">El remitente</option>
              <option value="Subcontractor">El subcontratador</option>
              <option value="ThirdParty">Un tercero</option>
            </SelectField>
          </div>
          {form.freightPayer === 'ThirdParty' && <PartyFields title="Tercero que paga el flete" value={form.thirdPartyPayer} onChange={(value) => set('thirdPartyPayer', value)} identityTypes={identityTypes} />}
          <label className="row"><input type="checkbox" checked={form.subcontracted} onChange={(event) => set('subcontracted', event.target.checked)} /> Transporte subcontratado</label>
          {form.subcontracted && (
            <div className="form-grid">
              <TextField label="RUC del subcontratador" pattern="\d{11}" maxLength={11} value={form.subcontractor.documentNumber} onChange={(event) => set('subcontractor', { ...form.subcontractor, documentTypeCode: '6', documentNumber: event.target.value })} />
              <TextField label="Razón social del subcontratador" value={form.subcontractor.name} onChange={(event) => set('subcontractor', { ...form.subcontractor, name: event.target.value })} />
            </div>
          )}
          <label className="row"><input type="checkbox" checked={form.plannedTransshipment} onChange={(event) => set('plannedTransshipment', event.target.checked)} /> Transbordo programado</label>
          <label className="row"><input type="checkbox" checked={form.returnWithEmptyPackaging} onChange={(event) => set('returnWithEmptyPackaging', event.target.checked)} /> Retorno del vehículo con envases o embalajes vacíos</label>
          <label className="row"><input type="checkbox" checked={form.returnEmptyVehicle} onChange={(event) => set('returnEmptyVehicle', event.target.checked)} /> Retorno del vehículo vacío</label>
        </div>

        <div><button className="btn primary" type="submit" disabled={create.isPending || !seriesId}>{create.isPending ? 'Preparando…' : 'Preparar guía'}</button></div>
      </form>
    </>
  )
}
