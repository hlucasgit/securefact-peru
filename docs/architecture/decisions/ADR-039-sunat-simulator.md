# ADR-039: Simulador de SUNAT en proceso (`Sunat:Environment=Sandbox`)

- Estado: Aceptada · Fecha: 2026-10-06
- Implementa: el `SandboxChannel` de ADR-006; respeta la regla 6 de CLAUDE.md (el beta de SUNAT no se usa para pruebas de carga o repetidas)

## Contexto
`ICpeSubmissionChannel` solo tenía dos adaptadores reales: el beta y la producción de SUNAT. Sin un canal configurado, la API contesta `SF-CPE-005` al enviar. Eso dejaba fuera de cualquier prueba automática y de toda demostración el recorrido completo (enviar, recibir el CDR, archivar, dar de baja), y obligaba a usar el beta para ver el resultado en la interfaz.

## Decisión
- **`SandboxSunatChannel`** (`SecureFact.CpeEngine`, interno): un canal **en proceso** que implementa los tres métodos del contrato y **no sale de la máquina**. Se activa con `Sunat:Environment=Sandbox` en la API y en los workers (`AddSandboxSubmissionChannel`).
- **Se rechaza en producción**: el registro lanza si el entorno de ASP.NET es `Production`. El simulador **acepta sin validar** contra las reglas de SUNAT; en producción un emisor creería que SUNAT aceptó lo que nadie revisó.
- **Sin estado**: el ticket de un resumen o de una baja lleva su propia referencia (`SBX.` + la referencia en base64 URL), así la API y los workers se contestan entre sí sin compartir memoria.
- **Comportamiento**: factura, boleta y notas se aceptan con un CDR con la forma del Anexo 1 del manual (referencia `SERIE-NÚMERO`, RUC del emisor, código 0). Un resumen o una baja emiten ticket y el estado contesta el CDR del resumen o de la comunicación. Un nombre de archivo que no es el de SUNAT, un ZIP ilegible o un ticket ajeno se contestan con las fallas de cliente 151, 153 y 1033.
- **Marcas en la descripción de un ítem** para provocar cada desenlace (documentadas aquí y en las pruebas):
  - `[sandbox:observar]`: aceptación con la observación 4030 (estado «Aceptado con observaciones»).
  - `[sandbox:rechazar]`: falla del servicio con el código 2800 (SOAP *fault*; el envío falla para siempre, estado «Falló»).
  - `[sandbox:rechazar-cdr]`: CDR con el código de rechazo 2800 (estado «Rechazado»).
  Los textos dicen «Simulador» para que nadie confunda el resultado con una respuesta de SUNAT; los códigos de rechazo no afirman el significado que SUNAT les da.
- **Configuración**: en `docker-compose.yml` y en el CI, `SF_SUNAT_ENVIRONMENT=Sandbox`; vacío sigue siendo «sin canal» (la API prepara y firma, pero no envía), y `Beta` y `Production` siguen exigiendo nombrarlos.

## Verificación
15 pruebas unitarias del canal (aceptación y su CDR, las tres marcas, nombres de archivo y ZIP inválidos, resumen y baja con su ticket, tickets ajenos, registro fuera y dentro de producción) y los recorridos de extremo a extremo de la interfaz (ADR-040), que envían, observan, rechazan y dan de baja contra él.

## Límites (P)
- **No es SUNAT**: no valida XSD, firma ni reglas. Que el simulador acepte un documento no prueba que SUNAT lo acepte; eso solo lo prueba el beta (herramienta `tools/SecureFact.BetaSmoke`, de forma funcional y nunca con carga).
- Un solo CDR por tipo de desenlace; no simula caídas, lentitud ni tiempos de espera (las pruebas del canal SOAP real lo hacen con un servidor HTTP propio).
