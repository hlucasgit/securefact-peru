# ADR-014: Numeración atómica, idempotencia e inmutabilidad de documentos

- Estado: Aceptada · Fecha: 2026-10-01

## Decisión
- **Numeración**: el contador vive en `billing.series.last_number` y solo avanza con `UPDATE … SET last_number = last_number + 1 … RETURNING` **dentro de la misma transacción** que inserta el documento. Si la transacción falla, el número no se consume (sin huecos); si dos transacciones compiten, PostgreSQL serializa el bloqueo de fila. Nunca `MAX()+1`. Restricción `CHECK (0 ≤ last_number ≤ 99 999 999)` y tope por `WHERE last_number < 99999999`. Una factura *rechazada por SUNAT* sí consume su número (R-005): eso ocurre en la fase de envío, no aquí.
- **Idempotencia** (`Idempotency-Key`, 8–100 caracteres): se inserta primero `billing.idempotency_key (tenant_id, key, request_hash, document_id)` con `ON CONFLICT DO NOTHING`; un duplicado concurrente se bloquea hasta que termine la primera transacción y luego **repite la respuesta original**. Misma clave con otro contenido → `409 SF-BIL-008`. El hash es SHA-256 del JSON de la solicitud. Las claves son por tenant.
- **Inmutabilidad**: `document`, `document_line` e `idempotency_key` son *insert-only* (el rol de aplicación no tiene `UPDATE/DELETE`); solo `series` admite `UPDATE` (el contador). Los cambios de estado fiscal vivirán en tablas de eventos (`CPE`), no editando el documento. Se guarda el JSON original, el cálculo completo del TaxEngine y los datos del adquirente como instantánea.
- **Reglas versionadas**: formato de serie por tipo (S16 *General*, código 0151: facturas `F…`, boletas `B…`, notas `F`/`B`; las series numéricas de contingencia no se soportan) y antigüedad máxima de la fecha de emisión (3 días, S02) viven en `BillingRules` con su fuente; la fecha tributaria se evalúa en la zona horaria de la empresa.
- Por ahora solo se emiten facturas (01) y boletas (03). Notas de crédito/débito (07/08) requieren el CDR del documento origen (Fase 4).

## Verificación
83 pruebas de seguridad/API contra PostgreSQL real, entre ellas 40 solicitudes concurrentes que producen exactamente 1…40, 12 reintentos concurrentes con una clave que crean un solo documento, y `UPDATE/DELETE` denegados al rol de aplicación.
