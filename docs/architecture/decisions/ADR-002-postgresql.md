# ADR-002: PostgreSQL como base de datos transaccional

- Estado: Aceptada · Fecha: 2026-09-30

## Decisión
PostgreSQL (versión estable soportada; fijada en `docker-compose.yml`, objetivo 17.x o superior) con:

- **Un esquema por módulo**; sin claves foráneas entre esquemas de módulos.
- Row Level Security (ADR-003), JSONB para documentos originales/normalizados y metadatos, `numeric` para dinero.
- Migraciones EF Core por módulo, versionadas y revisables (el rol de aplicación no tiene DDL; las migraciones corren con un rol distinto).
- PITR (WAL archiving) y backups cifrados con pruebas de restauración (ver `docs/operations/backup-restore.md`).

## Consecuencias
+ RLS nativo, transacciones sólidas, `SELECT … FOR UPDATE`/`UPDATE … RETURNING` para numeración sin carreras.
+ Provider EF Core maduro (Npgsql).
− Operar HA y PITR requiere disciplina; en producción se prefiere un servicio administrado compatible.

## Alternativas descartadas
SQL Server (licencia, sin ventaja clave); bases por tenant (costo operativo con miles de tenants); NoSQL (integridad transaccional más débil para numeración y auditoría).
