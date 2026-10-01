# ADR-008: Reglas regulatorias como datos versionados

- Estado: Aceptada · Fecha: 2026-09-30

## Contexto
SUNAT cambia reglas con frecuencia (reglas de validación actualizadas el 26.08.2026; manual del programador de 2021 aún vigente en partes). Plazos, umbrales, endpoints y catálogos no deben estar dispersos en `if`.

## Decisión
- Patrón `Rule { Code, Version, EffectiveFrom, EffectiveTo, Configuration (JSON), Source }`, evaluado **a la fecha tributaria del documento** (no a la fecha actual) para que los documentos históricos sigan evaluándose con la regla que les correspondía.
- `IRuleProvider.Resolve(code, asOfDate)` devuelve exactamente una versión activa o falla de forma explícita.
- Políticas centralizadas y probadas (`InvoiceSubmissionDeadlinePolicy`, `CreditNoteOriginPolicy`, …); prohibido `if (days > 3)` en el código.
- Catálogos con `Code, Description, EffectiveFrom, EffectiveTo, Version, Source, Active, Metadata`.
- **Nunca se reemplaza silenciosamente una regla**: un cambio crea una versión nueva con vigencia; flujo `Detect → Analyze → Regulatory change → Implement version → Test → Stage → Activate → Monitor`.
- Cada regla referencia su fuente en `docs/regulatory/sources.md` (ID `Sxx`) y su fila en `matrix.md`.
- Las reglas se siembran desde archivos versionados del repositorio y se cargan con migraciones/seed idempotentes.

## Consecuencias
+ Cambios normativos sin reescribir módulos; auditabilidad histórica.
− Más piezas que un `if`; se compensa con pruebas de tabla por regla.
