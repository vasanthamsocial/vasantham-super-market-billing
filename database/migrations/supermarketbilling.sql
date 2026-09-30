CREATE TABLE IF NOT EXISTS __ef_migrations_history (
    migration_id character varying(150) NOT NULL,
    product_version character varying(32) NOT NULL,
    CONSTRAINT pk___ef_migrations_history PRIMARY KEY (migration_id)
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260929153130_InitialFoundation') THEN
    CREATE TABLE audit_events (
        id uuid NOT NULL,
        sequence bigint GENERATED ALWAYS AS IDENTITY,
        occurred_at_utc timestamp with time zone NOT NULL,
        event_type character varying(100) NOT NULL,
        entity_type character varying(100),
        entity_id character varying(100),
        actor_user_id uuid,
        business_id uuid,
        store_id uuid,
        correlation_id character varying(100),
        payload_json jsonb NOT NULL,
        CONSTRAINT pk_audit_events PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260929153130_InitialFoundation') THEN
    CREATE INDEX ix_audit_events_business_id_store_id_occurred_at_utc ON audit_events (business_id, store_id, occurred_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260929153130_InitialFoundation') THEN
    CREATE INDEX ix_audit_events_entity_type_entity_id ON audit_events (entity_type, entity_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260929153130_InitialFoundation') THEN
    CREATE INDEX ix_audit_events_occurred_at_utc ON audit_events (occurred_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260929153130_InitialFoundation') THEN
    CREATE UNIQUE INDEX ix_audit_events_sequence ON audit_events (sequence);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260929153130_InitialFoundation') THEN
    ALTER TABLE audit_events ADD CONSTRAINT ck_audit_events_payload_is_object CHECK (jsonb_typeof(payload_json) = 'object');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260929153130_InitialFoundation') THEN
    ALTER TABLE audit_events ADD CONSTRAINT ck_audit_events_event_type_not_blank CHECK (btrim(event_type) <> '');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260929153130_InitialFoundation') THEN
    CREATE OR REPLACE FUNCTION sb_reject_mutation() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        RAISE EXCEPTION 'Table % is append-only; % is not permitted. Post a correcting entry instead.',
            TG_TABLE_NAME, TG_OP
            USING ERRCODE = 'restrict_violation';
    END;
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260929153130_InitialFoundation') THEN
    CREATE TRIGGER trg_audit_events_no_update_delete
        BEFORE UPDATE OR DELETE ON audit_events
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_audit_events_no_truncate
        BEFORE TRUNCATE ON audit_events
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260929153130_InitialFoundation') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20260929153130_InitialFoundation', '10.0.12');
    END IF;
END $EF$;
COMMIT;

