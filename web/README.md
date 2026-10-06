# SecureFact Perú — interfaz web

React 19 + TypeScript + Vite. Decisiones y límites: [ADR-038](../docs/architecture/decisions/ADR-038-web-interface.md).

## Desarrollo
Requiere la API en marcha (`docker compose up -d --build` en la raíz, o `dotnet run --project src/SecureFact.Api`) y un usuario (README de la raíz: `bootstrap-platform-admin`, luego un inquilino y su propietario por la API).

```bash
cd web
npm ci
npm run dev        # http://localhost:5173, /api se reenvía a SF_API_URL (por defecto http://localhost:5180)
```

| Comando | Qué hace |
|---|---|
| `npm test` | Pruebas unitarias (Vitest) |
| `npm run typecheck` | Tipos (TypeScript estricto) |
| `npm run lint` | oxlint |
| `npm run build` | Compilación de producción en `dist/` |

Con `docker compose up -d --build` el servicio `web` sirve la interfaz en `http://localhost:5173` y reenvía `/api` a la API.

## Pruebas de extremo a extremo (Playwright)
Contra la API real, no contra simulaciones del navegador (ADR-040). Necesitan: la API **y los workers** en marcha con `Sunat__Environment=Sandbox` (el simulador de SUNAT, ADR-039), PostgreSQL y el almacén S3, y un administrador de plataforma. El CI lo hace en `.github/scripts/e2e-stack.sh`; en su máquina:

```bash
docker compose up -d --build   # con SF_SUNAT_ENVIRONMENT=Sandbox en .env; el administrador: bootstrap-platform-admin (README de la raíz)
cd web
npm ci && npx playwright install chromium
SF_E2E_ADMIN_EMAIL=admin@ejemplo.local SF_E2E_ADMIN_PASSWORD='<su clave>' npm run e2e
```

- Variables: `SF_E2E_API_URL` (por defecto `http://localhost:5180`), `SF_E2E_WEB_URL` (si ya sirve la interfaz; si no, Playwright la compila y la sirve), `OPENSSL` (ruta del comando `openssl`, que genera los certificados de prueba).
- La API de las pruebas necesita `RateLimiting__AuthPermitPerMinute=1000` (en Docker, `SF_AUTH_RATE_LIMIT=1000`: se inicia sesión muchas veces desde una dirección) y los workers `Cpe__WorkerIntervalSeconds=3` (`SF_CPE_WORKER_INTERVAL=3`), para que la baja y el archivo no tarden.
- `npm run e2e:ui` abre el modo interactivo. Un fallo deja captura, traza y el informe en `playwright-report/`.
- Cada prueba crea su cuenta: no hace falta limpiar nada entre corridas.

## Enviar a SUNAT desde la interfaz
La API no contacta a SUNAT por omisión: `Sunat__Environment` vacío da el error `SF-CPE-005` en «Enviar a SUNAT». Con `SF_SUNAT_ENVIRONMENT=Sandbox` el envío lo contesta el **simulador en proceso** (ADR-039: acepta sin validar; las marcas `[sandbox:observar]`, `[sandbox:rechazar]` y `[sandbox:rechazar-cdr]` en la descripción de un ítem provocan los otros desenlaces). Para probar contra el beta de SUNAT (solo pruebas funcionales, nunca carga) configure `SF_SUNAT_ENVIRONMENT=Beta` en el `.env` de los workers y de la API, y cargue en la empresa el certificado digital y las credenciales SOL de pruebas.

## Estructura
- `src/api/`: cliente HTTP (`http.ts`), tipos de las respuestas (`types.ts`) y los *hooks* de consulta (`queries.ts`).
- `src/auth/`: sesión (tokens, renovación, roles).
- `src/components/`: piezas compartidas (`ui.tsx`, `Layout.tsx`, `LinesEditor.tsx`).
- `src/pages/`: una pantalla por ruta.
- `src/lib/`: formatos y etiquetas.

La interfaz no calcula impuestos: envía lo escrito y muestra lo que calcula la API.
