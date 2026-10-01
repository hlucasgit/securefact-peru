# Política de seguridad

## Reporte de vulnerabilidades
Escribir a **security@securefact.invalid** (dirección provisional; reemplazar al definir el dominio). No abrir issues públicos con detalles de vulnerabilidades. Objetivo de acuse: 2 días hábiles.

## Alcance y severidad
Una fuga de datos entre tenants, exposición de certificados/claves SOL/API keys o alteración de CDR/XML aceptados se clasifica **crítica**.

## Prácticas
OWASP ASVS como referencia; secretos con cifrado de envoltura (ADR-007); RLS por tenant (ADR-003); auditoría append-only; escaneo de dependencias, contenedores y secretos en CI; SBOM por release. Ver `docs/security/`.
