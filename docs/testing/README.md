# Estrategia de pruebas

- `tests/Unit`: dominio y cálculos. `tests/Architecture`: límites de módulos (bloqueante en CI). `tests/Integration`: API, BD, storage, colas. `tests/Contract`: adapters SUNAT/PSE/OSE contra el simulador. `tests/Security`: autorización y aislamiento cross-tenant. `tests/Performance`, `tests/E2E`.
- Sin FluentAssertions (licencia comercial en v8): se usa `Assert` de xUnit.
- Servicio beta de SUNAT solo para estructura XML en Staging; carga y resiliencia con el simulador interno.
