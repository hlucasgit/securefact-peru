# STATUS — sesión 1 (2026-09-30)

## Estado general
Fase 0 (Discovery) **completada**. Fase 1 (Foundation) **iniciada**: esqueleto compilable y probado, infraestructura local definida. Nada tributario implementado todavía (intencional). Código **sin commitear** (pendiente de tu orden).

## Arquitectura definida
Monolito modular .NET 10 + PostgreSQL (RLS) + outbox/RabbitMQ + object storage S3 + canales `ICpeSubmissionChannel`. Ver `docs/architecture/` (análisis, C4, ERD, ADR-001…010).

## Normativa revisada (fuentes primarias SUNAT, leídas con navegador real; 403 a clientes automáticos)
Detalle completo en `docs/regulatory/sources.md` y `current-baseline.md`. Resumen:
- **Confirmado**: requisitos PSE del contexto (150 UIT = capital SUNARP o casillero 390, ≥5 trabajadores, ISO 27001, etc.); tasa de rechazo PSE ≤10 %→5 %; plazo de envío de factura 3 días calendario; CDR (aceptada / con observación / rechazada); SOAP `sendBill/sendSummary/sendPack/getStatus` con WS-Security (Clave SOL secundaria); receptor único `e-factura.sunat.gob.pe`; rangos de error 0100–999 / 1000–1999 / 2000–3999 / 4000+; GRE por REST/OAuth2 y **no** vía OSE; con PSE se firma con certificado del PSE.
- **Hallazgos que corrigen supuestos**: Manual del Programador es de **mayo 2021**; reglas de validación vigentes 26.08.2026 (CPE) y 25.09.2026 (GRE); RS 049-2026 (PEI Web) no afecta al core; discrepancia sobre homologación PSE (RS 108-2022) sin resolver; UIT 2026 = S/5,500 sin verificar.
- **Pendientes bloqueantes** (no se implementa hasta leerlos): catálogos + parámetro 742, reglas de validación xlsx y XSD, flujo vigente de boletas, contenido del QR, detracciones/retenciones/percepciones, Manual URL-GRE, SIRE, Ley 29733.

## Módulos / código creado
`SharedKernel` (Result/Error, `Ruc`, `Series`, `DocumentNumber`, `TenantId`, `ITenantContext`, `ErrorCodes`), `Api` (health live/ready, Problem Details RFC 9457 con `code/traceId/correlationId`, correlation ID seguro, security headers, OpenAPI en dev), `Workers` (host vacío). Aún **no** hay módulos de negocio (Tenancy, Identity, Audit, Organizations son el siguiente paso).

## Pruebas
31 pasando: 20 unitarias, 5 de arquitectura (límites de módulos, SharedKernel sin dependencias, sin `double/float` monetario), 6 de integración de la API. Cobertura **no medida**. Build limpio sin warnings; `restore --locked-mode` OK; sin paquetes vulnerables.

## Entorno
`docker-compose.yml` (postgres 17, redis 7, rabbitmq 4, SeaweedFS S3, `s3-init`, api, workers), `.env.example`, rol de BD de aplicación sin `BYPASSRLS` (verificado: `securefact_app` bypassrls=f). CI en `.github/workflows/ci.yml` (restore bloqueado, build, pruebas, vulnerabilidades, SBOM, gitleaks, Trivy) — **no ejecutado todavía**.

## Riesgos y desvíos
- **MinIO no disponible**: imágenes comunitarias respondieron pull denied/401. Se usa SeaweedFS en dev (ADR-005, adenda).
- **Docker Desktop dejó de responder (HTTP 500)** durante el build de las imágenes `api`/`workers`: los Dockerfiles **no están verificados**. Infra (postgres/redis/rabbitmq/s3) sí se levantó sana antes del fallo. Los puertos 5432/6379/8080 estaban ocupados por otro proyecto; se usan 55432/56379/58000/5180. Rango 59000–59114 está reservado por Windows.
- Licencias: se evita FluentAssertions 8 (comercial) y se difiere la elección de framework de mensajería.
- Fuentes SUNAT inaccesibles por herramientas automáticas: la revisión regulatoria requiere navegador.

## Deuda técnica
Sin Testcontainers aún; `AllowedHosts: *` por restringir; CSP/HSTS básicos; CI sin probar; lock files de `bin/` ignorados por `.gitignore`.

## Pendientes inmediatos (siguiente sesión)
1. Reiniciar Docker y verificar `docker compose up --build` completo.
2. Módulos: **Tenancy + RLS + pruebas cross-tenant** → Identity/RBAC → Audit → Organizations (empresas/establecimientos).
3. OpenTelemetry y logging estructurado con saneamiento; outbox + `IMessageBus` + `IObjectStorage`.
4. Primer commit y ejecución de CI.
5. Descargar y versionar (hash) reglas de validación y XSD antes de la Fase 3.

## Siguiente fase
Completar Fase 1; luego Fase 2 (Core Billing) cuando los catálogos oficiales estén leídos.
