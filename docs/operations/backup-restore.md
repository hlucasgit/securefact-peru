# Backup y restauración (borrador)

Objetivos iniciales (a validar con el negocio): **RPO ≤ 5 min** (PITR con WAL archiving), **RTO ≤ 4 h**. Backups cifrados; object storage con versioning (y Object Lock en producción para XML firmado y CDR); retención según obligaciones de conservación (verificar plazo: pendiente en `docs/regulatory/sources.md`).

Un backup no restaurado no cuenta: restauración de prueba trimestral documentada como evidencia (`docs/pse/evidence-register.md`).
