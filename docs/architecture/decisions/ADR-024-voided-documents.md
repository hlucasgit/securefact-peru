# ADR-024: Comunicación de baja

- Estado: Aceptada · Fecha: 2026-10-01

## Fuentes
Hoja `Comunicación de Baja1_0` de las reglas de validación del 26.08.2026 (S16), XSD `UBLPE-VoidedDocuments-1.0` (S17), Manual del programador (S04: nombre `RUC-RA-AAAAMMDD-correlativo`, `sendSummary` + `getStatus`) y la prueba contra el beta del 2026-10-01.

## Decisión
- **Alcance**: facturas (01) y notas (07/08) **de facturas** (serie `F***`, regla 2310) por comunicación de baja (`RA`); boletas (03) y notas de boletas (serie `B***`) por **resumen diario con líneas de estado 3** (catálogo 19, «anulado»), ver más abajo.
- **Generador** (`IVoidedDocumentsGenerator`): UBL 2.0 `VoidedDocuments` (`CustomizationID` 1.0), orden del XSD, identificador `RA-AAAAMMDD-n` con la **fecha de generación** (reglas 2220/2346), `ReferenceDate` = fecha de emisión de los documentos, una línea por documento con tipo, serie, número y motivo (3–100 caracteres, sin saltos de línea). Valida contra el XSD y contiene todas las etiquetas obligatorias de la hoja. Máximo 500 líneas por archivo (suposición R-046, igual que el resumen).
- **Creación** (`POST /api/v1/voids`, permiso `cpe.send`): recibe documentos de Billing con su motivo y crea **una comunicación por fecha de emisión** (regla 2375: una sola fecha por archivo). Cada documento debe tener el CDR aceptado (regla 2105; rechazado = 2398), no estar ya anulado ni en una comunicación pendiente (2323; índice único de item activo), no repetirse (2348) y tener **como máximo 7 días** desde su emisión (2957). Todo o nada: un documento inválido rechaza la solicitud (`SF-CPE-011`).
- **Documento electrónico** tipo `RA`: se firma, se envía con `sendSummary` y se sigue con `getStatus` como el resumen (ADR-020); el CDR solo se acepta si su `ReferenceID` es el identificador `RA-…` y el RUC es el del contribuyente.
- **Los documentos anulados no cambian de estado**: un documento con CDR final es inmutable (ADR-019). «Anulado» se deriva: lo cubre una comunicación **aceptada**; `ElectronicDocumentDto.Voided` lo expone. Una comunicación rechazada libera sus documentos para volver a anularlos (nuevo correlativo); una fallida los mantiene reservados hasta reintentar.
- **Base de datos**: los items reutilizan `cpe.summary_item` con el motivo (inmutable por disparador).
- **Worker**: las comunicaciones `RA` entran en los envíos y consultas de ticket como los resúmenes.

## Boletas y notas de boletas: resumen con estado 3
- Una solicitud de baja con facturas y boletas produce un `RA` y un `RC` por fecha de emisión (máximo 500 líneas por archivo). El `RC` es un documento electrónico de tipo `RC` como cualquier resumen (mismo generador, firma, envío con `sendSummary` y seguimiento con `getStatus`), pero todas sus líneas llevan `ConditionCode` 3 y repiten los importes originales del comprobante (la hoja los exige).
- `cpe.summary_item.line_status` distingue 1 (informa el comprobante) de 3 (lo anula); el índice único de items activos pasa a `(tenant, documento, line_status)`, de modo que una boleta puede tener a la vez su item informado (aceptado) y uno de anulación pendiente, pero no dos anulaciones. `line_status` es inmutable por disparador.
- La boleta debe tener el CDR aceptado, es decir, ya informada en un resumen aceptado (si no, `SF-CPE-011`); el resumen ordinario ignora los items de estado 3 al decidir qué boletas faltan por informar.
- Un resumen de estado 3 rechazado libera solo sus líneas de anulación; el comprobante, ya aceptado, no se toca. Un resumen mixto (líneas 1 y 3) no se genera todavía: el generador lo admite, `VoidService` no lo produce.
- El motivo (3–100 caracteres) solo se guarda en nuestro registro: la línea del resumen no tiene campo de motivo.
- «Anulado» se deriva de un `RA` **o** `RC` aceptado con un item de estado 3.

## Verificado en el beta (2026-10-01)
Resumen de estado 3: una boleta enviada con `sendBill` y aceptada, y a continuación un resumen `RC` con esa boleta en estado 3: aceptado con código 0.

Una factura aceptada y su comunicación de baja: `sendSummary` devolvió ticket y `getStatus` el CDR en el primer intento con código 0 («La Comunicacion de baja RA-…, ha sido aceptada») y `ReferenceID` = identificador RA. El CDR trajo `cbc:IssueDate` **vacío**: ahora `CdrInfo.ReceivedDate/Time` pueden ser nulos.

## Límites
- Sin estado 2 (modificar) en resúmenes, sin baja de documentos de contingencia ni de otros tipos (25, 28, 30, 34, 42, 56).
- Billing no se entera: una factura anulada sigue existiendo (solo inserción) y puede recibir notas; falta bloquear notas sobre documentos anulados.
- El PDF de un documento anulado no lo indica todavía.
