import { describe, expect, it } from 'vitest'
import { decodeCsv, IMPORT_TEMPLATES, templateCsv } from './csv'

describe('decodeCsv', () => {
  it('reads UTF-8 as it is', () => {
    const bytes = new TextEncoder().encode('nombre\nAna Pérez Ñandú\n')
    expect(decodeCsv(bytes.buffer as ArrayBuffer)).toBe('nombre\nAna Pérez Ñandú\n')
  })

  it('falls back to Windows-1252 when the bytes are not valid UTF-8 (Excel «CSV» on Windows)', () => {
    // «Pérez Ñandú» in Windows-1252: é = 0xE9, Ñ = 0xD1, ú = 0xFA
    const bytes = Uint8Array.from([0x50, 0xe9, 0x72, 0x65, 0x7a, 0x20, 0xd1, 0x61, 0x6e, 0x64, 0xfa])
    expect(decodeCsv(bytes.buffer as ArrayBuffer)).toBe('Pérez Ñandú')
  })
})

describe('templates', () => {
  it('start with the columns that the API requires and carry a byte order mark for Excel', () => {
    for (const kind of ['customers', 'products'] as const) {
      const text = templateCsv(kind)
      expect(text.startsWith('﻿')).toBe(true)
      expect(text).toContain(IMPORT_TEMPLATES[kind].columns)
    }
    expect(IMPORT_TEMPLATES.customers.columns).toContain('numero_documento')
    expect(IMPORT_TEMPLATES.products.columns).toContain('afectacion_igv')
    expect(IMPORT_TEMPLATES.products.columns).toContain('valor_unitario')
  })

  it('have examples with as many cells as columns', () => {
    for (const template of Object.values(IMPORT_TEMPLATES)) {
      const width = template.columns.split(',').length
      for (const example of template.example) expect(example.split(',').length).toBe(width)
    }
  })
})
