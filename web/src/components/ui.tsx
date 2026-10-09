import { createContext, useCallback, useContext, useEffect, useId, useMemo, useRef, useState, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes, type TextareaHTMLAttributes } from 'react'
import { errorMessage } from '../api/http'
import type { EDocumentState } from '../api/types'
import { STATE_LABELS, STATE_TONES, type Tone } from '../lib/format'

export function Badge({ tone, children }: { tone: Tone; children: ReactNode }) {
  return <span className={`badge ${tone}`}>{children}</span>
}

export function StateBadge({ state, voided }: { state: EDocumentState; voided?: boolean }) {
  return (
    <span className="row" style={{ gap: 6 }}>
      <Badge tone={STATE_TONES[state]}>{STATE_LABELS[state]}</Badge>
      {voided && <Badge tone="bad">Anulado</Badge>}
    </span>
  )
}

export function Loading({ label = 'Cargando…' }: { label?: string }) {
  return (
    <div className="loading" role="status">
      <span className="spinner" aria-hidden="true" />
      {label}
    </div>
  )
}

export function ErrorAlert({ error }: { error: unknown }) {
  if (!error) return null
  return (
    <div className="alert bad" role="alert">
      {errorMessage(error)}
    </div>
  )
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="empty">{children}</div>
}

export function PageHeader({ title, subtitle, children }: { title: string; subtitle?: string; children?: ReactNode }) {
  return (
    <div className="page-head">
      <div>
        <h1>{title}</h1>
        {subtitle && <p>{subtitle}</p>}
      </div>
      {children && <div className="actions">{children}</div>}
    </div>
  )
}

interface FieldProps {
  label: string
  hint?: string
  error?: string
  children: (id: string) => ReactNode
}

export function Field({ label, hint, error, children }: FieldProps) {
  const id = useId()
  // The label names the control and nothing else: a label that wraps a select would take the text of every option into its name.
  return (
    <div className="field">
      <label htmlFor={id}>
        {label} {hint && <span className="hint">{hint}</span>}
      </label>
      {children(id)}
      {error && <span className="error-text">{error}</span>}
    </div>
  )
}

type TextProps = { label: string; hint?: string; error?: string } & InputHTMLAttributes<HTMLInputElement>

export function TextField({ label, hint, error, ...input }: TextProps) {
  return <Field label={label} hint={hint} error={error}>{(id) => <input id={id} className="input" {...input} />}</Field>
}

type AreaProps = { label: string; hint?: string; error?: string } & TextareaHTMLAttributes<HTMLTextAreaElement>

export function TextAreaField({ label, hint, error, ...input }: AreaProps) {
  return <Field label={label} hint={hint} error={error}>{(id) => <textarea id={id} className="input" {...input} />}</Field>
}

type SelectProps = { label: string; hint?: string; error?: string; children: ReactNode } & SelectHTMLAttributes<HTMLSelectElement>

export function SelectField({ label, hint, error, children, ...input }: SelectProps) {
  return <Field label={label} hint={hint} error={error}>{(id) => <select id={id} className="input" {...input}>{children}</select>}</Field>
}

export function Modal({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null)
  const titleId = useId()
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null
    ref.current?.querySelector<HTMLElement>('input, select, textarea, button')?.focus()
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('keydown', onKey)
      previous?.focus()
    }
  }, [onClose])
  return (
    <div className="backdrop" onMouseDown={(event) => event.target === event.currentTarget && onClose()}>
      <div className="modal" role="dialog" aria-modal="true" aria-labelledby={titleId} ref={ref}>
        <h2 id={titleId}>{title}</h2>
        {children}
      </div>
    </div>
  )
}

export function Tabs<T extends string>({ tabs, value, onChange }: { tabs: { id: T; label: string }[]; value: T; onChange: (id: T) => void }) {
  return (
    <div className="tabs" role="tablist">
      {tabs.map((tab) => (
        <button key={tab.id} role="tab" aria-selected={tab.id === value} className="tab" onClick={() => onChange(tab.id)} type="button">
          {tab.label}
        </button>
      ))}
    </div>
  )
}

export function KeyValues({ items }: { items: [string, ReactNode][] }) {
  return (
    <dl className="kv">
      {items.map(([key, value]) => (
        <div key={key} style={{ display: 'contents' }}>
          <dt>{key}</dt>
          <dd>{value}</dd>
        </div>
      ))}
    </dl>
  )
}

interface ToastItem {
  id: number
  tone: 'ok' | 'bad'
  text: string
}

const ToastContext = createContext<{ ok(text: string): void; fail(error: unknown): void } | null>(null)

export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<ToastItem[]>([])
  const counter = useRef(0)
  const push = useCallback((tone: ToastItem['tone'], text: string) => {
    const id = ++counter.current
    setItems((current) => [...current, { id, tone, text }])
    window.setTimeout(() => setItems((current) => current.filter((item) => item.id !== id)), tone === 'ok' ? 4000 : 8000)
  }, [])
  const value = useMemo(() => ({ ok: (text: string) => push('ok', text), fail: (error: unknown) => push('bad', errorMessage(error)) }), [push])
  return (
    <ToastContext.Provider value={value}>
      {children}
      <div className="toasts" aria-live="polite">
        {items.map((item) => (
          <div key={item.id} className={`toast ${item.tone}`} role={item.tone === 'bad' ? 'alert' : 'status'}>
            {item.text}
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  )
}

export function useToast() {
  const value = useContext(ToastContext)
  if (!value) throw new Error('useToast needs a ToastProvider')
  return value
}

/** Asks before an action that cannot be undone. */
export function ConfirmButton({
  label,
  message,
  onConfirm,
  className = 'btn small danger',
  disabled,
  ariaLabel,
}: {
  label: string
  message: string
  onConfirm: () => void
  className?: string
  disabled?: boolean
  /** What a screen reader says when the visible text is short and the row has several of them. */
  ariaLabel?: string
}) {
  return (
    <button type="button" className={className} disabled={disabled} aria-label={ariaLabel} onClick={() => window.confirm(message) && onConfirm()}>
      {label}
    </button>
  )
}
