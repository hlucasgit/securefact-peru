import { useCatalog, useProducts } from '../api/queries'
import type { DocumentLine, Product } from '../api/types'
import { copyTransport, emptyTransport, toRequestTransport, TransportEditor, type TransportState } from './TransportEditor'
import { SelectField, TextField } from './ui'

export interface LineState {
  key: number
  description: string
  unitCode: string
  productCode: string | null
  quantity: string
  unitValue: string
  affectation: string
  discount: string
  referenceValue: string
  isc: 'none' | 'AdValorem' | 'FixedAmount'
  iscValue: string
  bags: boolean
  /** Cargo transport data: sent only in a detraction 027. */
  transport: TransportState
}

let counter = 0

export function emptyLine(): LineState {
  return { key: ++counter, description: '', unitCode: 'NIU', productCode: null, quantity: '1', unitValue: '', affectation: '10', discount: '', referenceValue: '', isc: 'none', iscValue: '', bags: false, transport: emptyTransport() }
}

/** A line prefilled from a document line (to adjust it with a note). */
export function lineFrom(line: DocumentLine): LineState {
  return {
    ...emptyLine(),
    description: line.description,
    unitCode: line.unitCode,
    productCode: line.productCode,
    quantity: String(line.quantity),
    unitValue: String(line.unitValue),
    affectation: line.igvAffectationCode,
    isc: line.isc?.system ?? 'none',
    iscValue: line.isc ? String(line.isc.system === 'AdValorem' ? Math.round(line.isc.rateOrUnitAmount * 10000) / 100 : line.isc.rateOrUnitAmount) : '',
    bags: line.plasticBagCount > 0,
  }
}

const num = (text: string) => (text.trim() === '' ? 0 : Number(text))

/** The `tax` of a line request. Amounts go as typed: the API calculates every total and every tax. */
export function toRequestLine(line: LineState, isFree: boolean, withTransport = false) {
  const quantity = num(line.quantity)
  return {
    description: line.description.trim(),
    unitCode: line.unitCode.trim().toUpperCase(),
    productCode: line.productCode,
    ...(withTransport ? { transport: toRequestTransport(line.transport) } : {}),
    tax: {
      quantity,
      unitValue: isFree ? 0 : num(line.unitValue),
      igvAffectationCode: line.affectation,
      ...(num(line.discount) > 0 ? { discountAffectingBase: num(line.discount) } : {}),
      ...(isFree ? { referenceUnitValue: num(line.referenceValue) } : {}),
      ...(line.isc !== 'none' ? { isc: { system: line.isc, rateOrUnitAmount: line.isc === 'AdValorem' ? num(line.iscValue) / 100 : num(line.iscValue) } } : {}),
      plasticBagCount: line.bags ? quantity : 0,
    },
  }
}

interface Props {
  lines: LineState[]
  onChange: (lines: LineState[]) => void
  /** Every line has this affectation and it cannot be changed (the lines of an export). */
  fixedAffectation?: string
  /** Every line states the data of a cargo transport (detraction 027). */
  withTransport?: boolean
}

export function LinesEditor({ lines, onChange, fixedAffectation, withTransport = false }: Props) {
  const affectations = useCatalog('07')
  const products = useProducts('')
  const isFree = (code: string) => affectations.data?.find((entry) => entry.code === code)?.metadata['Codigo de tributo'] === '9996'
  const update = (key: number, patch: Partial<LineState>) => onChange(lines.map((line) => (line.key === key ? { ...line, ...patch } : line)))

  function pick(key: number, product: Product | undefined) {
    if (!product) return
    update(key, { description: product.description, unitCode: product.unitCode, productCode: product.internalCode, unitValue: String(product.unitValue), affectation: product.igvAffectationCode })
  }

  return (
    <div className="lines">
      {lines.map((line, index) => (
        <fieldset key={line.key} className="line-card" style={{ border: undefined }}>
          <legend className="row spread" style={{ width: '100%', padding: 0 }}>
            <strong>Ítem {index + 1}</strong>
            {lines.length > 1 && (
              <button className="btn small ghost" type="button" onClick={() => onChange(lines.filter((item) => item.key !== line.key))}>
                Quitar
              </button>
            )}
          </legend>
          <div className="stack">
            {(products.data?.length ?? 0) > 0 && (
              <SelectField label="Producto del catálogo" hint="(opcional: completa los datos)" value="" onChange={(event) => pick(line.key, products.data?.find((product) => product.id === event.target.value))}>
                <option value="">Seleccionar…</option>
                {products.data?.filter((product) => product.isActive).map((product) => (
                  <option key={product.id} value={product.id}>
                    {product.internalCode} · {product.description}
                  </option>
                ))}
              </SelectField>
            )}
            <TextField label="Descripción" required maxLength={500} value={line.description} onChange={(event) => update(line.key, { description: event.target.value })} />
            <div className="form-grid">
              <TextField label="Cantidad" type="number" min="0" step="any" required value={line.quantity} onChange={(event) => update(line.key, { quantity: event.target.value })} />
              <TextField label="Unidad" hint="NIU, ZZ, KGM…" required maxLength={3} value={line.unitCode} onChange={(event) => update(line.key, { unitCode: event.target.value })} />
              {isFree(fixedAffectation ?? line.affectation) ? (
                <TextField label="Valor referencial unitario" type="number" min="0" step="any" required value={line.referenceValue} onChange={(event) => update(line.key, { referenceValue: event.target.value })} />
              ) : (
                <TextField label="Valor unitario" hint="sin impuestos" type="number" min="0" step="any" required value={line.unitValue} onChange={(event) => update(line.key, { unitValue: event.target.value })} />
              )}
              <SelectField label="Afectación al IGV" value={fixedAffectation ?? line.affectation} disabled={fixedAffectation !== undefined} onChange={(event) => update(line.key, { affectation: event.target.value })}>
                {(affectations.data ?? []).map((entry) => (
                  <option key={entry.code} value={entry.code}>
                    {entry.code} · {entry.description}
                  </option>
                ))}
              </SelectField>
              <TextField label="Descuento" hint="reduce la base" type="number" min="0" step="any" value={line.discount} onChange={(event) => update(line.key, { discount: event.target.value })} />
            </div>
            <div className="form-grid">
              <SelectField label="ISC" value={line.isc} onChange={(event) => update(line.key, { isc: event.target.value as LineState['isc'] })}>
                <option value="none">Sin ISC</option>
                <option value="AdValorem">Al valor (%)</option>
                <option value="FixedAmount">Monto fijo por unidad</option>
              </SelectField>
              {line.isc !== 'none' && (
                <TextField label={line.isc === 'AdValorem' ? 'Tasa del ISC (%)' : 'ISC por unidad'} type="number" min="0" step="any" required value={line.iscValue} onChange={(event) => update(line.key, { iscValue: event.target.value })} />
              )}
              <label className="checkbox" style={{ alignSelf: 'end' }}>
                <input type="checkbox" checked={line.bags} onChange={(event) => update(line.key, { bags: event.target.checked })} />
                <span>Bolsas de plástico (ICBPER): una por unidad</span>
              </label>
            </div>
            {withTransport && <TransportEditor transport={line.transport} onChange={(transport) => update(line.key, { transport })} />}
          </div>
        </fieldset>
      ))}
      <div className="actions">
        <button className="btn" type="button" onClick={() => onChange([...lines, emptyLine()])}>
          Agregar ítem
        </button>
        {withTransport && lines.length > 1 && (
          <button className="btn" type="button" onClick={() => onChange(lines.map((line, index) => (index === 0 ? line : { ...line, transport: copyTransport(lines[0].transport) })))}>
            Usar el transporte del ítem 1 en todos
          </button>
        )}
      </div>
    </div>
  )
}

export function lineIsFree(affectation: string, affectations: { code: string; metadata: Record<string, string> }[] | undefined): boolean {
  return affectations?.find((entry) => entry.code === affectation)?.metadata['Codigo de tributo'] === '9996'
}
