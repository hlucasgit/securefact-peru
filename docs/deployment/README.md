# Despliegue

Local: `docker compose up -d` (ver `README.md`). Entornos: Local, Test, Staging, Production, con credenciales y certificados separados. Producción: balanceador + múltiples instancias API/Worker + PostgreSQL HA con PITR + Redis + RabbitMQ HA + object storage S3. Kubernetes no es requisito del MVP pero no se impide.

Dominios propios de los revendedores y sus certificados: `domains.md` (ADR-051).
