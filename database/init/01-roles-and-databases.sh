#!/bin/sh
# Runs once, when the PostgreSQL data volume is first initialised.
#
# Role model (least privilege):
#   superuser  (POSTGRES_USER)       container administration only; never used by the application
#   migrator   (SB_DB_MIGRATOR_USER) owns the schema; applies migrations (DDL)
#   app        (SB_DB_APP_USER)      runtime API account; DML only, cannot create, alter or drop objects
set -eu

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v migrator="$SB_DB_MIGRATOR_USER" -v migrator_pw="$SB_DB_MIGRATOR_PASSWORD" \
  -v app="$SB_DB_APP_USER" -v app_pw="$SB_DB_APP_PASSWORD" <<'EOSQL'
CREATE ROLE :"migrator" LOGIN PASSWORD :'migrator_pw' NOSUPERUSER NOCREATEDB NOCREATEROLE;
CREATE ROLE :"app" LOGIN PASSWORD :'app_pw' NOSUPERUSER NOCREATEDB NOCREATEROLE CONNECTION LIMIT 200;
EOSQL

configure_database() {
  db="$1"
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$db" \
    -v db="$db" -v migrator="$SB_DB_MIGRATOR_USER" -v app="$SB_DB_APP_USER" <<'EOSQL'
REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE :"db" TO :"migrator";
GRANT CONNECT ON DATABASE :"db" TO :"app";

REVOKE ALL ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO :"migrator";
GRANT USAGE ON SCHEMA public TO :"app";

-- Every object the migrator creates is automatically usable (but not alterable) by the runtime account.
ALTER DEFAULT PRIVILEGES FOR ROLE :"migrator" IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"app";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migrator" IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO :"app";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migrator" IN SCHEMA public
  GRANT EXECUTE ON FUNCTIONS TO :"app";
EOSQL
}

configure_database "$POSTGRES_DB"

# Additional databases (for example the integration-test database) get the same role model.
for extra in ${SB_DB_EXTRA_DATABASES:-}; do
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
    -v db="$extra" -v migrator="$SB_DB_MIGRATOR_USER" <<'EOSQL'
CREATE DATABASE :"db" OWNER :"migrator" ENCODING 'UTF8' TEMPLATE template0;
EOSQL
  configure_database "$extra"
done

echo "SupermarketBilling roles and databases initialised."
