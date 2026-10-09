import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { useCatalog, useCompanies, useCreateGuide, useGreSeries } from '../api/queries'
import { ErrorAlert, Loading, PageHeader, SelectField, TextAreaField, TextField, useToast } from '../components/ui'
import { todayInLima } from '../lib/format'
import {
  buildGuide,
  emptyContainer,
  emptyGood,
  emptyGuide,
  emptyRelated,
  isCustomsMotive,
  needsBuyer,
  needsSupplier,
  relatedTypesFor,
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
  const portCatalog = useCatalog('63')
  const airportCatalog = useCatalog('64')
  const [form, setForm] = useState<GuideForm>(() => emptyGuide(todayInLima()))
  const active = companies.data?.filter((company) => company.status === 'Active') ?? []
  const companyId = form.companyId || active[0]?.id || ''
  const series = useGreSeries(companyId || null)
  const activeSeries = series.data?.filter((item) => item.isActive && item.documentTypeCode === '09') ?? []
  const seriesId = activeSeries.some((item) => item.id === form.seriesId) ? form.seriesId : (activeSeries[0]?.id ?? '')

  if (companies.isPending) return <Loading />
  const identityTypes = identityCatalog.data ?? []
  const applicable = (relatedCatalog.data ?? []).filter((entry) => (entry.metadata['GRE Aplicable'] ?? '').toLowerCase().includes('remitente'))
  const allowed = relatedTypesFor(form.motiveCode, applicable.map((entry) => entry.code))
  const relatedTypes = applicable.filter((entry) => allowed.includes(entry.code))
  const isPrivate = form.modalityCode === '02'
  const motive = form.motiveCode
  const customs = isCustomsMotive(motive)
  const chosen = form.related.map((item) => item.typeCode)
  const has92 = customs && chosen.includes('92')
  const has91 = customs && chosen.includes('91')
  const ports = (form.portType === '2' ? airportCatalog.data : portCatalog.data) ?? []
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
            <TextField label="Peso bruto total" required={!has92} hint={has92 ? 'Con la orden de entrega del terminal portuario no hay peso' : undefined} inputMode="decimal" value={form.grossWeight} onChange={(event) => set('grossWeight', event.target.value)} />
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
          <AddressFields title="Punto de partida" value={form.origin} onChange={(value) => set('origin', value)} annex={motive === '04' || motive === '08'} />
          {motive === '18' ? (
            <p className="muted">El emisor itinerante no informa el punto de llegada: no lo conoce al salir.</p>
          ) : (
            <AddressFields title="Punto de llegada" value={form.destination} onChange={(value) => set('destination', value)} annex={motive === '04' || motive === '09' || motive === '19'} />
          )}
        </div>

        {customs && (
          <div className="card stack">
            <h2>Aduanas</h2>
            <p className="muted">Declaración (DAM o DS), puerto o aeropuerto, peso neto y contenedores de la importación, la exportación o la mercancía extranjera. Los documentos de aduanas se relacionan más abajo.</p>
            <div className="form-grid">
              <SelectField label="Tipo de puerto o aeropuerto" value={form.portType} onChange={(event) => setForm((current) => ({ ...current, portType: event.target.value, portCode: '', portName: '' }))}>
                <option value="">Ninguno</option>
                <option value="1">Puerto</option>
                <option value="2">Aeropuerto</option>
              </SelectField>
              {form.portType !== '' && (
                <SelectField
                  label="Puerto o aeropuerto"
                  value={form.portCode}
                  onChange={(event) => setForm((current) => ({ ...current, portCode: event.target.value, portName: ports.find((entry) => entry.code === event.target.value)?.description ?? '' }))}
                >
                  <option value="">Seleccione</option>
                  {ports.map((entry) => <option key={entry.code} value={entry.code}>{entry.code} · {entry.description}</option>)}
                </SelectField>
              )}
              {!has92 && <TextField label="Peso neto (KGM)" inputMode="decimal" value={form.netWeight} onChange={(event) => set('netWeight', event.target.value)} />}
            </div>
            {!has92 && <TextField label="Sustento de la diferencia de peso" maxLength={250} value={form.weightNote} onChange={(event) => set('weightNote', event.target.value)} />}
            {!has91 && !has92 && <label className="row"><input type="checkbox" checked={form.wholeTransfer} onChange={(event) => set('wholeTransfer', event.target.checked)} /> Traslado total de la declaración (DAM o DS)</label>}
            {has91 && <label className="row"><input type="checkbox" checked={form.manifestContainers} onChange={(event) => set('manifestContainers', event.target.checked)} /> Traslado en contenedores del manifiesto de carga</label>}
            {!has92 && form.containers.map((container, index) => (
              <div className="form-grid" key={container.key}>
                <TextField label={`Número del contenedor ${index + 1}`} maxLength={17} value={container.number} onChange={(event) => set('containers', form.containers.map((item) => (item.key === container.key ? { ...item, number: event.target.value } : item)))} />
                <TextField label={`Precinto del contenedor ${index + 1}`} maxLength={100} value={container.seal} onChange={(event) => set('containers', form.containers.map((item) => (item.key === container.key ? { ...item, seal: event.target.value } : item)))} />
                <div><button className="btn small danger" type="button" onClick={() => set('containers', form.containers.filter((item) => item.key !== container.key))}>Quitar contenedor {index + 1}</button></div>
              </div>
            ))}
            {!has92 && form.containers.length < 2 && <div><button className="btn small" type="button" onClick={() => set('containers', [...form.containers, emptyContainer()])}>Agregar contenedor</button></div>}
          </div>
        )}

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

        {!has92 && (
        <div className="card stack">
          <h2>Bienes</h2>
          {customs && <p className="muted">Con el traslado total de la declaración los bienes pueden quedar sin listar.</p>}
          {form.goods.map((good, index) => (
            <div className="form-grid" key={good.key}>
              <TextField label={`Descripción del bien ${index + 1}`} required={!customs} value={good.description} onChange={(event) => set('goods', form.goods.map((item) => (item.key === good.key ? { ...item, description: event.target.value } : item)))} />
              <TextField label="Unidad" required maxLength={3} hint="Código de unidad (NIU, KGM, ZZ…)" value={good.unitCode} onChange={(event) => set('goods', form.goods.map((item) => (item.key === good.key ? { ...item, unitCode: event.target.value } : item)))} />
              <TextField label="Cantidad" required inputMode="decimal" value={good.quantity} onChange={(event) => set('goods', form.goods.map((item) => (item.key === good.key ? { ...item, quantity: event.target.value } : item)))} />
              <TextField label="Código del bien" value={good.code} onChange={(event) => set('goods', form.goods.map((item) => (item.key === good.key ? { ...item, code: event.target.value } : item)))} />
              {motive === '09' && (
                <>
                  <TextField label="Numeración de la declaración" hint="Por ejemplo 118-2026-40-654321" value={good.declarationNumber} onChange={(event) => set('goods', form.goods.map((item) => (item.key === good.key ? { ...item, declarationNumber: event.target.value } : item)))} />
                  <TextField label="Serie del bien en la declaración" inputMode="numeric" maxLength={4} value={good.declarationSeries} onChange={(event) => set('goods', form.goods.map((item) => (item.key === good.key ? { ...item, declarationSeries: event.target.value } : item)))} />
                </>
              )}
              {has91 && (
                <>
                  <TextField label="Documento de transporte" maxLength={25} value={good.transportDocument} onChange={(event) => set('goods', form.goods.map((item) => (item.key === good.key ? { ...item, transportDocument: event.target.value } : item)))} />
                  <TextField label="Número de detalle" inputMode="numeric" maxLength={5} value={good.transportDetail} onChange={(event) => set('goods', form.goods.map((item) => (item.key === good.key ? { ...item, transportDetail: event.target.value } : item)))} />
                  {form.manifestContainers && (
                    <>
                      <TextField label="Contenedor de la línea" maxLength={17} value={good.manifestContainer} onChange={(event) => set('goods', form.goods.map((item) => (item.key === good.key ? { ...item, manifestContainer: event.target.value } : item)))} />
                      <TextField label="Precinto de la línea" maxLength={100} value={good.seal} onChange={(event) => set('goods', form.goods.map((item) => (item.key === good.key ? { ...item, seal: event.target.value } : item)))} />
                      <SelectField label="Contenedor vacío" value={good.emptyContainer} onChange={(event) => set('goods', form.goods.map((item) => (item.key === good.key ? { ...item, emptyContainer: event.target.value } : item)))}>
                        <option value="">Seleccione</option>
                        <option value="0">No, lleva carga</option>
                        <option value="1">Sí, vacío</option>
                      </SelectField>
                    </>
                  )}
                </>
              )}
              {(form.goods.length > 1 || customs) && <div><button className="btn small danger" type="button" onClick={() => set('goods', form.goods.filter((item) => item.key !== good.key))}>Quitar bien {index + 1}</button></div>}
            </div>
          ))}
          <div><button className="btn small" type="button" onClick={() => set('goods', [...form.goods, emptyGood()])}>Agregar bien</button></div>
        </div>
        )}

        <div className="card stack">
          <h2>Documentos relacionados</h2>
          <p className="muted">
            {customs ? 'La importación y la exportación relacionan la declaración (50 DAM o 52 DS); la mercancía extranjera, la declaración, el manifiesto de carga (91) o la orden de entrega del terminal portuario (92).' : 'Por ejemplo la factura o boleta de la venta. Son opcionales según el motivo.'}
          </p>
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
          <div><button className="btn small" type="button" onClick={() => set('related', [...form.related, { ...emptyRelated(), typeCode: relatedTypes[0]?.code ?? '01' }])}>Agregar documento relacionado</button></div>
        </div>

        <div><button className="btn primary" type="submit" disabled={create.isPending || !seriesId}>{create.isPending ? 'Preparando…' : 'Preparar guía'}</button></div>
      </form>
    </>
  )
}
