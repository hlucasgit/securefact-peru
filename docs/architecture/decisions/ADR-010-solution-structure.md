# ADR-010: Estructura de la solución (mejora sobre §10 del contexto)

- Estado: Aceptada · Fecha: 2026-09-30

## Contexto
El contexto sugiere proyectos transversales `Api/Application/Domain/Infrastructure` más una carpeta `Modules/`. Capas globales Application/Domain/Infrastructure para todos los módulos crean un "gran dominio" compartido que rompe el encapsulamiento de bounded contexts (ADR-001).

## Decisión
```
src/
  SecureFact.SharedKernel/           # primitivas: Result, Money, Ruc, IClock, IMessageBus, ITenantContext…
  SecureFact.Api/                    # host HTTP: composición, middlewares, endpoints, OpenAPI
  SecureFact.Workers/                # host de BackgroundServices (outbox publisher, etc.) – ejecutable
  Modules/
    <Module>/
      SecureFact.<Module>/           # Domain + Application + Infrastructure (carpetas; tipos internos)
      SecureFact.<Module>.Contracts/ # superficie pública: interfaces, DTO, eventos de integración
workers/SecureFact.DocumentWorker/…  # entradas dedicadas por carga (se añaden al necesitarlas)
tests/{Unit,Integration,Architecture,Contract,Security,Performance,E2E}
```
- Dentro de `SecureFact.<Module>` las carpetas `Domain/`, `Application/`, `Infrastructure/` y la regla de dependencia (Domain no depende de nada; Application no depende de Infrastructure) se verifican con pruebas de arquitectura.
- Un módulo **solo** puede referenciar `SharedKernel`, `SecureFact.Platform` y los `Contracts` de otros módulos (nunca su proyecto principal).
- `SecureFact.Platform` agrupa la infraestructura transversal que usan todos los módulos (RLS/ámbito de datos, base de DbContext; luego outbox y storage) y solo referencia `SharedKernel`.
- Los tipos de módulo son `internal`; solo `Contracts` y el registro DI (`AddXxxModule`) son públicos.
- `Directory.Build.props`: `net10.0`, `Nullable` enable, `TreatWarningsAsErrors` en Release, analizadores, `Directory.Packages.props` para gestión central de versiones y `packages.lock.json`.

## Consecuencias
+ Encapsulamiento real, módulos extraíbles. − Más proyectos; se crean módulo a módulo cuando su fase comienza, no todos de golpe.
