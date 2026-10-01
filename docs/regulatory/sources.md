# Registro de fuentes normativas

Registro de cada revisión de fuente oficial. Regla: ninguna regla tributaria se implementa sin una fila aquí (o en `matrix.md` apuntando a una fila de aquí).

Estados de verificación: **V** = leído en la fuente oficial primaria; **P** = pendiente de verificar en la fuente primaria; **U** = dato suministrado por el usuario o por fuente secundaria, sin comprobar.

Método de acceso: el portal `cpe.sunat.gob.pe` responde HTTP 403 a clientes automáticos (WebFetch/curl). Las páginas y PDF se leyeron con un navegador real (panel de navegador de Claude Code) y extracción de texto con pdf.js. Quien repita la revisión debe usar un navegador.

| # | Source | Document | URL | Resolution | ReviewedAt | EffectiveFrom | Estado | Impact |
|---|--------|----------|-----|------------|------------|---------------|--------|--------|
| S01 | SUNAT – Portal CPE | Proveedor de Servicios Electrónicos – PSE (última modificación del portal: 27/10/2025) | https://cpe.sunat.gob.pe/aliados/pse | RS 199-2015/SUNAT, mod. RS 108-2022/SUNAT | 2026-09-30 | vigente | V | Requisitos de inscripción/permanencia PSE (`docs/pse/requirements.md`). |
| S02 | SUNAT – Portal CPE | SEE – Del Contribuyente (mod. 30/05/2025) | https://cpe.sunat.gob.pe/sistema_emision/see_contribuyente | RS 097-2012/SUNAT y modif. | 2026-09-30 | vigente | V | Plazo de envío, calidad de emisor, estados de CDR, reglas de NC/ND, rechazo de facturas. |
| S03 | SUNAT – Portal CPE | Guías y Manuales (mod. 24/07/2026) | https://cpe.sunat.gob.pe/guias-y-manuales | — | 2026-09-30 | vigente | V | Índice oficial de XSD, XSL, guías XML 2.1, reglas de validación (26.08.2026), Manual del Programador. |
| S04 | SUNAT | Manual del Programador – Emisión electrónica desde Sistemas del Contribuyente (versión **Mayo 2021**) | https://cpe.sunat.gob.pe/sites/default/files/inline-files/manual_programador%20(1).pdf | RS 097-2012/SUNAT y modif. | 2026-09-30 | desde 2021 (no hay versión más reciente publicada en S03) | V | Endpoints SOAP, WS-Security, `sendBill/sendSummary/sendPack/getStatus`, nombres de archivo, firma, códigos de error. Ver `current-baseline.md` §4. |
| S05 | SUNAT – Portal CPE | Sistemas de emisión GRE (mod. 22/04/2025) | https://cpe.sunat.gob.pe/node/116 | — | 2026-09-30 | vigente | V | GRE: SEE-SOL / SEE-Contribuyente; GRE no se emite por SEE-OSE; CDR aceptada antes de iniciar traslado; reglas de validación GRE 25.09.2026. |
| S06 | SUNAT | Manual de Servicios Web Plataforma Nueva GRE (actualizado 04/10/2022) | https://cpe.sunat.gob.pe/sites/default/files/inline-files/Manual_Servicios_GRE%20(1)_0.pdf | — | 2026-09-30 | vigente | V (parcial: solo autenticación/errores) | API REST con OAuth2 (password grant con Clave SOL). Endpoints de envío en "Manual URL – GRE.xlsx" (no revisado: **P**). |
| S07 | SUNAT – Portal CPE | OSE – Operador de Servicios Electrónicos (mod. 25/09/2025) | https://cpe.sunat.gob.pe/aliados/ose | RS 117-2017/SUNAT, RS 092-2018/SUNAT | 2026-09-30 | vigente | V | Canal `ThirdPartyOse`: CDR de OSE válido a todo efecto tributario; requisitos OSE (300 UIT, carta fianza) como referencia de madurez. |
| S08 | SUNAT – Portal CPE | Comunicados CPE (mod. 27/10/2025) | https://cpe.sunat.gob.pe/comunicados | — | 2026-09-30 | 2021-06-21 | V | Receptor SOAP único: `e-factura.sunat.gob.pe`; `www.sunat.gob.pe` y `ww1.sunat.gob.pe` desactivados desde 20/07/2021. |
| S09 | SUNAT – Portal CPE | Concurrencia / Procedimiento de contingencia (mod. 08/07/2025) | https://cpe.sunat.gob.pe/informacion_general/procedimiento_contingencia | RS 113-2018, 181-2018, 254-2018, 295-2018, 043-2019 | 2026-09-30 | vigente | V | Contingencia = formatos preimpresos autorizados, envío hasta 7.º día calendario. Define el alcance del modelo de contingencia (`current-baseline.md` §8). |
| S10 | SUNAT | RS 000049-2026/SUNAT (26/03/2026) – PEI WEB | https://www.sunat.gob.pe/legislacion/superin/2026/000049-2026.pdf | RS 049-2026/SUNAT | 2026-09-30 | 2026-08-01 | V | **Sin impacto en el core** (aplica a documentos impresos, PSE-CF, PSE-ME). Registrado para evitar falsa alarma. |
| S11 | SUNAT – Portal CPE | SIRE – Manual del servicio API del Registro de Ventas e Información Electrónica v30 (2026-06) y Compras | https://cpe.sunat.gob.pe/node/158 | — | 2026-09-30 | vigente | P (solo identificado, no leído) | Fase 9. No se implementa nada de SIRE antes de leer los manuales vigentes. |
| S12 | SUNAT – Portal CPE | Factura con detracción | https://cpe.sunat.gob.pe/factura-con-detraccion | — | — | — | P | Fase 7 / TaxEngine SPOT. |
| S13 | SUNAT – Portal CPE | Plataforma de Confirmación (Factoring) | https://cpe.sunat.gob.pe/plataforma-de-confirmacion-factoring | — | — | — | P | Fase 9. |
| S14 | Fuente secundaria | Prensa/blogs sobre RS 108-2022 (elimina la homologación como requisito de inscripción PSE) | varios | RS 108-2022/SUNAT | 2026-09-30 | 2022 | U | **Contradice** el texto del portal (S01) que aún menciona "inscripción y homologación". Pendiente leer el texto de la RS. No es fuente normativa. |
| S15 | Usuario | UIT 2026 = S/ 5,500 | — | DS pendiente de identificar | — | 2026-01-01 | U | Umbral 150 UIT = S/ 825,000 (PSE). Verificar en El Peruano / MEF. |

## Revisión 2026-10-01 (continuación)

Acceso de archivos: se leyeron en memoria con el navegador (xlsx con SheetJS, zip con JSZip, PDF con pdf.js). **Descargarlos al repositorio requiere autorización explícita del usuario** (pendiente de preguntar); `curl` con cabeceras de navegador sí obtiene el xlsx, pero no se ejecutó.

| # | Source | Document | URL | Resolution | ReviewedAt | EffectiveFrom | Estado | Impact |
|---|--------|----------|-----|------------|------------|---------------|--------|--------|
| S16 | SUNAT – Portal CPE | **Reglas de validación CPE, actualizado al 26.08.2026** (xlsx, 855 113 bytes, SHA-256 `cb5e871cfe3b81abea7faf25b156e5d35b21e8c4837979350919926612da73b6`). Hojas: General, Firma, Factura2_0, Boleta2_0, NotaCredito2_0, NotaDebito2_0, Resumen Diario1_1, Comunicación de Baja1_0, Retenciones1_0, Percepciones1_0, LiquidacionCompra2_0, CDR-OSE, CódigosRetorno (2 080 filas), **Catálogos (Anexo N.°8)**, Listados, Parámetros, Control de Cambios | https://cpe.sunat.gob.pe/sites/default/files/2026-08/Reglas%20de%20validaci%C3%B3n%20-%20actualizado%20al%2026.08.2026.xlsx | — | 2026-10-01 | 2026-08-26 | V (leído en memoria; archivo **no** almacenado en el repositorio) | Fuente oficial de catálogos (47 catálogos), códigos de retorno y validaciones por tag. Ver `current-baseline.md` §10. |
| S17 | SUNAT – Portal CPE | **XSD** (zip, 609 363 bytes, SHA-256 `5cac9d9353521340fbc15d23f465a4946b004535b9ff411541c87da01694dbd5`): UBL 2.1 estándar OASIS (Invoice, CreditNote, DebitNote, ApplicationResponse, DespatchAdvice…, xmldsig, XAdES) y UBL 2.0 con extensiones `UBLPE-*` (Invoice, CreditNote, DebitNote, Perception, Retention, SummaryDocuments, VoidedDocuments) | https://cpe.sunat.gob.pe/sites/default/files/inline-files/Archivos%20XSD%20(1).zip | — | 2026-10-01 | 2022-02-28 | V (listado de contenido; archivo no almacenado) | Fase 3: validación XSD. Resumen diario y baja siguen en UBL 2.0 (`UBLPE-*`). |
| S18 | SUNAT – Portal CPE | Guías de elaboración XML 2.1: Factura (2 070 772 B), Boleta (1 535 786 B), Nota de Crédito (1 236 314 B), Nota de Débito (1 346 362 B), Resumen Diario 2.0 (929 271 B) | https://cpe.sunat.gob.pe/guias-y-manuales | — | 2026-10-01 | — | P (descargadas en memoria para medir tamaño; **no leídas**) | Fase 3: estructura UBL por documento, QR, leyendas. |

## Pendiente de revisión (antes de la fase que los necesite)

- Catálogos SUNAT vigentes (identificar la norma y anexo que los aprueba; no se cita número de memoria) y la tabla de parámetros 742 de códigos de error.
- Reglas de validación `.xlsx` 26.08.2026 (S03) y 25.09.2026 (S05): descargar y versionar en `docs/regulatory/rules/` (excluidas de git si pesan; guardar hash).
- XSD UBL 2.1 SUNAT (28/02/2022) y XSD GRE (13/07/2022).
- QR: contenido vigente del código QR en la representación impresa.
- Boletas: confirmar si el flujo vigente sigue exigiendo Resumen Diario (RC) o admite envío directo por `sendBill`.
- Detracciones (SPOT), retenciones, percepciones: normas y anexos vigentes.
- Ley 29733 y su reglamento vigente (protección de datos personales); Ley 27269 (firmas y certificados digitales).
- Plazos de conservación de comprobantes (Código Tributario, art. 87 y normas del RCP).
- RS 108-2022/SUNAT (texto completo) y RS 199-2015/SUNAT (texto consolidado).
