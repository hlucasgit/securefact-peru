# ADR-064: Suspensión por falta de pago

- Estado: Aceptada · Fecha: 2026-10-09
- Completa: ADR-045 (suspensión por el revendedor), que dejó «sin suspensión programada ni automática por mora».
- Relacionadas: ADR-062 (cargos y política de cobranza).

## Decisión
- Una cuenta con un cargo **sin pagar pasada su gracia** se suspende sola. La gracia sale de la política de cobranza vigente el día que se emitió el cargo, que guarda su fecha (`suspend_on` = vencimiento + días de gracia; vacía si la política no suspende). Publicar otra política no cambia los cargos ya emitidos.
- Quien suspende es el pase de cobranza (`CollectionPass`, el mismo del `SubscriptionWorker`), con el origen nuevo `SuspensionSource.NonPayment`: el efecto es el de cualquier suspensión (ADR-041: no ingresan, sesiones abiertas dejan de servir, `SF-TEN-002`), queda auditada con motivo y se avisa por correo como las demás (ADR-054).
- **Se levanta sola** cuando ya no queda ningún cargo vencido pasada su gracia: al registrar el pago que lo salda, al anular un cargo erróneo o en la siguiente pasada. **Solo levanta lo que ella suspendió**: una suspensión de la plataforma (uso indebido) o del revendedor no se levanta con un pago.
- Un **revendedor no levanta** una suspensión por mora (`SF-TEN-004`), como no levanta las de la plataforma; la plataforma sí, y puede tomarla como suya suspendiendo de nuevo.
- Un pago parcial no levanta mientras quede saldo vencido pasada la gracia; el saldo de varios cargos cuenta cargo por cargo.
- La cuenta ya suspendida por otro origen no se vuelve a suspender; una cuenta cerrada no se cobra ni se suspende.

## Verificación
`SubscriptionsApiTests`: sin suspensión antes de la gracia; suspensión con `NonPayment`, ingreso con `SF-TEN-002` y sesión abierta caída; un segundo pase no repite; un pago parcial no levanta y el resto sí; una cuenta puntual no se suspende; una suspensión de la plataforma sobrevive al pago; anular el cargo erróneo levanta. `CommissionsApiTests`: el revendedor recibe `SF-TEN-004`.

## Límites (P)
- La gracia y los plazos son cifras comerciales de la política, no normativa; la inicial es 10 días de plazo y 15 de gracia.
- No hay aviso previo de vencimiento ni de «se va a suspender»; el cliente se entera del cargo en su pantalla de plan y de la suspensión por el correo de estado (ADR-054).
- Suspender no borra datos ni impide que se sigan archivando y consultando los comprobantes ya emitidos (ADR-041); el cierre definitivo sigue siendo de la plataforma.
- Suspender corta también la emisión de comprobantes, y una cuenta de producción debe emitirlos a tiempo ante SUNAT. Por eso la suspensión es parte de la política y no del código: una versión con `suspend_after_days` vacío deja de suspender.
