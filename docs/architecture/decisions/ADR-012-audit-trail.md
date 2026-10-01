# ADR-012: Auditoría inmutable con cadena de hashes

- Estado: Aceptada · Fecha: 2026-10-01

## Decisión
- `audit.audit_event` es **append-only**: el rol de aplicación solo tiene `SELECT, INSERT`; además triggers `BEFORE UPDATE/DELETE/TRUNCATE` lanzan `42501` incluso para el dueño del esquema (quitarlos exige DDL explícito y deja huella).
- **Cadena de hashes por tenant** (y una de plataforma): cada evento guarda `seq`, `prev_hash` y `hash = SHA-256(JSON[id, cadena, seq, ticks UTC, actor, acción, entidad, valores previos/nuevos, IP, user-agent, correlación, request, prev_hash])`. Los escritores de una cadena se serializan con `pg_advisory_xact_lock`, sin huecos ni bifurcaciones. `IAuditQuery.VerifyAsync` recorre la cadena y localiza la primera modificación, eliminación o enlace roto.
- Los valores se guardan como **texto JSON canónico** (claves ordenadas) para poder re-hashear exactamente lo que se firmó; los instantes se truncan a microsegundos (precisión de `timestamptz`).
- Redacción defensiva de nombres de propiedad sensibles (`password`, `token`, `secret`, `apiKey`, `pfx`, `clave`, `hash`, …) y reglas de que los llamadores no pasen secretos.
- Visibilidad: RLS `TenantOrPlatform`; cada tenant solo lee su cadena. Endpoints `GET /api/v1/audit` y `POST /api/v1/audit/verify` con `audit.read`.

## Limitaciones conocidas
- El evento se escribe **después** de confirmar el cambio de negocio (no en la misma transacción): un fallo entre ambos puede perder el evento. Se resolverá con outbox transaccional (ADR-004) cuando exista, publicando el evento en la misma transacción.
- La cadena detecta manipulación, no la impide a un atacante con acceso total a la BD que recalcule toda la cadena; el anclaje periódico del último hash en almacenamiento externo (Object Lock) queda como mejora.
