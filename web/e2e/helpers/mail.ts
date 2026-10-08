import { readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect } from '@playwright/test'

// The e-mail of the stack of the tests is written as .eml files (Email__Provider=Sandbox, ADR-052): the tests read the message the way a person would, from the link in it.

export const MAIL_DIR = process.env.SF_E2E_MAIL_DIR

export interface Mail {
  to: string
  from: string
  replyTo: string | null
  subject: string
  /** Body with the quoted-printable encoding undone. */
  body: string
}

function decodeQuotedPrintable(text: string): string {
  const joined = text.replace(/=\r?\n/g, '')
  const bytes: number[] = []
  const encoder = new TextEncoder()
  for (let index = 0; index < joined.length; index++) {
    const hex = joined.slice(index + 1, index + 3)
    if (joined[index] === '=' && /^[0-9A-F]{2}$/i.test(hex)) {
      bytes.push(Number.parseInt(hex, 16))
      index += 2
    } else {
      bytes.push(...encoder.encode(joined[index]))
    }
  }
  return new TextDecoder().decode(Uint8Array.from(bytes))
}

/** Undoes the encoded words of a header (RFC 2047), which carry the accents of a name or a subject. */
function decodeWords(value: string): string {
  return value
    .replace(/\?=\s+=\?/g, '?==?')
    .replace(/=\?utf-8\?([qb])\?([^?]*)\?=/gi, (_whole, kind: string, text: string) =>
      kind.toLowerCase() === 'b' ? Buffer.from(text, 'base64').toString('utf-8') : decodeQuotedPrintable(text.replaceAll('_', ' ')),
    )
}

function header(source: string, name: string): string | null {
  const match = new RegExp(`^${name}: ((?:.|\\r?\\n[ \\t])*)`, 'im').exec(source)
  return match ? decodeWords(match[1].replace(/\r?\n[ \t]+/g, ' ').trim()) : null
}

/** The messages written for one address, oldest first. */
export function mailTo(address: string): Mail[] {
  if (!MAIL_DIR) throw new Error('SF_E2E_MAIL_DIR is not set: the API of the tests must run with Email__Provider=Sandbox and this variable pointing to its directory.')
  return readdirSync(MAIL_DIR)
    .filter((name) => name.endsWith('.eml'))
    .sort()
    .map((name) => readFileSync(join(MAIL_DIR, name), 'utf-8'))
    .filter((source) => (header(source, 'To') ?? '').toLowerCase().includes(address.toLowerCase()))
    .map((source) => ({
      to: address,
      from: header(source, 'From') ?? '',
      replyTo: header(source, 'Reply-To'),
      subject: header(source, 'Subject') ?? '',
      body: decodeQuotedPrintable(source),
    }))
}

const RESET_LINK = /https?:\/\/[^\s"<]+\/restablecer#token=[^\s"<]+/

/** Waits for the e-mail with the link to choose a new password (the other notices to the same address, such as the welcome, are not it) and returns the link as it appears in it. */
export async function resetLinkFor(address: string, already = 0): Promise<{ link: string; mail: Mail }> {
  let found: Mail | undefined
  await expect.poll(() => (found = mailTo(address).filter((mail) => RESET_LINK.test(mail.body))[already]) !== undefined, { timeout: 15_000 }).toBe(true)
  return { link: RESET_LINK.exec(found!.body)![0], mail: found! }
}
