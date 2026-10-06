# ADR-036: Almacenamiento de objetos y archivo de XML firmado y CDR

- Estado: Aceptada · Fecha: 2026-10-06
- Implementa: ADR-005 (object storage S3-compatible), con el outbox de ADR-022 y ADR-035

## Contexto
El XML firmado y el CDR viven hoy en PostgreSQL (`cpe.electronic_document.signed_xml` y `cdr_zip`, inmutables por disparador). El ADR-005 fijó el destino: los archivos en object storage versionado y la base con los metadatos. Faltaba el código: la abstracción, el adaptador S3 y el camino que lleva los archivos allí sin perder ninguno.

## Decisión

### Abstracción y adaptador
- **`IObjectStorage`** (`SharedKernel`): `PutAsync`, `HeadAsync`, `GetAsync` y `CreateDownloadUrlAsync`. Los archivos son pequeños (kilobytes), así que viajan como arreglos de bytes. **Escritura única**: poner el mismo contenido en una clave ya escrita no hace nada y devuelve lo guardado; poner uno distinto lanza `ObjectAlreadyExistsException` y no reemplaza nada. El SHA-256 se calcula **antes** de subir y se comprueba **al leer** contra el que el llamador registró (`ObjectIntegrityException`).
- **Adaptador S3** (`SecureFact.Storage.S3`, `AWSSDK.S3` 4.0.104.1, licencia Apache-2.0 comprobada en el nuspec): sirve para AWS S3 y para servidores compatibles. Cada objeto lleva su SHA-256 como metadato y como suma de verificación adicional de la subida (`x-amz-checksum-sha256`); AWS S3 la comprueba, y el SeaweedFS de desarrollo la acepta (que la verifique no se comprobó). Opciones por la sección `Storage:S3`: `ServiceUrl`, `Region`, `Bucket`, claves (por entorno o almacén de secretos, nunca en el repositorio; vacías, rige la cadena de credenciales del SDK), `ServerSideEncryption` (`None`, `Aes256`, `Kms`), `ObjectLockMode` (`None`, `Governance`, `Compliance`) con `RetentionDays`. Se validan al arrancar. El *bucket* se crea y configura fuera de la aplicación (versionado activo y, en producción, Object Lock); el período legal de retención es del emisor y se confirma aparte (P).
- **`InMemoryObjectStorage`** (`Platform`): mismas reglas, para pruebas y desarrollo sin servidor S3. Los hosts lo usan **solo si no hay `Storage:S3:Bucket` y no es producción**; en producción sin *bucket* el host no arranca.
- **Desarrollo**: SeaweedFS fijado a la versión `4.48` en `docker-compose.yml` (antes `latest`) y en las pruebas; la API y los workers reciben `Storage__S3__*`.

### Qué se archiva y cómo llega allí
- **Qué**: el XML firmado (`signed-xml`, tal cual se envió a SUNAT) y el CDR (`cdr-zip`, tal cual lo devolvió). Los comprobantes de un resumen no tienen CDR propio (reflejan el del resumen).
- **Clave**: `t/{tenant}/c/{empresa}/{año}/{tipo}/{nombre de archivo base}/{clase}/v1`. Se aparta del ADR-005 en un punto: usa el nombre de archivo base (RUC, tipo, serie y número, o el nombre del resumen) en lugar de `serie-número`, porque el número de un resumen o de una baja se repite cada día y la clave chocaría. Un prefijo es un tenant.
- **Registro**: `cpe.archived_file` (tenant, documento electrónico, clase, clave, versión del objeto, SHA-256, tamaño, tipo de contenido, fecha), con RLS, **solo inserción** (disparador: ni el dueño del esquema cambia o borra una fila) y una fila por clase y documento (índice único).
- **Outbox propio del módulo CPE** (`cpe.outbox_message`, el mismo diseño y las mismas garantías de ADR-022: eventos inmutables, entrega al menos una vez, reintentos, muertos, reencolado, purga de lo entregado). Los eventos `cpe.document.prepared` y `cpe.document.answered` se escriben **en la misma transacción** que guarda el documento firmado y que registra el CDR (también el CDR ilegible o que no corresponde, que se conserva como evidencia). Un consumidor por evento archiva; es idempotente. Los eventos también salen al bus (ADR-035) para quien los quiera (webhooks, por ejemplo): la carga solo trae identificadores, sin datos del documento.
- **`PostgresOutboxSource`** (`Platform`): el SQL del outbox (reclamar, completar, fallar con espera exponencial, listar muertos, reencolar, purgar) deja de estar copiado en cada módulo; Billing lo usa también (sus pruebas del outbox pasan sin cambios de comportamiento) y `OutboxSql.Secure(schema)` da el DDL de permisos, disparador y función de purga para la migración de un módulo nuevo.
- **Red de seguridad**: en cada pasada el worker de CPE encola el evento de todo documento que tenga archivo por archivar, no tenga copia archivada y no tenga un evento esperando. Así llegan al almacén los documentos **anteriores** al archivo y los eventos perdidos; un evento muerto cuenta como esperando (no se encolan eventos nuevos en cada pasada para un documento que no se puede archivar: un operador reencola el muerto).
- **Fallos visibles**: una clave que ya contiene otro contenido hace fallar el mensaje (error registrado) y no se sobrescribe nada; un almacén caído se reintenta con la espera de ADR-022.

### Descarga
`GET /api/v1/electronic-documents/{id}/archive` (permiso `documents.read`, por tenant) lista los archivos archivados con su clase, tipo, tamaño, SHA-256 y un **enlace de descarga prefirmado de 5 minutos**: la API autoriza y el almacén solo sirve el enlace (ADR-005). Un documento de otro tenant da 404.

## Decisión consciente: la base sigue siendo la fuente
**Esta es la fase 1 del ADR-005**: el archivo es una copia **duradera y versionada**, y la base conserva `signed_xml` y `cdr_zip`; la plataforma sigue trabajando desde ellos (envío, descargas de `/xml` y `/cdr`, firma, reglas de inmutabilidad). Quitar los bytes de la base (fase 2) cambia el modelo de datos, el envío y las lecturas, y se hará cuando haya evidencia de que el archivo está completo y se haya decidido la lectura desde el almacén. Condiciones para esa fase: cero documentos sin `archived_file` tras la red de seguridad, lectura verificada por hash en las descargas, política de retención y Object Lock confirmados.

## Verificación
- Contra un **SeaweedFS real** (Testcontainers, `chrislusf/seaweedfs:4.48`): ida y vuelta con el hash, versión del objeto, misma escritura sin efecto y distinta rechazada (una sola versión en el *bucket*), lectura de un objeto que no existe, **detección de un archivo alterado por fuera de la plataforma**, enlace prefirmado que descarga sin credenciales, claves que podrían salir de su prefijo rechazadas, validación de la configuración.
- Con el host real (API y worker): el XML y el CDR de una factura aceptada quedan archivados con su hash y listados con enlaces; archivar de nuevo no cambia nada y una clave ocupada por otro contenido falla sin reemplazarlo; el archivo es privado del tenant; el registro y los eventos son de solo anexar también para el dueño; la red de seguridad encuentra un documento sin evento y lo archiva sin repetirse; el archivo sobrevive a la purga de su evento.

## Límites (P)
- **Fase 2 pendiente** (arriba): los bytes siguen también en la base.
- **Sin conciliación periódica del contenido**: se comprueba el hash al subir y al leer, pero ningún proceso relee los objetos para verificar que siguen íntegros; un servicio que lo haga (muestreo) es trabajo futuro.
- **Object Lock y retención legal**: la opción existe y está validada, pero no se probó contra un *bucket* con Object Lock (SeaweedFS de desarrollo no lo ofrece) y el período de conservación que corresponde a los comprobantes electrónicos no se verificó en fuente primaria.
- **El PDF no se archiva** (se genera bajo demanda), ni el JSON original de la solicitud, que sigue en Billing.
- Un solo *bucket* y un solo origen (CPE); la compartición con otros tipos de archivo (certificados, por ejemplo) no está definida.
