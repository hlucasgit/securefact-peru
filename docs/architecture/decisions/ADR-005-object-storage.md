# ADR-005: Object storage S3-compatible para archivos documentales

- Estado: Aceptada · Fecha: 2026-09-30

## Decisión
Los archivos (JSON original/normalizado, XML sin firmar y firmado, ZIP enviado, CDR ZIP/XML, PDF, eventos de transmisión) viven en object storage; la BD guarda solo metadatos (`storage_key`, SHA-256, tamaño, MIME, versión, timestamp).

- Abstracción `IObjectStorage` (put/get/head/presigned URL) con implementación S3 (AWSSDK.S3) que sirve para AWS S3, MinIO (desarrollo) y S3-compatibles; adaptadores Azure/GCS posteriores si se necesitan.
- Claves con prefijo de tenant: `t/{tenantId}/c/{companyId}/{yyyy}/{docType}/{series}-{number}/{kind}/v{n}`.
- **Escritura única**: se habilita *object versioning* y, en producción, Object Lock (retención) para XML firmado y CDR; nunca se sobrescribe una versión previa. El hash se calcula antes de subir y se verifica al leer.
- Cifrado en reposo (SSE) y TLS en tránsito. Descargas vía URL prefirmada de corta duración tras autorización en la API.

## Consecuencias
+ Escala y costo; inmutabilidad real del CDR/XML aceptado.
− Consistencia entre BD y bucket: la fila se inserta después de confirmar la subida; un job de reconciliación detecta huérfanos.

## Adenda 2026-09-30 — servidor S3 de desarrollo
El contexto indica MinIO para desarrollo. Al intentar `docker pull` de `minio/minio`, `minio/mc` y `quay.io/minio/minio` el 2026-09-30, los registros respondieron *pull access denied* / `401 Unauthorized` (imágenes comunitarias ya no accesibles). Como el código usa **solo la API S3**, el entorno local usa el gateway S3 de **SeaweedFS** (`chrislusf/seaweedfs`, Apache-2.0) con credenciales por variables de entorno y un contenedor `s3-init` (AWS CLI) que crea el bucket con *versioning* habilitado. Cambiar de servidor S3 no afecta al código. Reevaluar MinIO (compilado desde fuente) o Garage/RustFS si SeaweedFS causa fricción.
