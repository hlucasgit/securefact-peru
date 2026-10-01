# STATUS — 2026-10-01 (tras la hora de trabajo autónomo)

## Estado general
Fase 0 completa. **Fase 1 casi completa** (falta outbox, bus de mensajes y almacenamiento S3 de código; el CI no se ha ejecutado). **Fase 2 en curso**: ya existen el motor tributario, las series, la numeración atómica y la emisión idempotente de facturas/boletas. Último commit en `origin/main`: `3bfc54b`. El trabajo de esta hora está **sin commitear**.

## Módulos
`SharedKernel`, `Platform` (RLS, ámbito de datos, cifrado de envoltura), `Tenancy`, `Identity`, `Audit`, `Organizations`, **`TaxEngine`** (nuevo), **`Billing`** (nuevo), `Api`. Ver ADR-001…014.

## Novedades de esta sesión
- **TaxEngine** (`ITaxCalculator`, puro, solo `decimal`): valor de venta, IGV por línea y global, ISC (al valor y monto fijo), ICBPER, gratuitas (11–16, 21, 31–37), exoneradas, inafectas, exportación, IVAP, descuentos y cargos de línea y globales (afectan o no la base), redondeo del total, precio unitario con tributos. Las fórmulas salen de las reglas oficiales de validación de factura (libro del 26.08.2026) y los catálogos 05, 07, 08 y 53 transcritos de él. **ADR-013**.
- **Billing**: series validadas por tipo (F/B), numeración atómica en la misma transacción que el documento (sin huecos ni duplicados bajo concurrencia), `Idempotency-Key` con reintentos concurrentes, adquirente como instantánea, JSON original y cálculo completo guardados, `document`/`document_line` **insert-only** a nivel de base de datos. **ADR-014**.
- Permisos nuevos: `series.manage`, `documents.read`, `documents.create`.

## Pruebas: 193 pasan en Release con warnings-as-errors
98 unitarias (40 de TaxEngine con casos a mano y 3 000 documentos aleatorios), 6 de arquitectura, 6 de integración, 83 de seguridad/API contra PostgreSQL real. Hallazgo: la regla de arquitectura "Contracts solo referencia SharedKernel" era demasiado estricta; ahora permite referenciar otros Contracts. Cobertura de código no medida.

## Normativa
Nuevo: fórmulas de totales y tolerancias de la hoja de reglas (S16). Supuestos explícitos: **R-024** modo de redondeo (AwayFromZero), **R-025** unidad de la "tolerancia ± 1", **R-029** plazo de antigüedad de boletas. No soportado aún (error explícito `SF-TAX-002`): mezcla IGV+IVAP, ISC sistema 03, anticipos, percepciones/retenciones, detracciones.

## Riesgos y deuda
- Auditoría escrita después del cambio de negocio (ADR-012) hasta tener outbox.
- Catálogos transcritos en código (afectaciones, tributos); falta el módulo `Catalogs` que los importe del xlsx (requiere autorizar la descarga).
- `docs/regulatory/sources.md` S18 (guías XML) sin leer: QR, leyendas y estructura UBL dependen de ellas.
- Sin clientes/productos como entidades propias (el adquirente va en cada documento).
- Notas de crédito/débito esperan el flujo de CDR (Fase 4).

## Pendientes
1. Decisión del usuario: descargar xlsx/XSD/guías a `docs/regulatory/assets/`.
2. Commit y push.
3. `Catalogs` (importación versionada), `Customers`, `Products`, reglas con vigencia como servicio (`IRuleProvider`).
4. Cerrar Fase 1: outbox + `IMessageBus` (RabbitMQ) + `IObjectStorage` (S3) y ejecutar el CI.
5. Fase 3: leer guías XML, generador UBL, XSD, firma, CDR, QR, PDF.
