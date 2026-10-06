import { useState, type ChangeEvent, type CSSProperties } from 'react'
import { useBrandSettings, useRemoveLogo, useSaveBrand, useSetHost, useSetLogo } from '../api/queries'
import type { BrandScope, BrandSettings } from '../api/types'
import { ConfirmButton, ErrorAlert, Loading, PageHeader, TextField, useToast } from '../components/ui'

const DEFAULT_COLOR = '#0f4c81'

/** What a reseller sets so that its users see its name, colour and logo; the platform edits any reseller's and alone assigns the domain. */
export function BrandEditor({ scope, canSetHost }: { scope: BrandScope; canSetHost: boolean }) {
  const settings = useBrandSettings(scope)
  if (settings.isPending) return <Loading />
  if (!settings.data) return <ErrorAlert error={settings.error} />
  return <BrandForm key={settings.data.resellerId} scope={scope} settings={settings.data} canSetHost={canSetHost} />
}

function BrandForm({ scope, settings, canSetHost }: { scope: BrandScope; settings: BrandSettings; canSetHost: boolean }) {
  const save = useSaveBrand(scope)
  const setLogo = useSetLogo(scope)
  const removeLogo = useRemoveLogo(scope)
  const setHost = useSetHost(settings.resellerId)
  const toast = useToast()
  const [value, setValue] = useState({ brandName: settings.brandName ?? '', primaryColor: settings.primaryColor ?? DEFAULT_COLOR, supportEmail: settings.supportEmail ?? '' })
  const [host, setHostValue] = useState(settings.host ?? '')
  const [logoError, setLogoError] = useState<string | null>(null)

  async function pickLogo(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0]
    event.target.value = ''
    if (!file) return
    setLogoError(null)
    if (file.size > 200 * 1024) {
      setLogoError('El logotipo debe pesar 200 KB o menos.')
      return
    }
    const dataBase64 = btoa(String.fromCharCode(...new Uint8Array(await file.arrayBuffer())))
    setLogo.mutate(dataBase64, { onSuccess: () => toast.ok('Logotipo actualizado.') })
  }

  const preview = { '--brand': value.primaryColor } as CSSProperties
  return (
    <div className="stack">
      <form
        className="card stack"
        onSubmit={(event) => {
          event.preventDefault()
          save.mutate({ brandName: value.brandName.trim() || null, primaryColor: value.primaryColor, supportEmail: value.supportEmail.trim() || null }, { onSuccess: () => toast.ok('Marca guardada.') })
        }}
      >
        <h2>Marca</h2>
        <p className="hint">Sus usuarios y los de sus cuentas verán este nombre, este color y este logotipo en el ingreso y en el menú. Sin nombre, se muestra la marca de la plataforma.</p>
        <ErrorAlert error={save.error} />
        <TextField label="Nombre de la marca" maxLength={60} value={value.brandName} onChange={(event) => setValue({ ...value, brandName: event.target.value })} />
        <div className="field">
          <label htmlFor="brand-color-text">
            Color <span className="hint">con el texto blanco encima debe leerse bien (contraste 4.5:1)</span>
          </label>
          <div className="row">
            <input type="color" aria-label="Elegir el color" value={/^#[0-9a-f]{6}$/i.test(value.primaryColor) ? value.primaryColor : DEFAULT_COLOR} onChange={(event) => setValue({ ...value, primaryColor: event.target.value })} />
            <input id="brand-color-text" className="input" style={{ maxWidth: 140 }} maxLength={7} value={value.primaryColor} onChange={(event) => setValue({ ...value, primaryColor: event.target.value })} />
            <span style={preview}>
              <span className="btn primary" aria-hidden="true">
                Así se ven los botones
              </span>
            </span>
          </div>
        </div>
        <TextField label="Correo de soporte" type="email" hint="se muestra en el ingreso" maxLength={254} value={value.supportEmail} onChange={(event) => setValue({ ...value, supportEmail: event.target.value })} />
        <div className="actions">
          <button className="btn primary" type="submit" disabled={save.isPending}>
            Guardar marca
          </button>
        </div>
      </form>

      <div className="card stack">
        <h2>Logotipo</h2>
        <ErrorAlert error={setLogo.error ?? removeLogo.error} />
        {logoError && (
          <div className="alert bad" role="alert">
            {logoError}
          </div>
        )}
        {settings.logoUrl ? <img className="brand-logo-preview" src={settings.logoUrl} alt="Logotipo actual" /> : <p className="muted">Sin logotipo.</p>}
        <div className="row">
          <input id="brand-logo-file" className="visually-hidden" type="file" accept="image/png,image/jpeg,image/webp" onChange={(event) => void pickLogo(event)} />
          <label className="btn file-pick" htmlFor="brand-logo-file">
            {settings.logoUrl ? 'Cambiar logotipo' : 'Subir logotipo'}
          </label>
          {settings.logoUrl && <ConfirmButton label="Quitar logotipo" message="¿Quitar el logotipo?" onConfirm={() => removeLogo.mutate(undefined, { onSuccess: () => toast.ok('Logotipo quitado.') })} />}
        </div>
        <p className="hint">PNG, JPEG o WebP de hasta 200 KB. Se verifica por su contenido; el SVG no se admite.</p>
      </div>

      {canSetHost && (
        <form
          className="card stack"
          onSubmit={(event) => {
            event.preventDefault()
            setHost.mutate(host.trim() || null, { onSuccess: () => toast.ok('Dominio guardado.') })
          }}
        >
          <h2>Dominio del portal</h2>
          <ErrorAlert error={setHost.error} />
          <TextField label="Dominio" placeholder="portal.ejemplo.pe" hint="sin protocolo ni puerto" value={host} onChange={(event) => setHostValue(event.target.value)} />
          <p className="hint">Con el dominio asignado, el ingreso en ese dominio ya muestra la marca. El registro DNS y el certificado del dominio los configura el operador de la plataforma; aquí solo se registra a qué revendedor pertenece.</p>
          <div className="actions">
            <button className="btn primary" type="submit" disabled={setHost.isPending}>
              Guardar dominio
            </button>
          </div>
        </form>
      )}
    </div>
  )
}

/** The reseller's own brand. */
export function ResellerBrand() {
  return (
    <>
      <PageHeader title="Marca" subtitle="Cómo ven la plataforma sus usuarios y los de sus cuentas" />
      <BrandEditor scope={{ kind: 'own' }} canSetHost={false} />
    </>
  )
}
