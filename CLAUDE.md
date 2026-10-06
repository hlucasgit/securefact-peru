# CLAUDE.md — guía para agentes en SecureFact Perú

Plataforma SaaS de facturación electrónica peruana (monolito modular .NET 10 + PostgreSQL + React). Contexto completo y decisiones: `docs/architecture/` (ADR-001…010) y `docs/regulatory/`.

## Reglas inquebrantables
1. **No inventar normativa.** Antes de codificar una regla tributaria/endpoint/XML/catálogo, consultar fuente oficial SUNAT, registrarla en `docs/regulatory/sources.md` y en `matrix.md`. Si no se puede verificar: crear la abstracción, documentar el pendiente, no asumir. `cpe.sunat.gob.pe` bloquea clientes automáticos (403): leer con navegador real.
2. **Reglas como datos versionados** (ADR-008): nada de `if (days > 3)`.
3. **Multitenancy**: toda tabla de negocio lleva `tenant_id` + RLS (ADR-003). Cada PR que toque datos incluye pruebas cross-tenant.
4. **Dinero**: solo `decimal`/`numeric`; nunca `float/double`. Numeración: nunca `MAX()+1`.
5. **Secretos**: nunca en claro, ni en logs, ni en el repo (ADR-007). Sin PFX/`.env` versionados.
6. **No usar el beta de SUNAT para estrés**; usar el simulador (`Sunat:Environment=Sandbox`, ADR-039; se rechaza en producción). En Local/Test solo `SandboxChannel`.
7. **No afirmar ser PSE.** `OwnPseMode=false`.
8. Documentos aceptados y CDR son **inmutables**.
9. No `catch (Exception) {}` vacíos, no TODO críticos, no código comentado, `CancellationToken` siempre, nullable habilitado.

## Estructura
`web/` (interfaz React, ADR-038; no calcula impuestos), `src/SecureFact.SharedKernel`, `src/SecureFact.Api`, `src/SecureFact.Workers`, `src/Modules/<Módulo>/{SecureFact.<Módulo>,SecureFact.<Módulo>.Contracts}`, `tests/*`. Un módulo solo referencia `SharedKernel` y `Contracts` de otros (lo verifican `tests/Architecture`).

## Comandos
```bash
docker compose up -d
dotnet build SecureFact.slnx
dotnet build SecureFact.slnx -c Release   # como el CI: en Release las advertencias son errores
dotnet test SecureFact.slnx
(cd web && npm ci && npm run lint && npm run typecheck && npm test && npm run build)   # interfaz web, como el trabajo `web` del CI
(cd web && npm run e2e)   # extremo a extremo (Playwright): pide la API con Sunat__Environment=Sandbox y los workers; ver web/README.md y ADR-040
# cobertura: ver docs/testing/README.md (coverage.runsettings; el CI exige 95 % de líneas y 84 % de ramas)
```

## Estilo
Código en inglés (identificadores), documentación en español. Errores con códigos estables `SF-<ÁREA>-nnn` y Problem Details RFC 9457. Definition of Done: `ROADMAP.md`.
