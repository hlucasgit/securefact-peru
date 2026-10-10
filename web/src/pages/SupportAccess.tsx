import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import { useEnterSupport, useGrantSupport, useRevokeSupport, useSupportAvailable, useSupportGrants } from '../api/queries'
import type { SupportGrantStatus } from '../api/types'
import { useSession } from '../auth/session'
import { Badge, ConfirmButton, Empty, ErrorAlert, Loading, Modal, PageHeader, SelectField, TextAreaField, TextField, useToast } from '../components/ui'
import { dateTime } from '../lib/format'
import type { Tone } from '../lib/format'

const STATUS: Record<SupportGrantStatus, { label: string; tone: Tone }> = {
  Active: { label: 'Vigente', tone: 'ok' },
  Expired: { label: 'Vencida', tone: 'neutral' },
  Revoked: { label: 'Quitada', tone: 'neutral' },
}

const DURATIONS = [
  { hours: 1, label: '1 hora' },
  { hours: 4, label: '4 horas' },
  { hours: 24, label: '24 horas' },
  { hours: 72, label: '72 horas' },
]

/** The owner decides who supports the account, for how long, and takes it back whenever (ADR-069). */
export function SupportAccess() {
  const { data, isPending, error } = useSupportGrants()
  const revoke = useRevokeSupport()
  const toast = useToast()
  const [granting, setGranting] = useState(false)
  const active = (data ?? []).some((grant) => grant.status === 'Active')

  return (
    <>
      <PageHeader title="Acceso de soporte" subtitle="Quien le da soporte puede entrar a su cuenta solo si usted lo autoriza, y solo para mirar">
        <button className="btn primary" type="button" disabled={active} onClick={() => setGranting(true)}>
          Autorizar acceso
        </button>
      </PageHeader>
      <div className="card">
        <p className="hint" style={{ marginTop: 0 }}>
          Mientras la autorización esté vigente, una persona de soporte puede ver sus comprobantes, clientes, productos y usuarios, pero no cambia nada ni ve sus llaves de API, webhooks, contraseñas ni secretos. Usted recibe un correo cada vez que alguien entra, y
          puede quitar la autorización cuando quiera: la sesión de quien esté dentro termina al instante.
        </p>
        <ErrorAlert error={error ?? revoke.error} />
        {isPending ? (
          <Loading />
        ) : data && data.length > 0 ? (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Autorizada</th>
                  <th>Vence</th>
                  <th>Nota</th>
                  <th className="right">Entradas</th>
                  <th>Última entrada</th>
                  <th>Estado</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {data.map((grant) => (
                  <tr key={grant.id}>
                    <td>{dateTime(grant.createdAt)}</td>
                    <td>{dateTime(grant.expiresAt)}</td>
                    <td>{grant.note ?? ''}</td>
                    <td className="right">{grant.entries}</td>
                    <td>{grant.lastEntryAt ? dateTime(grant.lastEntryAt) : 'Nunca'}</td>
                    <td>
                      <Badge tone={STATUS[grant.status].tone}>{STATUS[grant.status].label}</Badge>
                    </td>
                    <td className="right tight">
                      {grant.status === 'Active' && (
                        <ConfirmButton
                          label="Quitar"
                          ariaLabel="Quitar la autorización de soporte"
                          message="¿Quitar la autorización? Quien esté dentro de su cuenta saldrá al instante."
                          onConfirm={() => revoke.mutate(grant.id, { onSuccess: () => toast.ok('Autorización quitada.') })}
                        />
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <Empty>Nunca autorizó el acceso de soporte.</Empty>
        )}
      </div>
      {granting && <GrantModal onClose={() => setGranting(false)} />}
    </>
  )
}

function GrantModal({ onClose }: { onClose: () => void }) {
  const grant = useGrantSupport()
  const toast = useToast()
  const [form, setForm] = useState({ hours: '4', note: '' })
  return (
    <Modal title="Autorizar el acceso de soporte" onClose={onClose}>
      <form
        className="stack"
        onSubmit={(event) => {
          event.preventDefault()
          grant.mutate(
            { hours: Number(form.hours), note: form.note.trim() || null },
            {
              onSuccess: () => {
                toast.ok('Acceso autorizado.')
                onClose()
              },
            },
          )
        }}
      >
        <ErrorAlert error={grant.error} />
        <SelectField label="Durante" value={form.hours} onChange={(event) => setForm({ ...form, hours: event.target.value })}>
          {DURATIONS.map((option) => (
            <option key={option.hours} value={option.hours}>
              {option.label}
            </option>
          ))}
        </SelectField>
        <TextField label="Nota" maxLength={200} hint="para qué lo autoriza (opcional)" value={form.note} onChange={(event) => setForm({ ...form, note: event.target.value })} />
        <div className="actions">
          <button className="btn" type="button" onClick={onClose}>
            Cancelar
          </button>
          <button className="btn primary" type="submit" disabled={grant.isPending}>
            Autorizar
          </button>
        </div>
      </form>
    </Modal>
  )
}

/** On the account of a customer: whether it authorized the access, and the entry (with the reason that its owner reads). For the platform and for the reseller. */
export function EnterSupportCard({ tenantId }: { tenantId: string }) {
  const available = useSupportAvailable()
  const enter = useEnterSupport()
  const { enterSupport } = useSession()
  const navigate = useNavigate()
  const client = useQueryClient()
  const [open, setOpen] = useState(false)
  const [reason, setReason] = useState('')
  const grant = (available.data ?? []).find((item) => item.tenantId === tenantId)

  return (
    <div className="card">
      <h2>Entrar como soporte</h2>
      <ErrorAlert error={available.error ?? enter.error} />
      {available.isPending ? (
        <Loading />
      ) : grant ? (
        <>
          <p>
            La cuenta autorizó el acceso hasta el <strong>{dateTime(grant.expiresAt)}</strong>. Entra en solo lectura y su propietario recibe un correo con el motivo.
          </p>
          <button className="btn primary" type="button" onClick={() => setOpen(true)}>
            Entrar como soporte
          </button>
        </>
      ) : (
        <p>La cuenta no autorizó el acceso de soporte. Su propietario lo autoriza desde «Acceso de soporte».</p>
      )}
      {open && (
        <Modal title="Entrar como soporte" onClose={() => setOpen(false)}>
          <form
            className="stack"
            onSubmit={(event) => {
              event.preventDefault()
              enter.mutate(
                { tenantId, reason: reason.trim() },
                {
                  onSuccess: (session) => {
                    client.clear() // what was loaded for the own session is not shown inside the account
                    enterSupport(session)
                    navigate('/')
                  },
                },
              )
            }}
          >
            <ErrorAlert error={enter.error} />
            <TextAreaField label="Motivo" required minLength={3} maxLength={300} hint="el propietario lo lee en el correo y queda en la auditoría" value={reason} onChange={(event) => setReason(event.target.value)} />
            <div className="actions">
              <button className="btn" type="button" onClick={() => setOpen(false)}>
                Cancelar
              </button>
              <button className="btn primary" type="submit" disabled={enter.isPending || reason.trim().length < 3}>
                Entrar
              </button>
            </div>
          </form>
        </Modal>
      )}
    </div>
  )
}
