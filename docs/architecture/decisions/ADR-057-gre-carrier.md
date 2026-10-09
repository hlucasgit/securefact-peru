# ADR-057: Guía de remisión electrónica del transportista (31)

- Estado: Aceptada · Fecha: 2026-10-08
- Desarrolla: R-069 a R-072 de la matriz (fuente S27, hoja `Guía-Transportista2_0`, leída el 2026-10-08; canal S28 y S29).
- Completa: ADR-056 (guía del remitente), que dejó el transportista para otra entrega.

## Contexto
El transportista que presta el servicio de traslado emite su propia guía, tipo `31`, con serie `V…`. Es otro documento, no una variante del remitente: no tiene motivo ni modalidad de traslado; el emisor es el transportista y el remitente (quien envía los bienes) es un tercero; el vehículo y el conductor son siempre suyos; y puede apoyarse en la guía del remitente (`09`) para no repetir los bienes. El canal hacia SUNAT (token, envío del zip, ticket, CDR) es el mismo que el del remitente.

## Decisión
Se agrega al módulo `Gre`, sin otro módulo ni otra tabla: la guía sigue siendo una fila de `gre.guide` y su serie una de `gre.series`, con `document_type_code` (`09` o `31`).

### Qué cambia
- **Series.** `T###` es del remitente (`09`) y `V###` del transportista (`31`); la letra decide el tipo, la base lo comprueba (`CHECK`) y el tipo de una serie y de una guía no cambia (disparadores). Una guía solo se numera en una serie de su tipo (`SF-GRE-005`).
- **Solicitud.** `CreateGreCarrierRequest` (`POST /api/v1/gre/guides/carrier`): remitente, destinatario, puntos de partida y llegada, inicio del traslado, peso, vehículo principal y hasta 2 secundarios, conductor principal y hasta 2 secundarios, bienes o documentos relacionados, registro MTC, subcontratador, quién paga el flete y los indicadores de transbordo y retorno. El resto del ciclo (`submit`, `refresh`, XML, CDR, lista, worker) es el mismo endpoint y el mismo servicio: el nombre del archivo es `RUC-31-V001-1` y el tipo viaja en la `GreSubmission`.
- **`motive_code` y `modality_code`** de la guía pasan a ser opcionales (migración `AddCarrierGuide`); en la guía del transportista no existen.
- **`GreService`** reparte lo común (inquilino, empresa, serie, contexto de catálogos y reglas, número en la base, firma, fila y auditoría) en un solo camino, `PrepareAsync`; cada tipo aporta sus reglas y su XML.

### Reglas (S27, hoja `Guía-Transportista2_0`)
Se escriben como las del remitente, por el código de la regla de SUNAT, y las que solo puede contestar un registro de SUNAT se dejan al CDR.
- **Cabecera:** igual que el remitente (formato, plazo de emisión por el parámetro versionado `gre.max_issue_lag_days`, observaciones); el inicio del traslado no es anterior a la emisión (3343); peso positivo de hasta 12 enteros y 3 decimales en `KGM` o `TNE`.
- **Partes:** remitente y destinatario con documento del catálogo 06 y nombre; el remitente no es el propio transportista (2560).
- **Documentos relacionados:** `01`, `03`, `04`, `12`, `48` y `09`. De la `09` solo las electrónicas, serie `T…`, y su emisor es el remitente de esta guía (3381). Sin una `09` solo se admite un documento relacionado (3346). Los demás tipos del catálogo 61 (aduanas, eventos, constancias, otras guías del transportista) se rechazan con 3445.
- **Bienes y direcciones:** con una `09` relacionada, esa guía lista los bienes y las direcciones, así que no se informan bienes (4434) y las direcciones pueden faltar; sin ella hace falta al menos un bien (3435) y las dos direcciones (2577, 2574), salvo con transbordo programado. El ubigeo siempre (2775, 2776). El esquema UBL pide al menos una línea, así que cuando no hay bienes el XML lleva la línea de anotación de la hoja (número de orden «0», sin cantidad) que dice dónde están listados.
- **Vehículo y conductor:** placa de 6 a 8 (2566, 2567) y tarjeta de circulación (se pide: 4399, formato 3355) del principal y de los secundarios (hasta 2, 4389); conductor principal con documento que no es RUC (2571), nombres, apellidos y licencia de 9 a 10 (2573); hasta 2 secundarios (4376) con licencias distintas (3362).
- **Indicadores:** subcontratado (con el subcontratador por RUC, distinto del transportista: 3391, 3390, 4424, 4426), pagador del flete (remitente, subcontratador o tercero; el tercero se nombra: 4402, 3399, 3400), transbordo programado, retorno con envases vacíos y retorno vacío.

### XML
`GreUblGenerator.GenerateCarrier` arma el `DespatchAdvice` según las etiquetas de la hoja y el orden del esquema (S30), y se prueba contra el XSD oficial en sus dos formas (con bienes, con la `09` relacionada) y con todos los indicadores. El transportista va en `DespatchSupplierParty`, el destinatario en `DeliveryCustomerParty`, el remitente en `Shipment/Delivery/Despatch/DespatchParty`, el subcontratador en `Consignment/LogisticsOperatorParty` y el tercero que paga en `OriginatorCustomerParty`.

### Pantallas
*Emitir guía del transportista* (partes y puntos, vehículos y conductores, bienes —que desaparecen cuando se indica la guía del remitente—, documentos relacionados, flete e indicadores); la lista y el detalle distinguen el tipo; en la empresa, la pestaña *Guías de remisión* crea series `T…` y `V…`.

## Verificación
- Unidad (`GreCarrierTests`): reglas por código, esquema XSD de las dos formas y de todos los indicadores, y dónde queda cada parte en el XML.
- API (`GreCarrierApiTests`, PostgreSQL): numeración propia, XML, serie del otro tipo en los dos sentidos, reglas incumplidas con sus códigos, envío con tipo y nombre de archivo propios y aceptación, guía apoyada en la del remitente, aislamiento entre cuentas, y los disparadores y el `CHECK` de la base.
- Interfaz: `carrierGuide.test.ts` (armado de la solicitud) y `carrier-guides.spec.ts` de extremo a extremo con el simulador.

## Límites (P)
- Valen los de ADR-056: **sin SUNAT real** (no hay beta documentado), con los mismos supuestos sobre el `hashZip`, el CDR y `cac:Signature`. La representación impresa se hizo después (ADR-058).
- **Sin el indicador de traslado total de bienes** ni las guías por eventos (cambio de vehículo, imposibilidad de arribo o de entrega), ni los documentos de aduanas, constancias de depósito, ni otras guías del transportista (`31`) como documento relacionado.
- De la `09` relacionada solo se admiten las electrónicas (serie `T…`): las impresas, de serie numérica, no.
- **No se comprueba contra la guía del remitente** que el destinatario coincida (3434) ni que exista: no está en nuestra base si la emitió otro contribuyente; SUNAT lo contesta.
- La autorización especial del transportista y del vehículo (entidad autorizadora, catálogo D-37) y la georreferencia de los puntos no se informan.
- La interfaz exige la tarjeta de circulación aunque SUNAT solo la observa (4399): es más estricta que SUNAT a propósito, como con las demás observaciones.
