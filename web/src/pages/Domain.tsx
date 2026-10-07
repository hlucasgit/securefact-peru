import { useState } from 'react'
import { useAssignDomain, useDomain, useVerifyDomain } from '../api/queries'
import type { BrandScope, DomainInfo, DomainStatus } from '../api/types'
import { Badge, ErrorAlert, Loading, TextField, useToast } from '../components/ui'
import { dateTime } from '../lib/format'
import type { Tone } from '../lib/format'

const STATUS: Record<DomainStatus, { label: string; tone: Tone }> = {
  None: { label: 'Sin dominio', tone: 'neutral' },
  Pending: { label: 'Pendiente de verificar', tone: 'warn' },
  Verified: { label: 'Verificado', tone: 'ok' },
  Unreachable: { label: 'Sin respuesta', tone: 'bad' },
}

/** A value to copy into the DNS: shown as text, and copied with a button where the browser lets the page do it. */
function DnsRecord({ label, name, value }: { label: string; name: string; value: string }) {
  const toast = useToast()
  async function copy() {
    try {
      await navigator.clipboard.writeText(value)
      toast.ok('Copiado.')
    } catch {
      toast.fail(new Error('No se pudo copiar: selecciónelo y cópielo a mano.'))
    }
  }
  return (
    <tr>
      <th scope="row">{label}</th>
      <td className="mono">{name}</td>
      <td className="mono wrap">{value}</td>
      <td className="right tight">
        <button className="btn small" type="button" aria-label={`Copiar el valor de ${label}`} onClick={() => void copy()}>
          Copiar
        </button>
      </td>
    </tr>
  )
}

/**
 * The domain of the portal of a reseller (ADR-051): what the platform assigned, the two DNS records the reseller has to create, and whether the platform found them. A pending domain is
 * checked by the platform every few minutes, so the page looks again by itself while it waits.
 */
export function DomainCard({ scope, canAssign }: { scope: BrandScope; canAssign: boolean }) {
  const domain = useDomain(scope)
  if (domain.isPending) return <div className="card"><Loading /></div>
  if (!domain.data) return <div className="card"><ErrorAlert error={domain.error} /></div>
  return <DomainBody key={`${domain.data.resellerId}:${domain.data.host ?? ''}`} scope={scope} domain={domain.data} canAssign={canAssign} />
}

function DomainBody({ scope, domain, canAssign }: { scope: BrandScope; domain: DomainInfo; canAssign: boolean }) {
  const verify = useVerifyDomain(scope)
  const assign = useAssignDomain(domain.resellerId)
  const toast = useToast()
  const [host, setHost] = useState(domain.host ?? '')
  const status = STATUS[domain.status]
  const waiting = domain.status === 'Pending' || domain.status === 'Unreachable'

  return (
    <div className="card stack">
      <div className="row spread">
        <h2>Dominio del portal</h2>
        <Badge tone={status.tone}>{status.label}</Badge>
      </div>

      {canAssign && (
        <form
          className="stack"
          onSubmit={(event) => {
            event.preventDefault()
            assign.mutate(host.trim() || null, { onSuccess: () => toast.ok('Dominio guardado.') })
          }}
        >
          <ErrorAlert error={assign.error} />
          <TextField label="Dominio" placeholder="portal.ejemplo.pe" hint="sin protocolo, puerto ni ruta" value={host} onChange={(event) => setHost(event.target.value)} />
          <div className="actions">
            <button className="btn primary" type="submit" disabled={assign.isPending || host.trim() === (domain.host ?? '')}>
              Guardar dominio
            </button>
          </div>
        </form>
      )}

      {!domain.host ? (
        <p className="muted">{canAssign ? 'Este revendedor aún no tiene dominio.' : 'La plataforma aún no le asignó un dominio.'}</p>
      ) : (
        <>
          {!canAssign && <p>Dominio asignado: <strong className="mono">{domain.host}</strong></p>}
          {domain.status !== 'Verified' && (
            <>
              <p>Para que el portal funcione en <strong className="mono">{domain.host}</strong>, cree estos dos registros en el DNS del dominio. La plataforma los revisa sola cada pocos minutos, y puede revisarlos ahora.</p>
              <div className="table-wrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Para</th>
                      <th>Nombre</th>
                      <th>Valor</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    <DnsRecord label="TXT (prueba de que el dominio es suyo)" name={domain.txtName ?? ''} value={domain.txtValue ?? ''} />
                    {domain.cnameTarget ? (
                      <DnsRecord label="CNAME (lleva el dominio a la plataforma)" name={domain.host} value={domain.cnameTarget} />
                    ) : (
                      <tr>
                        <th scope="row">Destino</th>
                        <td className="mono">{domain.host}</td>
                        <td colSpan={2}>La plataforma aún no configuró a dónde apuntar el dominio.</td>
                      </tr>
                    )}
                    {domain.edgeAddresses.length > 0 && (
                      <tr>
                        <th scope="row">A o AAAA (si no admite CNAME)</th>
                        <td className="mono">{domain.host}</td>
                        <td className="mono wrap" colSpan={2}>{domain.edgeAddresses.join(', ')}</td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>
              <p className="hint">Un dominio raíz (sin subdominio, como ejemplo.pe) no puede tener un CNAME: use un subdominio como portal.ejemplo.pe, o los registros A o AAAA. Los cambios del DNS pueden tardar en publicarse.</p>
            </>
          )}
          {domain.status === 'Verified' && (
            <div className="alert ok" role="status">
              El dominio está verificado{domain.verifiedAt ? ` desde el ${dateTime(domain.verifiedAt)}` : ''}: el portal muestra la marca en <span className="mono">{domain.host}</span> y la plataforma emite su certificado al primer acceso. La plataforma lo vuelve a revisar cada pocas horas.
            </div>
          )}
          {domain.status === 'Unreachable' && (
            <div className="alert bad" role="alert">
              El dominio estaba verificado y dejó de responder: ya no muestra la marca ni se renueva su certificado. Se recupera solo cuando el DNS vuelva a estar bien.
            </div>
          )}
          {domain.error && domain.status !== 'Verified' && (
            <div className="alert warn" role="status">
              <strong>Lo que falta:</strong> {domain.error}
              {domain.checkedAt && <div className="muted">Revisado el {dateTime(domain.checkedAt)}</div>}
            </div>
          )}
          <ErrorAlert error={verify.error} />
          {waiting && (
            <div className="actions">
              <button className="btn primary" type="button" disabled={verify.isPending} onClick={() => verify.mutate(undefined, { onSuccess: (next) => toast.ok(next.status === 'Verified' ? 'Dominio verificado.' : 'Todavía faltan registros.') })}>
                {verify.isPending ? 'Revisando…' : 'Verificar ahora'}
              </button>
            </div>
          )}
        </>
      )}
    </div>
  )
}
