import { useState, type FormEvent } from 'react'
import { useCatalog, useCustomers, useDeactivateCustomer, useDeactivateProduct, useProducts, useSaveCustomer, useSaveProduct } from '../api/queries'
import type { Customer, CustomerDetails, Product, ProductDetails } from '../api/types'
import { ImportModal } from '../components/ImportModal'
import { Badge, ConfirmButton, Empty, ErrorAlert, Loading, Modal, PageHeader, SelectField, TextField, useToast } from '../components/ui'
import { money } from '../lib/format'

function Search({ value, onChange, label }: { value: string; onChange: (value: string) => void; label: string }) {
  return <input className="input" type="search" aria-label={label} placeholder={label} style={{ maxWidth: 320 }} value={value} onChange={(event) => onChange(event.target.value)} />
}

const EMPTY_CUSTOMER: CustomerDetails = { documentTypeCode: '6', documentNumber: '', name: '', address: null, email: null, phone: null }

export function Customers() {
  const [search, setSearch] = useState('')
  const { data, isPending, error } = useCustomers(search)
  const deactivate = useDeactivateCustomer()
  const [editing, setEditing] = useState<Customer | 'new' | null>(null)
  const [importing, setImporting] = useState(false)
  const types = useCatalog('06')

  return (
    <>
      <PageHeader title="Clientes" subtitle="Adquirentes frecuentes">
        <button className="btn" type="button" onClick={() => setImporting(true)}>Importar CSV</button>
        <button className="btn primary" type="button" onClick={() => setEditing('new')}>Nuevo cliente</button>
      </PageHeader>
      <div className="card">
        <div style={{ marginBottom: 12 }}><Search value={search} onChange={setSearch} label="Buscar por nombre o documento" /></div>
        <ErrorAlert error={error ?? deactivate.error} />
        {isPending ? <Loading /> : data && data.length > 0 ? (
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th>Documento</th><th>Nombre</th><th>Correo</th><th>Estado</th><th /></tr></thead>
              <tbody>
                {data.map((customer) => (
                  <tr key={customer.id}>
                    <td className="tight"><span className="muted">{types.data?.find((type) => type.code === customer.documentTypeCode)?.description.split(' ')[0] ?? customer.documentTypeCode}</span> <span className="mono">{customer.documentNumber}</span></td>
                    <td>{customer.name}</td>
                    <td>{customer.email ?? '—'}</td>
                    <td><Badge tone={customer.isActive ? 'ok' : 'neutral'}>{customer.isActive ? 'Activo' : 'Inactivo'}</Badge></td>
                    <td className="right tight">
                      <button className="btn small" type="button" onClick={() => setEditing(customer)}>Editar</button>{' '}
                      {customer.isActive && <ConfirmButton label="Desactivar" message={`¿Desactivar a ${customer.name}?`} onConfirm={() => deactivate.mutate(customer.id)} />}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : <Empty>No hay clientes.</Empty>}
      </div>
      {editing && <CustomerModal customer={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
      {importing && <ImportModal kind="customers" onClose={() => setImporting(false)} />}
    </>
  )
}

function CustomerModal({ customer, onClose }: { customer: Customer | null; onClose: () => void }) {
  const save = useSaveCustomer()
  const toast = useToast()
  const types = useCatalog('06')
  const [value, setValue] = useState<CustomerDetails>(customer ?? EMPTY_CUSTOMER)
  const optional = (key: 'address' | 'email' | 'phone') => ({ value: value[key] ?? '', onChange: (event: { target: { value: string } }) => setValue({ ...value, [key]: event.target.value || null }) })

  function submit(event: FormEvent) {
    event.preventDefault()
    save.mutate({ id: customer?.id, details: value }, { onSuccess: () => { toast.ok('Cliente guardado.'); onClose() } })
  }

  return (
    <Modal title={customer ? 'Editar cliente' : 'Nuevo cliente'} onClose={onClose}>
      <form className="stack" onSubmit={submit}>
        <ErrorAlert error={save.error} />
        <SelectField label="Tipo de documento" value={value.documentTypeCode} onChange={(event) => setValue({ ...value, documentTypeCode: event.target.value })}>
          {(types.data ?? []).map((type) => <option key={type.code} value={type.code}>{type.description}</option>)}
        </SelectField>
        <TextField label="Número de documento" required value={value.documentNumber} onChange={(event) => setValue({ ...value, documentNumber: event.target.value })} />
        <TextField label="Nombre o razón social" required value={value.name} onChange={(event) => setValue({ ...value, name: event.target.value })} />
        <TextField label="Dirección" {...optional('address')} />
        <TextField label="Correo" type="email" {...optional('email')} />
        <TextField label="Teléfono" {...optional('phone')} />
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>Cancelar</button>
          <button className="btn primary" type="submit" disabled={save.isPending}>Guardar</button>
        </div>
      </form>
    </Modal>
  )
}

const EMPTY_PRODUCT: ProductDetails = { internalCode: '', description: '', kind: 'Goods', unitCode: 'NIU', unitValue: 0, igvAffectationCode: '10', sunatProductCode: null, category: null }

export function Products() {
  const [search, setSearch] = useState('')
  const { data, isPending, error } = useProducts(search)
  const deactivate = useDeactivateProduct()
  const [editing, setEditing] = useState<Product | 'new' | null>(null)
  const [importing, setImporting] = useState(false)

  return (
    <>
      <PageHeader title="Productos" subtitle="Catálogo de bienes y servicios">
        <button className="btn" type="button" onClick={() => setImporting(true)}>Importar CSV</button>
        <button className="btn primary" type="button" onClick={() => setEditing('new')}>Nuevo producto</button>
      </PageHeader>
      <div className="card">
        <div style={{ marginBottom: 12 }}><Search value={search} onChange={setSearch} label="Buscar por código o descripción" /></div>
        <ErrorAlert error={error ?? deactivate.error} />
        {isPending ? <Loading /> : data && data.length > 0 ? (
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th>Código</th><th>Descripción</th><th>Tipo</th><th className="num">Valor unitario</th><th>Afectación</th><th>Estado</th><th /></tr></thead>
              <tbody>
                {data.map((product) => (
                  <tr key={product.id}>
                    <td className="mono">{product.internalCode}</td>
                    <td>{product.description}</td>
                    <td>{product.kind === 'Goods' ? 'Bien' : 'Servicio'}</td>
                    <td className="num">{money(product.unitValue)}</td>
                    <td>{product.igvAffectationCode}</td>
                    <td><Badge tone={product.isActive ? 'ok' : 'neutral'}>{product.isActive ? 'Activo' : 'Inactivo'}</Badge></td>
                    <td className="right tight">
                      <button className="btn small" type="button" onClick={() => setEditing(product)}>Editar</button>{' '}
                      {product.isActive && <ConfirmButton label="Desactivar" message={`¿Desactivar ${product.description}?`} onConfirm={() => deactivate.mutate(product.id)} />}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : <Empty>No hay productos.</Empty>}
      </div>
      {editing && <ProductModal product={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
      {importing && <ImportModal kind="products" onClose={() => setImporting(false)} />}
    </>
  )
}

function ProductModal({ product, onClose }: { product: Product | null; onClose: () => void }) {
  const save = useSaveProduct()
  const toast = useToast()
  const affectations = useCatalog('07')
  const [value, setValue] = useState<ProductDetails>(product ?? EMPTY_PRODUCT)

  function submit(event: FormEvent) {
    event.preventDefault()
    save.mutate({ id: product?.id, details: value }, { onSuccess: () => { toast.ok('Producto guardado.'); onClose() } })
  }

  return (
    <Modal title={product ? 'Editar producto' : 'Nuevo producto'} onClose={onClose}>
      <form className="stack" onSubmit={submit}>
        <ErrorAlert error={save.error} />
        <TextField label="Código interno" required value={value.internalCode} onChange={(event) => setValue({ ...value, internalCode: event.target.value })} />
        <TextField label="Descripción" required value={value.description} onChange={(event) => setValue({ ...value, description: event.target.value })} />
        <SelectField label="Tipo" value={value.kind} onChange={(event) => setValue({ ...value, kind: event.target.value as ProductDetails['kind'] })}>
          <option value="Goods">Bien</option>
          <option value="Service">Servicio</option>
        </SelectField>
        <TextField label="Unidad de medida" hint="código SUNAT, ej. NIU, ZZ" required maxLength={3} value={value.unitCode} onChange={(event) => setValue({ ...value, unitCode: event.target.value.toUpperCase() })} />
        <TextField label="Valor unitario (sin impuestos)" type="number" step="0.0000000001" min="0" required value={value.unitValue} onChange={(event) => setValue({ ...value, unitValue: Number(event.target.value) })} />
        <SelectField label="Afectación al IGV" value={value.igvAffectationCode} onChange={(event) => setValue({ ...value, igvAffectationCode: event.target.value })}>
          {(affectations.data ?? []).map((entry) => <option key={entry.code} value={entry.code}>{entry.code} · {entry.description}</option>)}
        </SelectField>
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>Cancelar</button>
          <button className="btn primary" type="submit" disabled={save.isPending}>Guardar</button>
        </div>
      </form>
    </Modal>
  )
}
