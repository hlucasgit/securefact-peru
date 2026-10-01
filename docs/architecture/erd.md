# ERD inicial

Principios:

- **Un esquema PostgreSQL por módulo** (`identity`, `tenancy`, `org`, `catalog`, `billing`, `cpe`, `cert`, `sunat`, `webhook`, `dev`, `sub`, `audit`, …). **Sin claves foráneas entre esquemas de módulos distintos**: se referencian por ID (GUID) y se validan por contrato.
- Toda tabla de negocio lleva `tenant_id uuid NOT NULL` y política **RLS** (ADR-003). Tablas de plataforma (catálogos globales, planes) no llevan `tenant_id`.
- Claves primarias `uuid` (v7, ordenables). Marcas de tiempo `timestamptz` en UTC; fechas tributarias `date` (campo `issue_date`) separadas de `issued_at`.
- Dinero: `numeric(18,4)` (precisión final la fija TaxEngine); porcentajes `numeric(9,6)`.
- Concurrencia optimista: columna `xmin`/`row_version` en agregados con estado.
- Tablas append-only (`audit.event`, `cpe.document_status_history`, `sub.usage_record`, `cpe.submission_attempt`) sin `UPDATE/DELETE` para el rol de aplicación.

## 1. Tenancy, organizaciones e identidad

```mermaid
erDiagram
  RESELLER ||--o{ TENANT : "gestiona"
  TENANT ||--o{ COMPANY : "posee"
  COMPANY ||--o{ ESTABLISHMENT : "tiene"
  TENANT ||--o{ APP_USER : "miembros"
  APP_USER ||--o{ USER_ROLE : ""
  ROLE ||--o{ USER_ROLE : ""
  ROLE ||--o{ ROLE_PERMISSION : ""
  PERMISSION ||--o{ ROLE_PERMISSION : ""
  APP_USER ||--o{ USER_SESSION : ""
  APP_USER ||--o{ MFA_CREDENTIAL : ""
  TENANT ||--o{ API_CLIENT : ""
  API_CLIENT ||--o{ API_KEY : ""

  TENANT { uuid id PK  uuid reseller_id FK  text name  text status  text environment }
  COMPANY { uuid id PK  uuid tenant_id  char11 ruc  text legal_name  text trade_name  text fiscal_address  char6 ubigeo  text tax_regime  text default_currency  text time_zone  text environment  text status }
  ESTABLISHMENT { uuid id PK  uuid tenant_id  uuid company_id FK  text code  text name  text address  char6 ubigeo }
  APP_USER { uuid id PK  uuid tenant_id  text email  text password_hash  int failed_attempts  timestamptz locked_until  bool mfa_enabled }
  ROLE { uuid id PK  uuid tenant_id  text code }
  PERMISSION { text code PK }
  USER_SESSION { uuid id PK  uuid user_id FK  text refresh_hash  timestamptz expires_at  timestamptz revoked_at  inet ip }
  API_CLIENT { uuid id PK  uuid tenant_id  uuid company_id  text name }
  API_KEY { uuid id PK  uuid api_client_id FK  text key_prefix  text key_hash  text[] scopes  timestamptz expires_at  timestamptz last_used_at  timestamptz revoked_at  cidr[] allowed_ips }
```

Nota: `RESELLER` pertenece a la plataforma y no lleva `tenant_id`; un tenant apunta opcionalmente a su reseller. Los permisos del reseller sobre un tenant son explícitos (`reseller_grant`), nunca implícitos.

## 2. Catálogos, clientes y productos

```mermaid
erDiagram
  CATALOG ||--o{ CATALOG_ENTRY : ""
  CATALOG { text code PK  text description }
  CATALOG_ENTRY { uuid id PK  text catalog_code FK  text code  text description  date effective_from  date effective_to  int version  text source  bool active  jsonb metadata }
  CUSTOMER { uuid id PK  uuid tenant_id  text doc_type_code  text doc_number  text legal_name  text address  text email  bool active }
  PRODUCT { uuid id PK  uuid tenant_id  text internal_code  text description  text kind  text unit_code  numeric unit_price  text tax_affectation_code  text sunat_product_code  bool active }
  RULE { text code  int version  date effective_from  date effective_to  jsonb configuration  text source }
```

## 3. Facturación y documento electrónico

```mermaid
erDiagram
  SERIES ||--|| SEQUENCE : "1:1"
  DOCUMENT ||--o{ DOCUMENT_LINE : ""
  DOCUMENT ||--o{ DOCUMENT_TAX : ""
  DOCUMENT ||--o{ ALLOWANCE_CHARGE : ""
  DOCUMENT ||--o{ PAYMENT_TERM : ""
  DOCUMENT ||--o{ RELATED_DOCUMENT : "origen (NC/ND)"
  DOCUMENT ||--|| ELECTRONIC_DOCUMENT : "1:1"
  ELECTRONIC_DOCUMENT ||--o{ ELECTRONIC_DOCUMENT_FILE : ""
  ELECTRONIC_DOCUMENT ||--o{ SUBMISSION : ""
  SUBMISSION ||--o{ SUBMISSION_ATTEMPT : ""
  ELECTRONIC_DOCUMENT ||--o| CDR : ""
  ELECTRONIC_DOCUMENT ||--o{ DOCUMENT_STATUS_HISTORY : ""

  SERIES { uuid id PK  uuid tenant_id  uuid company_id  uuid establishment_id  text doc_type_code  text series_code  bool active }
  SEQUENCE { uuid series_id PK  bigint last_number }
  DOCUMENT { uuid id PK  uuid tenant_id  uuid company_id  uuid customer_id  text doc_type_code  text series_code  bigint number  date issue_date  timestamptz issued_at  text currency  jsonb original_request  jsonb normalized  text request_hash }
  DOCUMENT_LINE { uuid id PK  uuid document_id FK  int line_no  text description  numeric quantity  text unit_code  numeric unit_value  text tax_affectation_code  numeric line_total }
  DOCUMENT_TAX { uuid id PK  uuid document_id FK  text tax_code  numeric taxable_base  numeric amount }
  ALLOWANCE_CHARGE { uuid id PK  uuid document_id FK  text kind  text reason_code  numeric amount }
  PAYMENT_TERM { uuid id PK  uuid document_id FK  text mode  numeric amount  date due_date }
  RELATED_DOCUMENT { uuid id PK  uuid document_id FK  uuid origin_document_id  text relation_code  text reason_code }
  ELECTRONIC_DOCUMENT { uuid id PK  uuid tenant_id  uuid document_id FK  text status  text channel  int row_version  timestamptz submit_deadline  timestamptz accepted_at }
  ELECTRONIC_DOCUMENT_FILE { uuid id PK  uuid electronic_document_id FK  text kind  text storage_key  text sha256  bigint size_bytes  text mime  int version  timestamptz created_at }
  SUBMISSION { uuid id PK  uuid electronic_document_id FK  text channel  text ticket  text state }
  SUBMISSION_ATTEMPT { uuid id PK  uuid submission_id FK  int attempt_no  text error_class  text provider_code  int latency_ms  timestamptz at  jsonb sanitized_meta }
  CDR { uuid id PK  uuid electronic_document_id FK  text response_code  text description  text status  jsonb notes  text sha256  text file_id }
  DOCUMENT_STATUS_HISTORY { uuid id PK  uuid electronic_document_id FK  text old_status  text new_status  timestamptz at  text actor  text correlation_id  text reason  jsonb metadata }
```

Reglas de integridad clave:

- Unicidad `UNIQUE (tenant_id, company_id, doc_type_code, series_code, number)`.
- Idempotencia: tabla `api.idempotency_key (tenant_id, key, request_hash, response_ref, expires_at)` con `UNIQUE (tenant_id, key)`.
- `SEQUENCE` se incrementa con `UPDATE ... SET last_number = last_number + 1 ... RETURNING` dentro de la misma transacción que crea el documento (nunca `MAX()+1`).
- Un documento aceptado es inmutable: triggers/permiso impiden `UPDATE` de columnas fiscales y de `electronic_document_file`.
- `RELATED_DOCUMENT` impide notas huérfanas (`origin_document_id` obligatorio para NC/ND).

## 4. Certificados, canales y secretos

```mermaid
erDiagram
  CERTIFICATE { uuid id PK  uuid tenant_id  uuid company_id  text subject  text serial  timestamptz not_after  text key_ref  text status }
  PROVIDER { text code PK  text kind  text description }
  PROVIDER_CONFIGURATION { uuid id PK  uuid tenant_id  uuid company_id  text provider_code FK  text environment  jsonb settings  text secret_ref  bool active }
  SECRET { uuid id PK  text purpose  bytea ciphertext  bytea wrapped_dek  text kek_id  int version }
```

`key_ref` y `secret_ref` apuntan a `SECRET` (cifrado de envoltura: DEK por secreto cifrada con KEK; ADR-007). Los PFX y claves SOL nunca se guardan en claro ni en columnas legibles.

## 5. Webhooks, API, suscripciones, auditoría

```mermaid
erDiagram
  WEBHOOK_ENDPOINT ||--o{ WEBHOOK_DELIVERY : ""
  PLAN ||--o{ SUBSCRIPTION : ""
  SUBSCRIPTION ||--o{ USAGE_RECORD : ""
  WEBHOOK_ENDPOINT { uuid id PK  uuid tenant_id  text url  text secret_ref  text[] events  bool active }
  WEBHOOK_DELIVERY { uuid id PK  uuid endpoint_id FK  text event_id  int attempt  int http_status  timestamptz next_retry_at  text state }
  PLAN { uuid id PK  text code  jsonb limits }
  SUBSCRIPTION { uuid id PK  uuid tenant_id  uuid plan_id FK  text status  date period_start  date period_end }
  USAGE_RECORD { uuid id PK  uuid tenant_id  text meter  numeric quantity  timestamptz at  text idempotency_key }
  AUDIT_EVENT { uuid id PK  uuid tenant_id  text actor  text action  text entity  text entity_id  jsonb old_values  jsonb new_values  inet ip  text user_agent  text correlation_id  text request_id  timestamptz at  bytea prev_hash  bytea hash }
  OUTBOX_MESSAGE { uuid id PK  uuid tenant_id  text type  int version  jsonb payload  timestamptz created_at  timestamptz published_at }
  INBOX_MESSAGE { text consumer  uuid message_id  timestamptz processed_at }
```

`AUDIT_EVENT` es append-only con cadena de hashes (`prev_hash → hash`) para detectar manipulación. `OUTBOX_MESSAGE` vive en el esquema de cada módulo que publica; `INBOX_MESSAGE` garantiza idempotencia de consumidores.

## 6. Entidades futuras (no se modelan aún)

`SUPPLIER`, `GRE_*` (guías, vehículos, conductores), `WITHHOLDING_*`, `PERCEPTION_*`, `TAX_BOOK_*` (SIRE), `FACTORING_*`. Se diseñan al iniciar sus fases, tras revisar la fuente oficial correspondiente.
