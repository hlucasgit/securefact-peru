# ADR-061: La guía de remisión por evento no se emite desde SecureFact

- Estado: Aceptada · Fecha: 2026-10-09
- Desarrolla: R-079 de la matriz (fuente S34, páginas «Tipos de guía de remisión» y «Preguntas frecuentes» del portal CPE de SUNAT; RS 123-2022, S32).
- Cierra: el pendiente «GRE por eventos» que dejaron ADR-056 y ADR-057.

## Contexto
La GRE por evento complementa otra GRE del mismo emisor cuando ocurre algo que no es imputable a él: no se puede llegar al punto de llegada o entregar los bienes allí (y hay que partir a otro lugar para el mismo destinatario), o hay que transbordarlos a otro vehículo. La emite el remitente en el transporte privado y el transportista en el público, antes de reiniciar el traslado, y con ella y el documento anterior se sustenta el tramo que sigue. Se relaciona con una GRE propia que no esté dada de baja e informa el tipo de evento, el punto de inicio del tramo, el de llegada si cambia, el establecimiento, los vehículos y los conductores.

## Hallazgo
La regla 1 de este proyecto pide la fuente oficial antes de codificar. Se buscó en este orden:
- La hoja de reglas S27 solo trae `Guía-Remitente2_0` y `Guía-Transportista2_0`: no hay hoja ni estructura UBL de la GRE por evento. El anexo S33 tampoco la describe.
- La página oficial de SUNAT (S34) dice que los canales de emisión son el portal de SUNAT, la APP Emprender y el SEE propio, y que las series son: remitente `EG07` (portal), `EG02` (APP) y `T…` (SEE propio); transportista `EG03`, `EG04` y `V…`; **evento `EG05` (portal) y `EG06` (APP), sin serie para un SEE propio**.
- La RS 123-2022 manda emitirla «a través del SEE-SOL», que es el sistema de SUNAT, no el del contribuyente.

## Decisión
No se implementa. Un sistema propio no puede emitir la GRE por evento: SUNAT no le asigna serie ni publica la estructura. Inventar un UBL o una serie rompería la regla 1, y SUNAT lo rechazaría.

Lo que sí hace SecureFact en estos casos: el usuario emite el evento en el portal de SUNAT o la APP Emprender. La guía propia no cambia: sigue aceptada y es inmutable.

## Límites (P)
- Sin SUNAT real: la lectura es de páginas informativas, no de una especificación técnica. Si SUNAT publica una estructura y una serie para sistemas propios (se revisa con cada nueva versión de la hoja de reglas), R-079 se reabre.
- Las guías que sustentan un traslado tras un evento (la propia más la del evento emitida en el portal) no se relacionan entre sí en SecureFact.
