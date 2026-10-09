# ADR-059: Guía de remisión remitente con aduanas: motivos 08, 09, 18 y 19

- Estado: Aceptada · Fecha: 2026-10-09
- Desarrolla: R-074 a R-077 de la matriz (fuente S27, hoja `Guía-Remitente2_0`, releída el 2026-10-09; catálogos 55, 61, 63, 64 y 65 de S27).
- Completa: ADR-056, que dejó fuera estos cuatro motivos (R-068) y los rechazaba con `SF-GRE-003`.

## Contexto
Cuatro motivos del catálogo 20 quedaron sin emitir:
- **08 importación**, **09 exportación** y **19 traslado de mercancía extranjera** llevan documentos de aduanas (declaración DAM `50` o simplificada DS `52`, manifiesto de carga `91`, orden de entrega del terminal portuario `92`), puerto o aeropuerto, peso neto, contenedores y, en cada bien, los datos de la declaración o de la línea del manifiesto.
- **18 emisor itinerante** es el traslado de quien vende en ruta: la guía no tiene punto de llegada.

La hoja de reglas de SUNAT dice para cada uno qué se exige y qué no; el trabajo es leerla entera y aplicarla sin suponer. Lo que SUNAT contesta de sus registros (que la declaración exista y de quién sea, las líneas del manifiesto, el depósito temporal) queda para el CDR, como en el resto de la guía.

## Decisión
Los cuatro motivos se emiten con el mismo servicio, la misma tabla y el mismo envío que los demás. `GreService` ya no rechaza ninguno (`SF-GRE-003` queda sin uso: el motivo `13` con una declaración lo rechaza el validador con el código 3445).

### Solicitud
- `CreateGreRequest.Customs` (`GreCustomsInput`): `PortCode` y `PortType` (`1` puerto del catálogo 63, `2` aeropuerto del 64) y `PortName`; `WholeTransfer` (viaja toda la declaración: los bienes no se listan uno a uno); `ManifestContainers` (las líneas del manifiesto viajan en contenedores); `NetWeight` (`KGM`) y `WeightNote` (por qué el peso bruto difiere); hasta dos `Containers` (número y precinto).
- `GreGoodInput.Customs` (`GreGoodCustomsInput`): numeración de la declaración y serie del bien en ella (propiedades 7021 y 7023), y, del manifiesto, documento de transporte (7024), detalle (7025), contenedor (7026), precinto (7027) e indicador de contenedor vacío (7028).
- `Destination` puede faltar, **solo** con el motivo 18.

### Reglas (`GreValidator.Customs`, las de S27 con su código)
- **Documentos relacionados** (R-074): la importación y la exportación exigen una `50` o `52` y solo admiten además una guía `09` anterior; el 19 exige una `50`, `52`, `91` o `92` y no admite otros; el `91` no va con la `50` ni la `52`, la `92` va sola, y no hay más de un `91` ni de una `92`; las declaraciones solo se relacionan con 08, 09 y 19 y el manifiesto y la orden solo con el 19. El número de cada uno tiene la forma de su motivo: `50` régimen 10 (08), 40 (09) o 10, 20, 21, 30, 36, 70, 80 (19); `52` régimen 18 (08 y 19) o 48 (09); `91` con la vía 1 si el punto es un puerto y 4 si es un aeropuerto; `92` de hasta 50 dígitos con el RUC del emisor.
- **Partes y puntos** (R-075): el destinatario de la exportación no es el remitente, el del 19 tiene RUC, y el del 18 es el propio remitente. El punto de partida de la importación y del 19 es el ubigeo del puerto o aeropuerto (el catálogo trae el ubigeo de cada uno); la importación sin puerto pide el establecimiento de partida (el depósito temporal), la exportación sin puerto el de llegada, y el 19 llega a un establecimiento del destinatario.
- **Pesos, bultos y contenedores** (R-076): el peso neto y su sustento solo existen en estos motivos y la exportación sin traslado total los exige; el peso, los bultos y los contenedores no van con la orden `92`; en lo demás hay bultos o contenedores, no ambos, con hasta dos contenedores sin repetir y su precinto.
- **Bienes** (R-077): la unidad sale del catálogo 65; la exportación lista sus bienes con la numeración de la declaración (una de las relacionadas) y su serie; el manifiesto lista sus líneas con documento de transporte y detalle (unidad `U`, cantidad entera) y, con contenedores, contenedor, precinto e indicador de vacío.
- Los datos de aduanas con otro motivo se rechazan (3392, 3395, 3418, 3478). Las reglas propias que SUNAT no numera llevan `SF` en lugar de un código.

### XML (`GreUblGenerator`)
Añade `cbc:Information` (sustento del peso), `cbc:NetWeightMeasure`, los indicadores `SUNAT_Envio_IndicadorTrasladoTotalDAMoDS` y `…TrasladoContenedorManifiestoCarga`, `cac:Package` con `cbc:ID` y `cbc:TraceID` dentro de la unidad de transporte, `cac:FirstArrivalPortLocation`, las propiedades `cac:AdditionalItemProperty` del catálogo 55 y, para el 18, un `cac:Delivery` sin `DeliveryAddress`. Sin peso bruto (orden `92`) no se escribe la etiqueta; con las líneas del manifiesto en contenedores no se escribe la cantidad. Se valida contra el XSD oficial en los siete casos de prueba.

**La línea de forma (P).** El esquema UBL pide al menos una línea de despacho. Cuando no hay bienes (toda la declaración viaja, o la orden `92` lo dice todo) el XML lleva una línea con orden `1`, referencia `1` y un `cac:Item` vacío, que es lo que la hoja de SUNAT prescribe solo para la orden `92`; para el traslado total es una extensión nuestra sin beta con que comprobarla.

### Catálogos y reglas como datos
El contexto de validación lee de la base los catálogos 63, 64 (con su ubigeo) y 65 (ya cargados con la hoja de S27); las reglas no se escriben en el código por país ni por fecha. Si un catálogo no está cargado, sus códigos no se comprueban.

### Pantallas y papel
El formulario de emisión ofrece los cuatro motivos: el motivo elige los documentos relacionados que se pueden agregar, muestra la tarjeta *Aduanas* (puerto o aeropuerto de los catálogos, peso neto, contenedores, traslado total o en contenedores del manifiesto), pide en cada bien los datos de su declaración o de su línea del manifiesto y oculta el punto de llegada del 18 y los bienes y el peso con la orden `92`. La hoja impresa y el detalle muestran el motivo, el puerto, el peso neto y los contenedores.

## Verificación
- Unidad (`GreCustomsTests`, 32): las siete guías completas válidas; cada familia de reglas con sus códigos (documentos y sus formas por régimen, destinatario y puntos, puerto, pesos, bultos y contenedores, bienes, líneas del manifiesto, datos fuera de su motivo); el XML de los siete casos contra el XSD y el contenido de cada etiqueta nueva.
- API (`GreCustomsApiTests`, PostgreSQL con los catálogos reales): importación aceptada con su PDF, exportación con unidad del catálogo 65 y rechazo de una unidad ajena, 19 con orden y con manifiesto y el 18 sin llegada ni etiqueta de entrega, y una guía con tres errores a la vez devuelta con los tres códigos.
- Web: `guide.test.ts` (armado de la solicitud y documentos por motivo) y `customs-guides.spec.ts` de extremo a extremo con el simulador (18, importación aceptada, importación sin declaración refusada con el 3440).

## Límites (P)
- **Sin SUNAT real** (ADR-056): estas reglas y la línea de forma no se probaron con una CDR.
- **Sin partida arancelaria (7020) ni indicador de bien normalizado (7022)**: los documentos `49` y `80` y el catálogo 62 no se admiten con estos motivos, y el motivo `13` con una declaración tampoco.
- Lo que contesta SUNAT de sus registros (existencia y estado de la declaración, importador o exportador, depósito temporal, líneas y contenedores del manifiesto, administrador portuario de la orden `92`) no se comprueba aquí.
- Con la declaración o el manifiesto no hay comprobación cruzada entre el peso neto, la cantidad y la declaración (4436): la hace SUNAT.
- El contenedor se valida por su forma (17 caracteres), no por el dígito de control ISO 6346: SUNAT no lo pide en la hoja.
- La orden `92` y el 18 con las guías del transportista (la `09` con serie numérica o motivo 18 que cita la `31`) no se cruzan: la guía del transportista solo admite las `T…` electrónicas (ADR-057).
