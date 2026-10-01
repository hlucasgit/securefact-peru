# ADR-024: Comunicación de baja

- Estado: Aceptada · Fecha: 2026-10-01

## Fuentes
Hoja `Comunicación de Baja1_0` de las reglas de validación del 26.08.2026 (S16), XSD `UBLPE-VoidedDocuments-1.0` (S17), Manual del programador (S04: nombre `RUC-RA-AAAAMMDD-correlativo`, `sendSummary` + `getStatus`) y la prueba contra el beta del 2026-10-01.

## Decisión
- **Alcance**: facturas (01) y notas (07/08) **de facturas** (serie `F***`, regla 2310). Las boletas y sus notas se anulan por el resumen diario con estado 3 («anulado»), que aún no se implementa.
- **Generador** (`IVoidedDocumentsGenerator`): UBL 2.0 `VoidedDocuments` (`CustomizationID` 1.0), orden del XSD, identificador `RA-AAAAMMDD-n` con la **fecha de generación** (reglas 2220/2346), `ReferenceDate` = fecha de emisión de los documentos, una línea por documento con tipo, serie, número y motivo (3–100 caracteres, sin saltos de línea). Valida contra el XSD y contiene todas las etiquetas obligatorias de la hoja. Máximo 500 líneas por archivo (suposición R-046, igual que el resumen).
- **Creación** (`POST /api/v1/voids`, permiso `cpe.send`): recibe documentos de Billing con su motivo y crea **una comunicación por fecha de emisión** (regla 2375: una sola fecha por archivo). Cada documento debe tener el CDR aceptado (regla 2105; rechazado = 2398), no estar ya anulado ni en una comunicación pendiente (2323; índice único de item activo), no repetirse (2348) y tener **como máximo 7 días** desde su emisión (2957). Todo o nada: un documento inválido rechaza la solicitud (`SF-CPE-011`).
- **Documento electrónico** tipo `RA`: se firma, se envía con `sendSummary` y se sigue con `getStatus` como el resumen (ADR-020); el CDR solo se acepta si su `ReferenceID` es el identificador `RA-…` y el RUC es el del contribuyente.
- **Los documentos anulados no cambian de estado**: un documento con CDR final es inmutable (ADR-019). «Anulado» se deriva: lo cubre una comunicación **aceptada**; `ElectronicDocumentDto.Voided` lo expone. Una comunicación rechazada libera sus documentos para volver a anularlos (nuevo correlativo); una fallida los mantiene reservados hasta reintentar.
- **Base de datos**: los items reutilizan `cpe.summary_item` con el motivo (inmutable por disparador).
- **Worker**: las comunicaciones `RA` entran en los envíos y consultas de ticket como los resúmenes.

## Verificado en el beta (2026-10-01)
Una factura aceptada y su comunicación de baja: `sendSummary` devolvió ticket y `getStatus` el CDR en el primer intento con código 0 («La Comunicacion de baja RA-…, ha sido aceptada») y `ReferenceID` = identificador RA. El CDR trajo `cbc:IssueDate` **vacío**: ahora `CdrInfo.ReceivedDate/Time` pueden ser nulos.

## Límites
- Sin baja de boletas/notas de boletas (resumen con estado 3), sin baja de documentos de contingencia ni de otros tipos (25, 28, 30, 34, 42, 56).
- Billing no se entera: una factura anulada sigue existiendo (solo inserción) y puede recibir notas; falta bloquear notas sobre documentos anulados.
- El PDF de un documento anulado no lo indica todavía.
