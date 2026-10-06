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

## Enviar a SUNAT desde la interfaz
La API no contacta a SUNAT por omisión: `Sunat__Environment` vacío da el error `SF-CPE-005` en «Enviar a SUNAT». Para probar contra el beta de SUNAT (solo pruebas funcionales, nunca carga) configure `SF_SUNAT_ENVIRONMENT=Beta` en el `.env` de los workers y de la API, y cargue en la empresa el certificado digital y las credenciales SOL de pruebas.

## Estructura
- `src/api/`: cliente HTTP (`http.ts`), tipos de las respuestas (`types.ts`) y los *hooks* de consulta (`queries.ts`).
- `src/auth/`: sesión (tokens, renovación, roles).
- `src/components/`: piezas compartidas (`ui.tsx`, `Layout.tsx`, `LinesEditor.tsx`).
- `src/pages/`: una pantalla por ruta.
- `src/lib/`: formatos y etiquetas.

La interfaz no calcula impuestos: envía lo escrito y muestra lo que calcula la API.
