# ADR-020: Resumen diario de boletas

- Estado: Aceptada · Fecha: 2026-10-01

## Fuentes
Manual del programador (S04: `sendSummary`, `getStatus`, nombre `RUC-RC-AAAAMMDD-correlativo`, bloques de 500 líneas), guía del resumen diario v2 de enero de 2018 (S18, ejemplos), XSD oficial `UBLPE-SummaryDocuments-1.0` (S17) y hoja `Resumen Diario1_1` de las reglas de validación del 26.08.2026 (S16, la más reciente y la que prevalece).

## Decisión
- **Generador** (`ISummaryDocumentGenerator`): UBL 2.0 `SummaryDocuments` (`CustomizationID` 1.1), orden de elementos del XSD, una línea por boleta con estado 1 («adicionar»), importe total, `BillingPayment` 01/02/03 solo cuando corresponde, IGV siempre (también en cero) con su tasa (`cbc:Percent`, regla 2992) y adquirente solo si hay documento (boletas de más de S/ 700 exigen identificarlo, regla 2514). Máximo 500 líneas. Valida contra el XSD oficial y contiene todas las etiquetas obligatorias de la hoja (la prueba lee el libro). Fuera de alcance, con `SF-CPE-002` o ausente del modelo: operaciones gratuitas/exportación, ISC, ICBPER, cargos, percepción, notas, modificaciones y bajas (estado 3).
- **Resumen como documento electrónico** (tipo `RC`): se firma, se guarda, se envía con `sendSummary` (ticket) y se consulta con `getStatus`; reutiliza la máquina de estados, el historial, los reintentos y los disparadores de inmutabilidad (ADR-017/019). El correlativo sale del conteo de resúmenes del día de la empresa; los índices únicos (`file_base_name`, boleta activa) resuelven las carreras.
- **Las boletas siguen a su resumen**: cada boleta enlazada (`summary_item`) refleja los eventos del resumen (envío, ticket, CDR, fallas, reintento manual). **Un resumen rechazado no juzga las boletas**: vuelven a `ReadyToSend` y sus enlaces se liberan para informarlas en un resumen nuevo (`ReturnedToQueue`). Una boleta pertenece como máximo a un resumen activo (índice único parcial).
- **Consulta del ticket** (`POST …/poll`): `98` sigue esperando, `0`/`99` traen el CDR, que solo se acepta si su `ReferenceID` es el identificador del resumen y el RUC es el del contribuyente; si SUNAT no responde en 24 h el resumen pasa a `Failed`.
- Las boletas individuales **no** se envían con `sendBill` (R-039).

## Supuestos por confirmar en el beta (R-040)
- **Fecha del nombre y del identificador**: la hoja de reglas exige que `IssueDate` (fecha de generación) coincida con la fecha del nombre del archivo (reglas 2220 y 2346); la guía y el manual sugieren la fecha de emisión de las boletas. Se usa la **fecha de generación**; ambas coinciden cuando el resumen se genera el mismo día.
- El `ReferenceID` del CDR de un resumen es el identificador `RC-AAAAMMDD-n`.
- La zona horaria de «hoy» es la de la empresa (por defecto `America/Lima`).
