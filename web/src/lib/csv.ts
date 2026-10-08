// Helpers of the import of customers and products from a CSV (ADR-053). The API reads the text; the page only decodes the file and offers a template.

/** Reads the bytes of a file as text: UTF-8 when valid (Excel's «CSV UTF-8»), Windows-1252 otherwise (Excel's plain «CSV» on Windows), so «ñ» and accents are not lost. */
export function decodeCsv(bytes: ArrayBuffer): string {
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes)
  } catch {
    return new TextDecoder('windows-1252').decode(bytes)
  }
}

export type ImportKind = 'customers' | 'products'

interface Template {
  fileName: string
  columns: string
  help: string
  example: string[]
}

/** The column names are the ones that the API accepts (it also accepts others, see ADR-053); the examples are only that, examples. */
export const IMPORT_TEMPLATES: Record<ImportKind, Template> = {
  customers: {
    fileName: 'plantilla-clientes.csv',
    columns: 'tipo_documento,numero_documento,nombre,direccion,correo,telefono',
    help: 'tipo_documento: RUC, DNI, CE, PASAPORTE o el código del catálogo 06 (si falta la columna, 11 dígitos son RUC y 8 dígitos DNI). Un cliente que ya existe no se modifica.',
    example: ['RUC,20100066603,Empresa de Ejemplo SAC,Av. Ejemplo 123,compras@ejemplo.pe,999888777', 'DNI,45678912,Ana Pérez,,,'],
  },
  products: {
    fileName: 'plantilla-productos.csv',
    columns: 'codigo,descripcion,tipo,unidad,valor_unitario,afectacion_igv,codigo_sunat,categoria',
    help: 'valor_unitario sin IGV, con punto decimal. afectacion_igv: código del catálogo 07, obligatorio (10 gravado, 20 exonerado, 30 inafecto…). tipo: bien o servicio. Sin unidad se usa NIU para bienes y ZZ para servicios (convención de la plataforma: confírmela o indique la unidad). Un producto que ya existe no se modifica.',
    example: ['SKU-001,Cuaderno A4,bien,NIU,12.50,10,,Útiles', 'SRV-001,Instalación,servicio,,300.00,10,,'],
  },
}

export function templateCsv(kind: ImportKind): string {
  const template = IMPORT_TEMPLATES[kind]
  return `﻿${[template.columns, ...template.example].join('\r\n')}\r\n`
}

/** Hands a text to the browser as a file to save. */
export function downloadText(fileName: string, text: string): void {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/csv;charset=utf-8' }))
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  document.body.append(link)
  link.click()
  link.remove()
  URL.revokeObjectURL(url)
}
