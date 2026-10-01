# Contribuir

1. Rama desde `main`; commits en formato Conventional Commits (`feat:`, `fix:`, `docs:`…).
2. Todo cambio pasa por PR con CI verde: build, pruebas unitarias, de arquitectura, de integración y escaneos.
3. Decisión arquitectónica nueva → ADR en `docs/architecture/decisions/`.
4. Cambio con impacto normativo → actualizar `docs/regulatory/sources.md` y `matrix.md` con la fuente oficial **antes** de implementar.
5. Definition of Done en `ROADMAP.md` (implementación, validación, autorización, auditoría, pruebas, documentación, observabilidad, errores, seguridad, migración, API docs).
6. Prohibido: secretos en el repo, `float/double` para dinero, `MAX()+1`, mezclar tenants, modificar CDR/XML aceptado, depender de scraping no autorizado.
7. Migraciones EF Core versionadas, nunca cambios manuales de esquema en producción.
