# ADR-009: Stack de frontend

- Estado: Aceptada (revisable al iniciar el frontend) · Fecha: 2026-09-30

## Decisión
React + TypeScript (strict) + Vite, arquitectura **por features** (`src/features/<feature>/{api,components,hooks,routes}`), TanStack Query para estado de servidor, React Router, validación con Zod, i18n `es-PE`. Biblioteca UI: **Mantine** (MIT, mantenida, accesible, buenos componentes de tabla/formulario/fechas) en lugar de construir primitivas; se confirmará su versión y accesibilidad en una prueba de concepto al iniciar el frontend. El cliente HTTP se genera desde el OpenAPI del backend.

Frontends separados por audiencia dentro de un mismo repositorio de SPA o apps distintas según crezca: portal cliente, portal desarrollador, backoffice (independiente del portal cliente, contexto §63).

## Consecuencias
+ Productividad y accesibilidad por defecto. − Acoplamiento a una biblioteca; mitigado por componentes envoltorio propios en `shared/ui`.
