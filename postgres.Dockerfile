# Base pgvector + PostGIS ajouté, et non l'inverse : les images postgis/postgis:16-* sont sur
# Debian bullseye, que apt.postgresql.org a retiré (404 sur bullseye-pgdg), donc
# `apt-get install postgresql-16-pgvector` y échoue avec le code 100. pgvector/pgvector:pg16
# suit la Debian courante, où PostGIS est disponible dans le dépôt PGDG déjà configuré.
# Rester sur PostgreSQL 16 : changer de version majeure rendrait le volume existant illisible.
FROM pgvector/pgvector:pg16

RUN apt-get update \
    && apt-get install -y --no-install-recommends postgresql-16-postgis-3 postgresql-16-postgis-3-scripts \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

COPY init-extensions.sql /docker-entrypoint-initdb.d/10-init-extensions.sql

HEALTHCHECK --interval=5s --timeout=5s --start-period=60s --retries=10 \
    CMD pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB" || exit 1
