# SecureFact Perú

Plataforma SaaS multiempresa de facturación electrónica, API tributaria e infraestructura CPE para Perú. Diseñada para operar hoy mediante SUNAT, PSE u OSE externos y evolucionar a PSE acreditado.

> **SecureFact no es PSE.** No se debe afirmar lo contrario hasta tener inscripción oficial en el registro de SUNAT (ver `docs/pse/`).

## Estado
Fase 1 completa; backend de facturación electrónica y primera interfaz web (MVP) en marcha. Ver [STATUS.md](STATUS.md) y [ROADMAP.md](ROADMAP.md).

## Documentación
- Normativa: [`docs/regulatory/`](docs/regulatory/) — fuentes, baseline vigente, matriz.
- Arquitectura: [`docs/architecture/`](docs/architecture/) — análisis, C4, ERD, ADRs.
- PSE futuro: [`docs/pse/`](docs/pse/).
- Contexto para agentes: [CLAUDE.md](CLAUDE.md).

## Requisitos de desarrollo
.NET SDK 10, Node 24 (interfaz web, `web/`), Docker.

## Arranque local
```bash
cp .env.example .env        # completar SF_JWT_SIGNING_KEY y SF_LOCAL_DEV_KEK (openssl rand -base64 48 / 32)
docker compose up -d --build   # postgres, redis, rabbitmq, S3 local, migraciones, api, workers
docker compose run --rm -e SF_BOOTSTRAP_ADMIN_EMAIL=admin@ejemplo.local -e SF_BOOTSTRAP_ADMIN_PASSWORD='<frase larga>' api bootstrap-platform-admin
dotnet test SecureFact.slnx     # las pruebas de seguridad usan Testcontainers (requieren Docker)
```
Interfaz web: `http://localhost:5173` (en desarrollo, `cd web && npm ci && npm run dev`; ver [web/README.md](web/README.md)).
API local: `http://localhost:5180` (health: `/health/live`, `/health/ready`; OpenAPI en desarrollo: `/openapi/v1.json`).
Observabilidad opcional: `docker compose --profile observability up -d` y `SF_OTLP_ENDPOINT=http://otel:4317`.

## Licencia
Propietario. Todos los derechos reservados.
