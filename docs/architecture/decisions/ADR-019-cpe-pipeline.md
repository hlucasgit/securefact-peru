# ADR-019: Tubería del documento electrónico

- Estado: Aceptada · Fecha: 2026-10-01

## Decisión
`IElectronicDocumentService` (módulo `CpeEngine`, esquema `cpe`) convierte un documento numerado de Billing en un documento electrónico firmado y lo envía a SUNAT. Cada paso es una llamada explícita (hoy por API; el worker las invocará después).

1. **Preparar** (`POST /api/v1/documents/{id}/electronic`): lee el documento (`IDocumentService`), la empresa (`ICompanyAdministration`), la tasa de IGV de las reglas por fecha de emisión (`IRuleProvider`), genera el UBL, lo firma con el certificado activo de la empresa (`ICertificateProvider`), valida el nombre del paquete y guarda el resultado. Idempotente: un documento se prepara una vez (índice único `(tenant_id, document_id)`; una carrera devuelve el ganador). Si falta el certificado, no se guarda nada (`SF-CRT-004`).
2. **Enviar** (`POST /api/v1/electronic-documents/{id}/send`): solo facturas (01). Obtiene las credenciales SOL (`ISolCredentialProvider`), **reclama** el documento (`ReadyToSend → Sending`, guardado con token de concurrencia antes de llamar a SUNAT), llama a `sendBill` y aplica la respuesta con la máquina de estados (ADR-017). Dos envíos simultáneos llegan a SUNAT una sola vez (el segundo recibe `SF-CPE-008`). Faltar credenciales o canal no consume un intento.
3. **CDR**: se interpreta con `ICdrParser`. **Solo se acepta un CDR que corresponda al documento** (`SERIE-NÚMERO` y RUC del contribuyente); si no coincide o no se puede leer, el documento pasa a `Failed` (nunca a aceptado), se conservan los bytes recibidos y se registra `SF-CPE-006`/`SF-CPE-003`.
4. **Fallas**: del canal, reintentables (red, 5xx, lado `Server`, rango 0100–0999) → vuelve a `ReadyToSend` con espera exponencial (30 s, 60 s, … tope 1 h); no reintentables (1000–1999, 4xx) → `Failed`. Tras 5 envíos → `Failed`. Un operador usa `retry` (`Failed → ReadyToSend`, intentos a cero). Si el llamador cancela durante el envío, el documento vuelve a la cola (resultado desconocido, nunca estado final).
5. **Inmutabilidad en la base de datos** (disparadores): el XML firmado, su resumen y sus claves no cambian nunca; un documento con respuesta final (aceptado, aceptado con observaciones, rechazado) no admite ningún `UPDATE` ni `DELETE`, ni siquiera del dueño del esquema; el historial de eventos es de solo inserción. El rol de la aplicación no tiene `DELETE`.
6. **Historial** (`GET …/events`): cada transición queda con estado origen y destino, evento, intento y detalle. **Auditoría** de preparación, resultado final, falla y reintento (sin secretos).

## Seguridad
- Permiso `cpe.send` (TenantOwner, TenantAdmin, BillingAdmin) para preparar, enviar y reintentar; la lectura usa `documents.read`. RLS por tenant en ambas tablas (la tabla principal admite el ámbito de plataforma para el futuro worker).
- El endpoint de SUNAT debe nombrarse (`Sunat:Environment` = `Beta` o `Production`); sin él los documentos se preparan y firman pero no se envían (`SF-CPE-005`). El beta no se usa para pruebas de carga.
- Las pruebas usan un simulador del canal en proceso; nada sale a la red.

## Límites conocidos
- **Boletas**: se preparan pero no se envían; se informan en el resumen diario (`sendSummary`), pendiente.
- **Resultado desconocido**: un proceso que muera en `Sending` deja el documento en ese estado; falta el detector de documentos atascados y la consulta `getStatusCdr` (Fase 4).
- El UBL no incluye hora de emisión ni valor de referencia de operaciones gratuitas (ADR-016); los descuentos y cargos de facturas y boletas sí (ADR-025). El XML y el CDR se guardan en la base de datos hasta que exista `IObjectStorage` (S3).
- Falta el worker que reintente por `next_attempt_at` y consulte tickets.
