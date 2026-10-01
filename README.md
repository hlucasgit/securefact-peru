# SecureFact Perú

Plataforma SaaS multiempresa de facturación electrónica, API tributaria e infraestructura CPE para Perú. Diseñada para operar hoy mediante SUNAT, PSE u OSE externos y evolucionar a PSE acreditado.

> **SecureFact no es PSE.** No se debe afirmar lo contrario hasta tener inscripción oficial en el registro de SUNAT (ver `docs/pse/`).

## Estado
Fase 1 (Foundation) en curso. Ver [STATUS.md](STATUS.md) y [ROADMAP.md](ROADMAP.md).

## Documentación
- Normativa: [`docs/regulatory/`](docs/regulatory/) — fuentes, baseline vigente, matriz.
- Arquitectura: [`docs/architecture/`](docs/architecture/) — análisis, C4, ERD, ADRs.
- PSE futuro: [`docs/pse/`](docs/pse/).
- Contexto para agentes: [CLAUDE.md](CLAUDE.md).

## Requisitos de desarrollo
.NET SDK 10, Node 24 (frontend, más adelante), Docker.

## Arranque local
```bash
cp .env.example .env        # ajustar valores locales (nunca commitear .env)
docker compose up -d        # postgres, redis, rabbitmq, minio
dotnet build SecureFact.slnx
dotnet test SecureFact.slnx
dotnet run --project src/SecureFact.Api
```
API local: `http://localhost:5180` (health: `/health/live`, `/health/ready`).

## Licencia
Propietario. Todos los derechos reservados.
