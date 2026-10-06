# Estrategia de pruebas

- `tests/Unit`: dominio y cálculos. `tests/Architecture`: límites de módulos (bloqueante en CI). `tests/Integration`: API, BD, storage, colas. `tests/Contract`: adapters SUNAT/PSE/OSE contra el simulador. `tests/Security`: autorización y aislamiento cross-tenant. `tests/Performance`, `tests/E2E`.
- **CI** (`.github/workflows/ci.yml`, GitHub Actions): restauración con `--locked-mode`, compilación en **Release** (donde las advertencias son errores: una prueba que compila en Debug puede romper el CI), pruebas unitarias, de arquitectura, de seguridad/API (Testcontainers) e integración, paquetes vulnerables, SBOM, gitleaks y construcción y escaneo con Trivy de las imágenes de la API y de los workers. Las acciones de terceros van fijadas a un commit. Antes de subir, `dotnet build SecureFact.slnx -c Release` y las pruebas dan el mismo resultado que el CI.
- Sin FluentAssertions (licencia comercial en v8): se usa `Assert` de xUnit.
- Servicio beta de SUNAT solo para estructura XML en Staging; carga y resiliencia con el simulador interno.
