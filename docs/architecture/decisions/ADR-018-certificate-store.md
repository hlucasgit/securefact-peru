# ADR-018: Almacén de certificados digitales

- Estado: Aceptada · Fecha: 2026-10-01

## Decisión
Módulo `Certificates` (esquema `certificates`, RLS por tenant) que guarda el certificado digital de cada empresa para firmar CPE.

- **Cifrado en reposo**: el PKCS#12 se abre al cargarlo, se vuelve a exportar sin contraseña y se cifra con `ISecretProtector` (propósito `certificates.pfx`, cifrado de sobre AES-GCM, ADR-007). Ni el archivo, ni la contraseña, ni la clave aparecen en respuestas, registros ni auditoría (una prueba lo comprueba). El texto cifrado no es un PKCS#12 legible.
- **Validación al cargar**: clave privada presente, RSA ≥ 2048, vigente ahora, contraseña correcta (mismo mensaje para contraseña errónea y archivo corrupto, para no ayudar a adivinarla) y tamaño ≤ 100 KB. Si el sujeto contiene números de 11 dígitos y ninguno es el RUC de la empresa, se rechaza (certificado de otro contribuyente). Si no contiene ninguno se acepta pero se marca `rucInSubject = false` (R-036).
- **Un solo certificado activo por empresa**, garantizado por un índice único parcial; subir uno nuevo desactiva el anterior en la misma transacción. Los certificados **nunca se borran** (el rol de la aplicación no tiene `DELETE`): los documentos firmados deben seguir siendo verificables.
- **Consumo**: `ICertificateProvider.GetActiveSigningCertificateAsync` devuelve el certificado descifrado solo en memoria (claves efímeras, buffers puestos a cero) y falla con `SF-CRT-004` si no hay uno activo y vigente. Respeta el aislamiento por tenant.
- **Alertas de vencimiento**: `GET /api/v1/certificates/expiring?days=N`.
- **Permisos**: `certificates.manage` (TenantOwner, TenantAdmin, PlatformSuperAdmin) y `certificates.read` (además, Auditor). Ventas, contabilidad y solo lectura no tienen acceso. Todas las cargas y desactivaciones se auditan (huella, sujeto, vencimiento; nunca material de clave).

## No decidido
- **Confianza de la cadena**: no se valida contra las autoridades certificadoras aceptadas por SUNAT (lista por confirmar, R-037). Se aceptan autofirmados para pruebas en el beta.
- El protector de secretos de producción sigue pendiente (KMS/Vault, ADR-007): el KEK local es solo para desarrollo.
