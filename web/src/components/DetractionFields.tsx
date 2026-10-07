import { useCatalog } from '../api/queries'
import { SelectField, TextField } from './ui'
import type { DeductionInput } from '../lib/operations'

interface Props {
  value: DeductionInput
  onChange: (value: DeductionInput) => void
  /** The account that the company has registered: the field may stay empty when there is one. */
  companyAccount: string | null
  /** Catalogue 54 codes the form does not offer. */
  excluded?: string[]
}

/** The data of a detraction that the issuer states: the good or service (catalogue 54), the percentage, the amount in soles and the account at the Banco de la Nación. */
export function DetractionFields({ value, onChange, companyAccount, excluded = [] }: Props) {
  const codes = useCatalog('54')
  const set = (patch: Partial<DeductionInput>) => onChange({ ...value, ...patch })
  return (
    <div className="form-grid">
      <SelectField label="Bien o servicio" hint="catálogo 54" required value={value.goodsOrServiceCode} onChange={(event) => set({ goodsOrServiceCode: event.target.value })}>
        <option value="">Elija…</option>
        {(codes.data ?? []).filter((entry) => !excluded.includes(entry.code)).map((entry) => <option key={entry.code} value={entry.code}>{entry.code} · {entry.description}</option>)}
      </SelectField>
      <TextField label="Porcentaje" hint="% de la detracción" type="number" min="0" max="100" step="any" required value={value.percentage} onChange={(event) => set({ percentage: event.target.value })} />
      <TextField label="Monto de la detracción" hint="en soles" type="number" min="0" step="0.01" required value={value.amount} onChange={(event) => set({ amount: event.target.value })} />
      <TextField
        label="Cuenta en el Banco de la Nación"
        hint={companyAccount ? 'vacía: se usa la de la empresa' : 'obligatoria si la empresa no la tiene registrada'}
        required={!companyAccount}
        maxLength={100}
        value={value.account}
        onChange={(event) => set({ account: event.target.value })}
      />
    </div>
  )
}
