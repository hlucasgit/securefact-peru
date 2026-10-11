# ADR-070: Avisos al revendedor por la cobranza de sus cuentas

- Estado: Aceptada · Fecha: 2026-10-11
- Completa: ADR-068 (avisos de cobranza a los propietarios), que dejó «sin copia al revendedor».
- Relacionadas: ADR-054 (avisos a los administradores del revendedor), ADR-063 (comisiones y liquidación), ADR-064 (suspensión por falta de pago).

## Contexto
El revendedor vende y atiende a sus clientes, pero se enteraba de que uno estaba atrasado, o suspendido, cuando el cliente lo llamaba. Y se enteraba de que la plataforma le liquidó un mes de comisiones al mirar su pantalla. Los administradores del revendedor ya reciben correos de su dominio (ADR-054); faltaban estos dos.

## Decisión
- **Un resumen al día, no un correo por cliente.** Cada pase de cobranza (el diario) junta, por revendedor, lo que pasó **ese día** con sus cuentas y manda un solo correo a sus administradores activos:
  - los clientes a los que ese pase les avisó que su cargo está **vencido** o que su cuenta está **cerca de la suspensión** (con el saldo, el vencimiento y la fecha de suspensión), y
  - las cuentas que ese pase **suspendió por falta de pago** (se reactivan solas al regularizar).
- **Solo lo nuevo.** El cliente aparece cuando su propietario recibe el aviso (una vez por hecho, ADR-068); si el aviso ya se dio, no vuelve a aparecer ni genera otro resumen. Un cargo que nadie paga produce, a lo sumo, un resumen por vencido, otro por suspensión cercana y otro por la suspensión, no uno por día. Sin novedades no hay correo.
- **Una vez por día y revendedor**: la clave de no repetición del correo es `billing:reseller-digest:<revendedor>:<día>`; correr el pase dos veces el mismo día no repite el resumen. Un resumen lista hasta 30 clientes y dice cuántos más hay.
- **Liquidación de comisiones**: al registrar la liquidación de un mes (ADR-063), los administradores del revendedor reciben el mes, el total, el número de pagos, la fecha y la referencia, una vez por liquidación. Las que se rechazan no avisan nada.
- **Solo lo del propio revendedor**: el resumen se arma con los cargos y cuentas cuyo revendedor es ese (el del momento del cargo, ADR-062); los nombres son de sus clientes. Las cuentas directas de la plataforma no generan resumen. El texto no incluye nada fiscal ni un secreto: nombres de cuenta, mes, saldos y fechas.
- **Mejor esfuerzo** como todo aviso (`IBillingNotices`, con implementación nula por omisión): un fallo al encolar se registra con el tipo del fallo y no deshace el cargo, el pago, la suspensión ni la liquidación.

## Verificación
`CollectionNoticesApiTests`: el resumen de un revendedor con su cliente atrasado (nombre, saldo, fecha de suspensión, enlace a sus cuentas), que no se repite al día siguiente ni al volver a correr el pase, el de suspensión cercana, y el de las cuentas suspendidas. `CommissionsApiTests`: la liquidación avisa una vez, con total, referencia y enlace, y la segunda (rechazada) no avisa.

## Límites (P)
- Solo correo a los administradores del revendedor; no hay preferencia por revendedor para apagar o cambiar la frecuencia.
- El revendedor no recibe aviso de cada pago de sus clientes ni de los cargos nuevos, solo de lo atrasado.
- Un resumen cuenta lo que el pase avisó ese día: si el correo de un cliente no se pudo encolar, esa cuenta no aparece hasta que el aviso se logre.
