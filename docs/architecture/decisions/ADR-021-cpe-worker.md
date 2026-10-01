# ADR-021: Worker de documentos electrónicos

- Estado: Aceptada · Fecha: 2026-10-01

## Decisión
`ICpeWorkProcessor` (módulo `CpeEngine`) hace una pasada de trabajo entre tenants y `SecureFact.Workers` la repite cada `Cpe:WorkerIntervalSeconds` (15 s por defecto) en un `BackgroundService`. Una pasada:

1. **Resúmenes de días cerrados**: boletas preparadas (estado `ReadyToSend`) de fechas anteriores a hoy (hora de Lima) agrupadas por tenant, empresa y fecha → `ISummaryService.CreateAsync`. El día en curso no se resume. Las boletas del día que aún no tienen documento electrónico las prepara el propio resumen.
2. **Envíos vencidos**: facturas y resúmenes en `ReadyToSend` cuya `next_attempt_at` es nula o ya pasó (espera exponencial, ADR-019).
3. **Tickets**: resúmenes en `AwaitingTicket` cuya próxima consulta venció (cada minuto; 24 h máximo, ADR-020).
4. **Atascados**: documentos en `Sending` por más de 15 minutos se **cuentan y se registran, nunca se reenvían**: el resultado en SUNAT es desconocido. Un operador, tras comprobar el estado en SUNAT, usa `POST /api/v1/electronic-documents/{id}/recover` (`Sending → ReadyToSend`, permiso `cpe.send`, auditado; solo pasado el plazo). Reenviar puede duplicar (R-038).

## Reglas de operación
- **Aislamiento**: el descubrimiento usa el ámbito de plataforma solo para listar identificadores; cada elemento se procesa en su propio ámbito de DI con `UseTenant`, de modo que el trabajo real sigue bajo RLS. La identidad es la del sistema (`SystemCurrentUser`: sin usuario, sin roles; la auditoría registra actor «system»).
- **Tolerancia a fallos**: un elemento que falla (precondición, concurrencia o excepción) se cuenta y se sigue con los demás; una pasada que lanza no detiene el bucle. Faltar credenciales SOL no consume intentos.
- **Varias instancias**: seguro; cada documento se reclama con el token de concurrencia (ADR-019) y el índice único de resumen activo evita duplicados.
- **Sin canal no hay SUNAT**: sin `Sunat:Environment` (`Beta` o `Production`) el worker solo crea resúmenes. El beta nunca se usa para pruebas de carga.
- **Lotes** de 50 por tipo y pasada, los más antiguos primero.
- Pruebas: procesador con reloj desplazado (para cruzar esperas sin dormir) y el host real (`WorkerHost`) arrancado contra la base de pruebas con el simulador de SUNAT.

## Límites
- Solo ve documentos que ya tienen documento electrónico: **falta generarlo automáticamente al emitir** (outbox Billing → CPE); hoy lo dispara la API (`POST /documents/{id}/electronic`) o el resumen.
- Un resumen que no se puede generar (por ejemplo, una boleta con operación gratuita) bloquea ese día y se reintenta en cada pasada con una línea en el registro.
- Sin métricas propias aún (solo registros) y sin elección de líder.
