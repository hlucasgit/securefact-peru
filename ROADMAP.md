# Roadmap ejecutable

Cada fase tiene **entrada** (qué debe estar listo) y **salida** (criterio de aceptación verificable). Una funcionalidad solo cuenta como hecha con la Definition of Done del contexto maestro (§112): implementación, validación, autorización, auditoría, pruebas unitarias e integración, documentación, observabilidad, manejo de errores, revisión de seguridad, migración y documentación API.

| Fase | Nombre | Estado |
|------|--------|--------|
| 0 | Discovery | **Completada (2026-09-30)** — ver `STATUS.md` |
| 1 | Foundation | **En curso** — entorno + esqueleto |
| 2 | Core Billing | **Casi completa** — TaxEngine, series, numeración, idempotencia, catálogos, reglas, clientes y productos; faltan notas (esperan el CDR) |
| 3 | Motor CPE | **En curso** — QR, UBL, firma XMLDSig, ZIP, parser de CDR y canal SOAP (con simulador) hechos; almacén de certificados, credenciales SOL, máquina de estados, tubería, worker, resumen diario, outbox, PDF y notas de facturas hechos y aceptados en el beta; bajas de facturas y boletas hechas; descuentos, cargos y crédito con cuotas hechos |
| 4 | Integración | Pendiente |
| 5 | MVP comercial | Pendiente |
| 6 | White label / Resellers | En curso: revendedores (ADR-043) y marca blanca de la interfaz (ADR-044) hechos, y los dominios se verifican por DNS (ADR-051); el correo saliente existe (recuperación de contraseña, ADR-052; avisos de cuenta, dominio y negocio con cola de reintentos, ADR-054 y ADR-055); precios, cargos, comisiones y suspensión por mora hechos (ADR-062 a ADR-064); la plataforma factura lo que cobra (ADR-065); faltan plantillas por revendedor y la pasarela de cobro |
| 7 | Tributario avanzado | Pendiente |
| 8 | SecureFact PSE | Pendiente (decisión comercial) |
| 9 | Tax Platform | Pendiente |

## Fase 0 — Discovery ✔
Entregables: `docs/regulatory/{sources,current-baseline,matrix}.md`, `docs/architecture/{architecture-analysis,c4,erd}.md`, ADR-001…010, `docs/pse/*`, este roadmap.

## Fase 1 — Foundation
**Entrada**: Fase 0.
Orden de trabajo (incremental, cada paso con pruebas):
1. Entorno de desarrollo: `docker compose` (postgres, redis, rabbitmq, minio), `.env.example`, `Directory.Build.props`, gestión central de paquetes.
2. Esqueleto: `SharedKernel`, `Api` (health, ProblemDetails RFC 9457, correlation ID, OpenAPI), `Workers`.
3. Pruebas de arquitectura (NetArchTest): límites de módulos y capas.
4. ✔ Logging estructurado + OpenTelemetry (trazas, métricas, logs) con saneamiento.
5. ✔ **Tenancy**: `ITenantContext`, RLS + filtros EF + pruebas cross-tenant (resolución desde credenciales llega con Identity).
6. ✔ **Identity/RBAC**: usuarios, roles, permisos explícitos, sesiones, MFA, bloqueo, revocación.
7. ✔ **Audit**: append-only con cadena de hash.
8. ✔ **Organizations**: empresas (RUC), establecimientos.
9. ✔ Outbox + `IMessageBus` (RabbitMQ) + `IObjectStorage` (S3; SeaweedFS en desarrollo, ADR-036).
10. CI (build, tests, escaneos), SBOM, Dependabot/Renovate, gitleaks.

**Salida**: `docker compose up` levanta todo; un usuario crea tenant → empresa → establecimiento vía API; las pruebas cross-tenant, de arquitectura e integración pasan en CI; todo cambio queda auditado. **Sin XML todavía.**

## Fase 2 — Core Billing
Catálogos (tras leer catálogos oficiales: bloqueo R-016), clientes, productos, series/numeración transaccional, documentos y líneas, TaxEngine (gravadas, exoneradas, inafectas, gratuitas, descuentos, ISC, ICBPER, redondeo con `decimal`), factura/boleta/NC/ND en dominio, idempotencia. **Salida**: cálculos cubiertos por pruebas exhaustivas y de propiedades; numeración sin carreras bajo concurrencia (prueba de carga).

## Fase 3 — Motor CPE
Modelo canónico, generador UBL 2.1, validación XSD y reglas SUNAT, firma XMLDSig, ZIP, parser de CDR, QR, PDF. Desbloqueo previo: reglas de validación y XSD descargados y versionados (R-016/R-017/R-018). **Salida**: golden files (invoice-basic, receipt-basic, credit-note, debit-note) válidos contra XSD y reglas; firma verificable.

## Fase 4 — Integración
Simulador SUNAT, `ICpeSubmissionChannel` + adapter inicial elegido por ADR (SUNAT directo / PSE / OSE), colas, workers, reintentos, circuit breaker, máquina de estados, webhooks. **Salida**: flujo end-to-end (§126) en Sandbox y, tras aprobación explícita, en beta de SUNAT.

## Fase 5 — MVP comercial
**En curso**: primera interfaz web (`web/`, ADR-038): ingreso con segundo factor, empresas, certificado y SOL, series, clientes, productos, emisión de facturas y boletas (con ISC e ICBPER), notas, seguimiento y envío, baja, resumen diario, usuarios y reglas. **Administración de plataforma hecha (ADR-041)**: inquilinos (lista, alta con propietario, suspender, reactivar y cerrar con motivo y auditoría), usuarios por cuenta, auditoría con verificación de la cadena y mensajes fallidos. **Planes y consumo hechos (ADR-042)**: límites de empresas, usuarios y comprobantes por mes, asignación por la plataforma y consumo visible. Pendiente de la fase: búsqueda avanzada, importación masiva, portal de desarrollador y sandbox público (la API, sus llaves, los webhooks y su guía ya están); pasarela de cobro (precios, cargos y su comprobante hechos, ADR-062 y ADR-065).
Dashboard, emisión web, búsqueda, descarga, importación masiva, usuarios, planes básicos, metering, API pública estable + OpenAPI + colección Bruno (hechos, ADR-066), llaves de API y webhooks (hechos, ADR-066 y ADR-067), portal de desarrollador, sandbox. **Salida**: primeros clientes reales.

## Fase 6 — White label / Resellers
Resellers, branding, dominios, precios (ADR-062), comisiones (ADR-063), subcuentas, consumo, plantillas.

## Fase 7 — Tributario avanzado
Detracciones, retenciones, percepciones, exportaciones, GRE (bounded context independiente; el remitente `09` está hecho contra el simulador, ADR-056; el transportista `31` también, ADR-057; los motivos 08, 09, 18 y 19 también, ADR-059; el traslado total de bienes del transportista también, ADR-060; falta probar con SUNAT; la guía por evento no se emite desde un sistema propio, ADR-061; la representación impresa de las guías está hecha, ADR-058, salvo el QR real), conciliaciones. Cada pieza empieza leyendo su fuente.

## Fase 8 — SecureFact PSE
Solo cuando comercialmente corresponda: ejecutar `docs/pse/gap-analysis.md` contra la norma vigente; capital/activos ≥ 150 UIT, ≥ 5 trabajadores, ISO/IEC 27001, soporte de primer nivel, KPI de rechazo ≤ 10 % → 5 %. Nunca afirmar ser PSE antes de la inscripción oficial.

## Fase 9 — Tax Platform
SIRE (RVIE/RCE), factoring, cobranzas, analytics, IA (SecureFact Insights), conciliación.
