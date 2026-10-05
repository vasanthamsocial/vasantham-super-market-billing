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

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE TABLE businesses (
        id uuid NOT NULL,
        code character varying(12) NOT NULL,
        legal_name character varying(200) NOT NULL,
        trade_name character varying(200) NOT NULL,
        gstin character(15),
        state_code character(2) NOT NULL,
        address character varying(500),
        is_active boolean NOT NULL,
        require_mfa_for_privileged_users boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT pk_businesses PRIMARY KEY (id),
        CONSTRAINT ck_businesses_code CHECK (code ~ '^[A-Z0-9]{2,12}$'),
        CONSTRAINT ck_businesses_gstin_state CHECK (gstin IS NULL OR left(gstin, 2) = state_code),
        CONSTRAINT ck_businesses_state_code CHECK (state_code ~ '^[0-9]{2}$' AND state_code <> '00')
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE TABLE users (
        id uuid NOT NULL,
        username character varying(50) NOT NULL,
        display_name character varying(100) NOT NULL,
        is_active boolean NOT NULL,
        password_hash character varying(200) NOT NULL,
        password_changed_at_utc timestamp with time zone NOT NULL,
        must_change_password boolean NOT NULL,
        failed_login_count integer NOT NULL,
        locked_until_utc timestamp with time zone,
        last_login_at_utc timestamp with time zone,
        mfa_enabled boolean NOT NULL,
        mfa_secret_protected character varying(200),
        mfa_pending_secret_protected character varying(200),
        mfa_last_used_step bigint,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT pk_users PRIMARY KEY (id),
        CONSTRAINT ck_users_failed_login_count CHECK (failed_login_count >= 0),
        CONSTRAINT ck_users_mfa_secret CHECK (NOT mfa_enabled OR mfa_secret_protected IS NOT NULL),
        CONSTRAINT ck_users_username CHECK (username ~ '^[a-z0-9][a-z0-9._-]{2,49}$')
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE TABLE stores (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        code character varying(12) NOT NULL,
        name character varying(120) NOT NULL,
        state_code character(2) NOT NULL,
        gstin character(15),
        address character varying(500),
        time_zone character varying(64) NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT pk_stores PRIMARY KEY (id),
        CONSTRAINT ak_stores_id_business_id UNIQUE (id, business_id),
        CONSTRAINT ck_stores_code CHECK (code ~ '^[A-Z0-9][A-Z0-9-]{0,11}$'),
        CONSTRAINT ck_stores_gstin_state CHECK (gstin IS NULL OR left(gstin, 2) = state_code),
        CONSTRAINT ck_stores_state_code CHECK (state_code ~ '^[0-9]{2}$' AND state_code <> '00'),
        CONSTRAINT fk_stores_businesses_business_id FOREIGN KEY (business_id) REFERENCES businesses (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE TABLE approval_requests (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        type character varying(50) NOT NULL,
        summary character varying(500) NOT NULL,
        payload_json jsonb NOT NULL,
        reason character varying(500),
        status character varying(20) NOT NULL,
        requested_by_user_id uuid NOT NULL,
        requested_at_utc timestamp with time zone NOT NULL,
        expires_at_utc timestamp with time zone NOT NULL,
        decided_by_user_id uuid,
        decided_at_utc timestamp with time zone,
        decision_note character varying(500),
        CONSTRAINT pk_approval_requests PRIMARY KEY (id),
        CONSTRAINT ck_approval_requests_decision CHECK ((status IN ('approved', 'rejected')) = (decided_by_user_id IS NOT NULL AND decided_at_utc IS NOT NULL)),
        CONSTRAINT ck_approval_requests_maker_checker CHECK (decided_by_user_id IS NULL OR decided_by_user_id <> requested_by_user_id),
        CONSTRAINT ck_approval_requests_payload CHECK (jsonb_typeof(payload_json) = 'object'),
        CONSTRAINT ck_approval_requests_status CHECK (status IN ('pending', 'approved', 'rejected', 'cancelled', 'expired')),
        CONSTRAINT fk_approval_requests_businesses_business_id FOREIGN KEY (business_id) REFERENCES businesses (id) ON DELETE RESTRICT,
        CONSTRAINT fk_approval_requests_users_decided_by_user_id FOREIGN KEY (decided_by_user_id) REFERENCES users (id) ON DELETE RESTRICT,
        CONSTRAINT fk_approval_requests_users_requested_by_user_id FOREIGN KEY (requested_by_user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE TABLE mfa_recovery_codes (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        code_hash bytea NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        used_at_utc timestamp with time zone,
        CONSTRAINT pk_mfa_recovery_codes PRIMARY KEY (id),
        CONSTRAINT fk_mfa_recovery_codes_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE TABLE password_reset_tokens (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        token_hash bytea NOT NULL,
        issued_by_user_id uuid NOT NULL,
        issued_at_utc timestamp with time zone NOT NULL,
        expires_at_utc timestamp with time zone NOT NULL,
        used_at_utc timestamp with time zone,
        CONSTRAINT pk_password_reset_tokens PRIMARY KEY (id),
        CONSTRAINT fk_password_reset_tokens_users_issued_by_user_id FOREIGN KEY (issued_by_user_id) REFERENCES users (id) ON DELETE RESTRICT,
        CONSTRAINT fk_password_reset_tokens_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE TABLE sessions (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        token_hash bytea NOT NULL,
        csrf_token_hash bytea NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        last_seen_at_utc timestamp with time zone NOT NULL,
        idle_expires_at_utc timestamp with time zone NOT NULL,
        absolute_expires_at_utc timestamp with time zone NOT NULL,
        mfa_satisfied boolean NOT NULL,
        ip_address character varying(64),
        user_agent character varying(300),
        revoked_at_utc timestamp with time zone,
        revoked_reason character varying(50),
        CONSTRAINT pk_sessions PRIMARY KEY (id),
        CONSTRAINT ck_sessions_expiry CHECK (idle_expires_at_utc > created_at_utc AND absolute_expires_at_utc > created_at_utc),
        CONSTRAINT ck_sessions_token_hash CHECK (octet_length(token_hash) = 32 AND octet_length(csrf_token_hash) = 32),
        CONSTRAINT fk_sessions_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE TABLE role_assignments (
        id uuid NOT NULL,
        user_id uuid NOT NULL,
        role_code character varying(40) NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid,
        granted_by_user_id uuid,
        granted_at_utc timestamp with time zone NOT NULL,
        approval_request_id uuid,
        revoked_by_user_id uuid,
        revoked_at_utc timestamp with time zone,
        CONSTRAINT pk_role_assignments PRIMARY KEY (id),
        CONSTRAINT ck_role_assignments_business_wide CHECK (role_code NOT IN ('accountant', 'auditor', 'owner', 'support_admin') OR store_id IS NULL),
        CONSTRAINT ck_role_assignments_revocation CHECK ((revoked_at_utc IS NULL) = (revoked_by_user_id IS NULL)),
        CONSTRAINT ck_role_assignments_role_code CHECK (role_code IN ('accountant', 'auditor', 'cashier', 'collection_manager', 'collection_person', 'inventory_operator', 'manager', 'owner', 'purchase_operator', 'support_admin')),
        CONSTRAINT fk_role_assignments_approval_requests_approval_request_id FOREIGN KEY (approval_request_id) REFERENCES approval_requests (id) ON DELETE RESTRICT,
        CONSTRAINT fk_role_assignments_businesses_business_id FOREIGN KEY (business_id) REFERENCES businesses (id) ON DELETE RESTRICT,
        CONSTRAINT fk_role_assignments_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_role_assignments_users_granted_by_user_id FOREIGN KEY (granted_by_user_id) REFERENCES users (id) ON DELETE RESTRICT,
        CONSTRAINT fk_role_assignments_users_revoked_by_user_id FOREIGN KEY (revoked_by_user_id) REFERENCES users (id) ON DELETE RESTRICT,
        CONSTRAINT fk_role_assignments_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE INDEX ix_approval_requests_business_id_status ON approval_requests (business_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE INDEX ix_approval_requests_decided_by_user_id ON approval_requests (decided_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE INDEX ix_approval_requests_requested_by_user_id ON approval_requests (requested_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE UNIQUE INDEX ix_businesses_code ON businesses (code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE UNIQUE INDEX ix_businesses_gstin ON businesses (gstin) WHERE gstin IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE UNIQUE INDEX ix_mfa_recovery_codes_user_id_code_hash ON mfa_recovery_codes (user_id, code_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE INDEX ix_password_reset_tokens_issued_by_user_id ON password_reset_tokens (issued_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE UNIQUE INDEX ix_password_reset_tokens_token_hash ON password_reset_tokens (token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE INDEX ix_password_reset_tokens_user_id ON password_reset_tokens (user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE INDEX ix_role_assignments_approval_request_id ON role_assignments (approval_request_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE INDEX ix_role_assignments_business_id ON role_assignments (business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE INDEX ix_role_assignments_granted_by_user_id ON role_assignments (granted_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE INDEX ix_role_assignments_revoked_by_user_id ON role_assignments (revoked_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE INDEX ix_role_assignments_store_id_business_id ON role_assignments (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE UNIQUE INDEX ux_role_assignments_active ON role_assignments (user_id, role_code, business_id, store_id) NULLS NOT DISTINCT WHERE revoked_at_utc IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE UNIQUE INDEX ix_sessions_token_hash ON sessions (token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE INDEX ix_sessions_user_id ON sessions (user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE UNIQUE INDEX ix_stores_business_id_code ON stores (business_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    CREATE UNIQUE INDEX ix_users_username ON users (username);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930105226_IdentityAndOrganisation') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20260930105226_IdentityAndOrganisation', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE approval_requests DROP CONSTRAINT fk_approval_requests_businesses_business_id;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE role_assignments DROP CONSTRAINT fk_role_assignments_businesses_business_id;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE role_assignments DROP CONSTRAINT fk_role_assignments_users_user_id;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE sessions DROP CONSTRAINT fk_sessions_users_user_id;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE stores DROP CONSTRAINT fk_stores_businesses_business_id;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    DROP INDEX ix_users_username;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    DROP INDEX ix_businesses_code;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    DROP INDEX ix_businesses_gstin;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE users ADD tenant_id uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE stores ADD tenant_id uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE sessions ADD tenant_id uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE role_assignments ADD tenant_id uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE password_reset_tokens ADD tenant_id uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE mfa_recovery_codes ADD tenant_id uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE businesses ADD tenant_id uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE audit_events ADD tenant_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE approval_requests ADD tenant_id uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE users ADD CONSTRAINT ak_users_id_tenant_id UNIQUE (id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE businesses ADD CONSTRAINT ak_businesses_id_tenant_id UNIQUE (id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE TABLE tenants (
        id uuid NOT NULL,
        code character varying(20) NOT NULL,
        name character varying(200) NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT pk_tenants PRIMARY KEY (id),
        CONSTRAINT ck_tenants_code CHECK (code ~ '^[A-Z0-9]{3,20}$')
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE TABLE installation (
        id smallint NOT NULL,
        installation_id uuid NOT NULL,
        tenant_id uuid,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT pk_installation PRIMARY KEY (id),
        CONSTRAINT ck_installation_singleton CHECK (id = 1),
        CONSTRAINT fk_installation_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    INSERT INTO installation (id, installation_id, tenant_id, created_at_utc)
    VALUES (1, gen_random_uuid(), NULL, now());

    DO $$
    DECLARE
        first_business record;
        new_tenant uuid;
    BEGIN
        SELECT * INTO first_business FROM businesses ORDER BY created_at_utc LIMIT 1;
        IF NOT FOUND THEN
            RETURN;
        END IF;

        new_tenant := gen_random_uuid();
        INSERT INTO tenants (id, code, name, is_active, created_at_utc)
        VALUES (new_tenant, rpad(first_business.code, 3, 'X'), first_business.legal_name, true, now());
        UPDATE businesses SET tenant_id = new_tenant;
        UPDATE stores SET tenant_id = new_tenant;
        UPDATE users SET tenant_id = new_tenant;
        UPDATE role_assignments SET tenant_id = new_tenant;
        UPDATE sessions SET tenant_id = new_tenant;
        UPDATE password_reset_tokens SET tenant_id = new_tenant;
        UPDATE mfa_recovery_codes SET tenant_id = new_tenant;
        UPDATE approval_requests SET tenant_id = new_tenant;
        ALTER TABLE audit_events DISABLE TRIGGER trg_audit_events_no_update_delete;
        UPDATE audit_events SET tenant_id = new_tenant;
        ALTER TABLE audit_events ENABLE TRIGGER trg_audit_events_no_update_delete;
        UPDATE installation SET tenant_id = new_tenant WHERE id = 1;
    END
    $$;

    ALTER TABLE businesses ALTER COLUMN tenant_id DROP DEFAULT;
    ALTER TABLE stores ALTER COLUMN tenant_id DROP DEFAULT;
    ALTER TABLE users ALTER COLUMN tenant_id DROP DEFAULT;
    ALTER TABLE role_assignments ALTER COLUMN tenant_id DROP DEFAULT;
    ALTER TABLE sessions ALTER COLUMN tenant_id DROP DEFAULT;
    ALTER TABLE password_reset_tokens ALTER COLUMN tenant_id DROP DEFAULT;
    ALTER TABLE mfa_recovery_codes ALTER COLUMN tenant_id DROP DEFAULT;
    ALTER TABLE approval_requests ALTER COLUMN tenant_id DROP DEFAULT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE INDEX ix_users_tenant_id ON users (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE UNIQUE INDEX ux_users_tenant_username ON users (tenant_id, username);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE INDEX ix_stores_business_id_tenant_id ON stores (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE INDEX ix_stores_tenant_id ON stores (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE INDEX ix_sessions_tenant_id ON sessions (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE INDEX ix_sessions_user_id_tenant_id ON sessions (user_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE INDEX ix_role_assignments_business_id_tenant_id ON role_assignments (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE INDEX ix_role_assignments_tenant_id ON role_assignments (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE INDEX ix_role_assignments_user_id_tenant_id ON role_assignments (user_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE INDEX ix_password_reset_tokens_tenant_id ON password_reset_tokens (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE INDEX ix_mfa_recovery_codes_tenant_id ON mfa_recovery_codes (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE INDEX ix_businesses_tenant_id ON businesses (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE UNIQUE INDEX ux_businesses_tenant_code ON businesses (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE UNIQUE INDEX ux_businesses_tenant_gstin ON businesses (tenant_id, gstin) WHERE gstin IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE INDEX ix_audit_events_tenant_id_sequence ON audit_events (tenant_id, sequence);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE INDEX ix_approval_requests_business_id_tenant_id ON approval_requests (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE INDEX ix_approval_requests_tenant_id ON approval_requests (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE INDEX ix_installation_tenant_id ON installation (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE UNIQUE INDEX ix_tenants_code ON tenants (code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE approval_requests ADD CONSTRAINT fk_approval_requests_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE approval_requests ADD CONSTRAINT fk_approval_requests_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE audit_events ADD CONSTRAINT fk_audit_events_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE businesses ADD CONSTRAINT fk_businesses_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE mfa_recovery_codes ADD CONSTRAINT fk_mfa_recovery_codes_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE password_reset_tokens ADD CONSTRAINT fk_password_reset_tokens_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE role_assignments ADD CONSTRAINT fk_role_assignments_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE role_assignments ADD CONSTRAINT fk_role_assignments_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE role_assignments ADD CONSTRAINT fk_role_assignments_users_user_id_tenant_id FOREIGN KEY (user_id, tenant_id) REFERENCES users (id, tenant_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE sessions ADD CONSTRAINT fk_sessions_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE sessions ADD CONSTRAINT fk_sessions_users_user_id_tenant_id FOREIGN KEY (user_id, tenant_id) REFERENCES users (id, tenant_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE stores ADD CONSTRAINT fk_stores_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE stores ADD CONSTRAINT fk_stores_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    ALTER TABLE users ADD CONSTRAINT fk_users_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    CREATE FUNCTION sb_current_tenant() RETURNS uuid
        LANGUAGE sql STABLE
        AS $$ SELECT nullif(current_setting('sb.tenant_id', true), '')::uuid $$;

    ALTER TABLE businesses ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON businesses
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE stores ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON stores
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE users ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON users
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE role_assignments ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON role_assignments
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE sessions ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON sessions
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE password_reset_tokens ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON password_reset_tokens
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE mfa_recovery_codes ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON mfa_recovery_codes
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE approval_requests ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON approval_requests
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());

    ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON tenants
        USING (id = sb_current_tenant())
        WITH CHECK (id = sb_current_tenant());

    -- Audit events: each tenant sees its own. Events recorded before any tenant was known (for example a
    -- cloud sign-in with an unknown company code) belong to no tenant and are visible only without one.
    ALTER TABLE audit_events ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_read ON audit_events FOR SELECT
        USING (tenant_id = sb_current_tenant() OR (tenant_id IS NULL AND sb_current_tenant() IS NULL));
    CREATE POLICY tenant_append ON audit_events FOR INSERT
        WITH CHECK (tenant_id IS NULL OR tenant_id = sb_current_tenant());
    -- UPDATE and DELETE are always rejected by the append-only trigger. These policies only let such an
    -- attempt reach the trigger, so tampering fails loudly instead of silently matching no rows.
    CREATE POLICY tenant_tamper_detection_update ON audit_events FOR UPDATE
        USING (tenant_id = sb_current_tenant());
    CREATE POLICY tenant_tamper_detection_delete ON audit_events FOR DELETE
        USING (tenant_id = sb_current_tenant());

    -- Lookups needed before the tenant is known. They return only an id, never row data.
    CREATE FUNCTION sb_tenant_by_code(p_code text) RETURNS uuid
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public
        AS $$ SELECT id FROM public.tenants WHERE code = p_code AND is_active $$;
    CREATE FUNCTION sb_session_tenant(p_token_hash bytea) RETURNS uuid
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public
        AS $$ SELECT tenant_id FROM public.sessions WHERE token_hash = p_token_hash AND revoked_at_utc IS NULL $$;
    REVOKE ALL ON FUNCTION sb_tenant_by_code(text) FROM PUBLIC;
    REVOKE ALL ON FUNCTION sb_session_tenant(bytea) FROM PUBLIC;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930123709_Tenancy') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20260930123709_Tenancy', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    ALTER TABLE businesses ADD require_price_approval boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE TABLE brands (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        name character varying(80) NOT NULL,
        is_active boolean NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_brands PRIMARY KEY (id),
        CONSTRAINT ak_brands_id_business_id UNIQUE (id, business_id),
        CONSTRAINT fk_brands_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_brands_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE TABLE categories (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        parent_id uuid,
        name character varying(80) NOT NULL,
        is_active boolean NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_categories PRIMARY KEY (id),
        CONSTRAINT ak_categories_id_business_id UNIQUE (id, business_id),
        CONSTRAINT fk_categories_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_categories_categories_parent_id_business_id FOREIGN KEY (parent_id, business_id) REFERENCES categories (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_categories_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE TABLE customer_groups (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        code character varying(20) NOT NULL,
        name character varying(80) NOT NULL,
        is_active boolean NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_customer_groups PRIMARY KEY (id),
        CONSTRAINT ak_customer_groups_id_business_id UNIQUE (id, business_id),
        CONSTRAINT fk_customer_groups_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_customer_groups_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE TABLE tax_registrations (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        mode character varying(20) NOT NULL,
        effective_from date NOT NULL,
        gstin character(15),
        reason character varying(500) NOT NULL,
        evidence_reference character varying(200),
        recorded_by_user_id uuid NOT NULL,
        approval_request_id uuid,
        recorded_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_tax_registrations PRIMARY KEY (id),
        CONSTRAINT ck_tax_registrations_gstin CHECK ((mode = 'NOT_GST_REGISTERED') = (gstin IS NULL)),
        CONSTRAINT ck_tax_registrations_mode CHECK (mode IN ('GST_REGULAR', 'GST_COMPOSITION', 'NOT_GST_REGISTERED')),
        CONSTRAINT fk_tax_registrations_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_tax_registrations_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE TABLE units (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        code character varying(10) NOT NULL,
        name character varying(50) NOT NULL,
        decimal_places integer NOT NULL,
        is_active boolean NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_units PRIMARY KEY (id),
        CONSTRAINT ak_units_id_business_id UNIQUE (id, business_id),
        CONSTRAINT ck_units_decimal_places CHECK (decimal_places BETWEEN 0 AND 3),
        CONSTRAINT fk_units_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_units_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE TABLE products (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        code character varying(30) NOT NULL,
        name character varying(150) NOT NULL,
        print_name character varying(40) NOT NULL,
        category_id uuid,
        brand_id uuid,
        base_unit_id uuid NOT NULL,
        hsn_sac character varying(8) NOT NULL,
        supply_type character varying(12) NOT NULL,
        gst_rate_percent numeric(7,3) NOT NULL,
        cess_rate_percent numeric(7,3) NOT NULL,
        is_weighed boolean NOT NULL,
        tracks_batches boolean NOT NULL,
        tracks_expiry boolean NOT NULL,
        tracks_serials boolean NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_products PRIMARY KEY (id),
        CONSTRAINT ak_products_id_business_id UNIQUE (id, business_id),
        CONSTRAINT ck_products_expiry_needs_batches CHECK (NOT tracks_expiry OR tracks_batches),
        CONSTRAINT ck_products_hsn CHECK (hsn_sac ~ '^([0-9]{4}|[0-9]{6}|[0-9]{8})$'),
        CONSTRAINT ck_products_rates CHECK ((supply_type = 'TAXABLE' AND gst_rate_percent > 0 AND gst_rate_percent <= 100 AND cess_rate_percent >= 0) OR (supply_type <> 'TAXABLE' AND gst_rate_percent = 0 AND cess_rate_percent = 0)),
        CONSTRAINT ck_products_supply_type CHECK (supply_type IN ('TAXABLE', 'EXEMPT', 'NIL_RATED', 'NON_GST')),
        CONSTRAINT fk_products_brands_brand_id_business_id FOREIGN KEY (brand_id, business_id) REFERENCES brands (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_products_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_products_categories_category_id_business_id FOREIGN KEY (category_id, business_id) REFERENCES categories (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_products_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_products_units_base_unit_id_business_id FOREIGN KEY (base_unit_id, business_id) REFERENCES units (id, business_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE TABLE product_variants (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        product_id uuid NOT NULL,
        code character varying(30) NOT NULL,
        name character varying(150) NOT NULL,
        is_active boolean NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_product_variants PRIMARY KEY (id),
        CONSTRAINT ak_product_variants_id_business_id UNIQUE (id, business_id),
        CONSTRAINT fk_product_variants_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_product_variants_products_product_id_business_id FOREIGN KEY (product_id, business_id) REFERENCES products (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_product_variants_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE TABLE variant_units (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        variant_id uuid NOT NULL,
        unit_id uuid NOT NULL,
        factor_to_base numeric(18,6) NOT NULL,
        is_base boolean NOT NULL,
        is_active boolean NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_variant_units PRIMARY KEY (id),
        CONSTRAINT ak_variant_units_id_variant_id UNIQUE (id, variant_id),
        CONSTRAINT ck_variant_units_factor CHECK (factor_to_base > 0 AND (NOT is_base OR factor_to_base = 1)),
        CONSTRAINT fk_variant_units_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_variant_units_product_variants_variant_id_business_id FOREIGN KEY (variant_id, business_id) REFERENCES product_variants (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_variant_units_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_variant_units_units_unit_id_business_id FOREIGN KEY (unit_id, business_id) REFERENCES units (id, business_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE TABLE price_rules (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        variant_id uuid NOT NULL,
        variant_unit_id uuid NOT NULL,
        rate_type character varying(20) NOT NULL,
        channel character varying(10) NOT NULL,
        price numeric(18,4) NOT NULL,
        tax_inclusive boolean NOT NULL,
        mrp numeric(18,2),
        store_id uuid,
        customer_group_id uuid,
        members_only boolean NOT NULL,
        min_quantity numeric(18,3) NOT NULL,
        max_quantity numeric(18,3),
        valid_from_utc timestamp with time zone NOT NULL,
        valid_to_utc timestamp with time zone,
        priority integer NOT NULL,
        status character varying(20) NOT NULL,
        note character varying(300),
        created_by_user_id uuid NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        approval_request_id uuid,
        retired_by_user_id uuid,
        retired_at_utc timestamp with time zone,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_price_rules PRIMARY KEY (id),
        CONSTRAINT ck_price_rules_channel CHECK (channel IN ('RETAIL', 'WHOLESALE', 'ANY')),
        CONSTRAINT ck_price_rules_price CHECK (price > 0 AND (mrp IS NULL OR mrp > 0)),
        CONSTRAINT ck_price_rules_quantity CHECK (min_quantity >= 0 AND (max_quantity IS NULL OR max_quantity > min_quantity)),
        CONSTRAINT ck_price_rules_rate_type CHECK (rate_type IN ('STANDARD', 'STORE', 'QUANTITY_SLAB', 'MEMBER', 'CUSTOMER_GROUP', 'PROMOTIONAL', 'MINIMUM')),
        CONSTRAINT ck_price_rules_retirement CHECK ((status = 'RETIRED') = (retired_at_utc IS NOT NULL)),
        CONSTRAINT ck_price_rules_status CHECK (status IN ('PENDING_APPROVAL', 'ACTIVE', 'REJECTED', 'RETIRED')),
        CONSTRAINT ck_price_rules_validity CHECK (valid_to_utc IS NULL OR valid_to_utc > valid_from_utc),
        CONSTRAINT fk_price_rules_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_price_rules_customer_groups_customer_group_id_business_id FOREIGN KEY (customer_group_id, business_id) REFERENCES customer_groups (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_price_rules_product_variants_variant_id_business_id FOREIGN KEY (variant_id, business_id) REFERENCES product_variants (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_price_rules_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_price_rules_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_price_rules_variant_units_variant_unit_id_variant_id FOREIGN KEY (variant_unit_id, variant_id) REFERENCES variant_units (id, variant_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE TABLE variant_barcodes (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        variant_id uuid NOT NULL,
        variant_unit_id uuid NOT NULL,
        code character varying(20) NOT NULL,
        type character varying(10) NOT NULL,
        is_active boolean NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_variant_barcodes PRIMARY KEY (id),
        CONSTRAINT ck_variant_barcodes_type CHECK (type IN ('GS1', 'INTERNAL')),
        CONSTRAINT fk_variant_barcodes_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_variant_barcodes_product_variants_variant_id_business_id FOREIGN KEY (variant_id, business_id) REFERENCES product_variants (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_variant_barcodes_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_variant_barcodes_variant_units_variant_unit_id_variant_id FOREIGN KEY (variant_unit_id, variant_id) REFERENCES variant_units (id, variant_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE TABLE variant_mrps (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        variant_id uuid NOT NULL,
        variant_unit_id uuid NOT NULL,
        mrp numeric(18,2) NOT NULL,
        effective_from date NOT NULL,
        is_active boolean NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_variant_mrps PRIMARY KEY (id),
        CONSTRAINT ck_variant_mrps_positive CHECK (mrp > 0),
        CONSTRAINT fk_variant_mrps_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_variant_mrps_product_variants_variant_id_business_id FOREIGN KEY (variant_id, business_id) REFERENCES product_variants (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_variant_mrps_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_variant_mrps_variant_units_variant_unit_id_variant_id FOREIGN KEY (variant_unit_id, variant_id) REFERENCES variant_units (id, variant_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE UNIQUE INDEX ix_brands_business_id_name ON brands (business_id, name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_brands_business_id_tenant_id ON brands (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_brands_tenant_id ON brands (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE UNIQUE INDEX ix_categories_business_id_parent_id_name ON categories (business_id, parent_id, name) NULLS NOT DISTINCT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_categories_business_id_tenant_id ON categories (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_categories_parent_id_business_id ON categories (parent_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_categories_tenant_id ON categories (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE UNIQUE INDEX ix_customer_groups_business_id_code ON customer_groups (business_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_customer_groups_business_id_tenant_id ON customer_groups (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_customer_groups_tenant_id ON customer_groups (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_price_rules_business_id_status ON price_rules (business_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_price_rules_business_id_tenant_id ON price_rules (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_price_rules_customer_group_id_business_id ON price_rules (customer_group_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_price_rules_store_id_business_id ON price_rules (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_price_rules_tenant_id ON price_rules (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_price_rules_variant_id_business_id ON price_rules (variant_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_price_rules_variant_unit_id_status ON price_rules (variant_unit_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_price_rules_variant_unit_id_variant_id ON price_rules (variant_unit_id, variant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE UNIQUE INDEX ix_product_variants_business_id_code ON product_variants (business_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_product_variants_business_id_tenant_id ON product_variants (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_product_variants_product_id_business_id ON product_variants (product_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_product_variants_tenant_id ON product_variants (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_products_base_unit_id_business_id ON products (base_unit_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_products_brand_id_business_id ON products (brand_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE UNIQUE INDEX ix_products_business_id_code ON products (business_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_products_business_id_name ON products (business_id, name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_products_business_id_tenant_id ON products (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_products_category_id_business_id ON products (category_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_products_tenant_id ON products (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE UNIQUE INDEX ix_tax_registrations_business_id_effective_from ON tax_registrations (business_id, effective_from);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_tax_registrations_business_id_tenant_id ON tax_registrations (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_tax_registrations_tenant_id ON tax_registrations (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE UNIQUE INDEX ix_units_business_id_code ON units (business_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_units_business_id_tenant_id ON units (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_units_tenant_id ON units (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_variant_barcodes_business_id_tenant_id ON variant_barcodes (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_variant_barcodes_tenant_id ON variant_barcodes (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_variant_barcodes_variant_id_business_id ON variant_barcodes (variant_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_variant_barcodes_variant_unit_id_variant_id ON variant_barcodes (variant_unit_id, variant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE UNIQUE INDEX ux_variant_barcodes_active_code ON variant_barcodes (business_id, code) WHERE is_active;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_variant_mrps_business_id_tenant_id ON variant_mrps (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_variant_mrps_tenant_id ON variant_mrps (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_variant_mrps_variant_id_business_id ON variant_mrps (variant_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_variant_mrps_variant_unit_id_variant_id ON variant_mrps (variant_unit_id, variant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE UNIQUE INDEX ux_variant_mrps_active ON variant_mrps (variant_unit_id, mrp) WHERE is_active;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_variant_units_business_id_tenant_id ON variant_units (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_variant_units_tenant_id ON variant_units (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_variant_units_unit_id_business_id ON variant_units (unit_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE INDEX ix_variant_units_variant_id_business_id ON variant_units (variant_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE UNIQUE INDEX ix_variant_units_variant_id_unit_id ON variant_units (variant_id, unit_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE UNIQUE INDEX ux_variant_units_one_base ON variant_units (variant_id) WHERE is_base;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    ALTER TABLE tax_registrations ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON tax_registrations
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE units ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON units
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE categories ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON categories
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE brands ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON brands
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE customer_groups ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON customer_groups
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE products ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON products
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE product_variants ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON product_variants
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE variant_units ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON variant_units
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE variant_barcodes ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON variant_barcodes
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE variant_mrps ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON variant_mrps
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE price_rules ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON price_rules
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE TRIGGER trg_tax_registrations_no_update_delete
        BEFORE UPDATE OR DELETE ON tax_registrations
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_tax_registrations_no_truncate
        BEFORE TRUNCATE ON tax_registrations
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    CREATE FUNCTION sb_price_rule_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Price rules cannot be deleted; retire them instead.' USING ERRCODE = 'restrict_violation';
        END IF;

        IF (NEW.business_id, NEW.variant_id, NEW.variant_unit_id, NEW.rate_type, NEW.channel, NEW.price, NEW.tax_inclusive,
            NEW.mrp, NEW.store_id, NEW.customer_group_id, NEW.members_only, NEW.min_quantity, NEW.max_quantity,
            NEW.valid_from_utc, NEW.valid_to_utc, NEW.priority, NEW.created_by_user_id, NEW.created_at_utc, NEW.tenant_id)
           IS DISTINCT FROM
           (OLD.business_id, OLD.variant_id, OLD.variant_unit_id, OLD.rate_type, OLD.channel, OLD.price, OLD.tax_inclusive,
            OLD.mrp, OLD.store_id, OLD.customer_group_id, OLD.members_only, OLD.min_quantity, OLD.max_quantity,
            OLD.valid_from_utc, OLD.valid_to_utc, OLD.priority, OLD.created_by_user_id, OLD.created_at_utc, OLD.tenant_id) THEN
            RAISE EXCEPTION 'A price rule''s price and conditions cannot be changed; create a new rule and retire this one.'
                USING ERRCODE = 'restrict_violation';
        END IF;

        IF OLD.status IN ('REJECTED', 'RETIRED') AND NEW.status IS DISTINCT FROM OLD.status THEN
            RAISE EXCEPTION 'A % price rule cannot change status.', lower(OLD.status) USING ERRCODE = 'restrict_violation';
        END IF;

        IF OLD.status = 'ACTIVE' AND NEW.status NOT IN ('ACTIVE', 'RETIRED') THEN
            RAISE EXCEPTION 'An active price rule can only be retired.' USING ERRCODE = 'restrict_violation';
        END IF;

        RETURN NEW;
    END;
    $$;

    CREATE TRIGGER trg_price_rules_guard
        BEFORE UPDATE OR DELETE ON price_rules
        FOR EACH ROW EXECUTE FUNCTION sb_price_rule_guard();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20260930131143_Catalog') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20260930131143_Catalog', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE TABLE batches (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        variant_id uuid NOT NULL,
        batch_number character varying(30) NOT NULL,
        manufactured_on date,
        expires_on date,
        created_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_batches PRIMARY KEY (id),
        CONSTRAINT ak_batches_id_variant_id UNIQUE (id, variant_id),
        CONSTRAINT ck_batches_dates CHECK (manufactured_on IS NULL OR expires_on IS NULL OR expires_on > manufactured_on),
        CONSTRAINT fk_batches_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_batches_product_variants_variant_id_business_id FOREIGN KEY (variant_id, business_id) REFERENCES product_variants (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_batches_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE TABLE document_sequences (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        series character varying(20) NOT NULL,
        next_number bigint NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_document_sequences PRIMARY KEY (id),
        CONSTRAINT ck_document_sequences_positive CHECK (next_number > 0),
        CONSTRAINT fk_document_sequences_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_document_sequences_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_document_sequences_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE TABLE inventory_settings (
        business_id uuid NOT NULL,
        valuation_method character varying(20) NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_inventory_settings PRIMARY KEY (business_id),
        CONSTRAINT ck_inventory_settings_method CHECK (valuation_method IN ('FIFO', 'FEFO', 'WEIGHTED_AVERAGE')),
        CONSTRAINT fk_inventory_settings_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_inventory_settings_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE TABLE negative_stock_rules (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid,
        product_id uuid,
        mode character varying(20) NOT NULL,
        limit_quantity numeric(18,3),
        reason character varying(300) NOT NULL,
        created_by_user_id uuid NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        approval_request_id uuid,
        is_active boolean NOT NULL,
        superseded_at_utc timestamp with time zone,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_negative_stock_rules PRIMARY KEY (id),
        CONSTRAINT ck_negative_stock_rules_limit CHECK ((mode = 'ENABLED_WITH_LIMIT') = (limit_quantity IS NOT NULL AND limit_quantity > 0)),
        CONSTRAINT ck_negative_stock_rules_mode CHECK (mode IN ('DISABLED', 'WARN_OVERRIDE', 'ENABLED_WITH_LIMIT')),
        CONSTRAINT fk_negative_stock_rules_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_negative_stock_rules_products_product_id_business_id FOREIGN KEY (product_id, business_id) REFERENCES products (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_negative_stock_rules_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_negative_stock_rules_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE TABLE reorder_levels (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        variant_id uuid NOT NULL,
        minimum_quantity numeric(18,3) NOT NULL,
        reorder_quantity numeric(18,3) NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_reorder_levels PRIMARY KEY (id),
        CONSTRAINT ck_reorder_levels_positive CHECK (minimum_quantity >= 0 AND reorder_quantity >= 0),
        CONSTRAINT fk_reorder_levels_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_reorder_levels_product_variants_variant_id_business_id FOREIGN KEY (variant_id, business_id) REFERENCES product_variants (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_reorder_levels_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_reorder_levels_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE TABLE stock_balances (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        variant_id uuid NOT NULL,
        quantity numeric(18,3) NOT NULL,
        average_cost numeric(18,4) NOT NULL,
        last_cost numeric(18,4) NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_stock_balances PRIMARY KEY (id),
        CONSTRAINT ck_stock_balances_costs CHECK (average_cost >= 0 AND last_cost >= 0),
        CONSTRAINT fk_stock_balances_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_stock_balances_product_variants_variant_id_business_id FOREIGN KEY (variant_id, business_id) REFERENCES product_variants (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_stock_balances_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_stock_balances_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE TABLE stock_documents (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        target_store_id uuid,
        type character varying(20) NOT NULL,
        number character varying(40) NOT NULL,
        business_date date NOT NULL,
        reason character varying(200) NOT NULL,
        note character varying(500),
        idempotency_key character varying(100) NOT NULL,
        request_hash character varying(64) NOT NULL,
        negative_stock_override boolean NOT NULL,
        posted_by_user_id uuid NOT NULL,
        posted_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_stock_documents PRIMARY KEY (id),
        CONSTRAINT ck_stock_documents_transfer CHECK ((type = 'TRANSFER') = (target_store_id IS NOT NULL) AND (target_store_id IS NULL OR target_store_id <> store_id)),
        CONSTRAINT ck_stock_documents_type CHECK (type IN ('OPENING', 'ADJUSTMENT', 'DAMAGE', 'WASTAGE', 'TRANSFER', 'COUNT')),
        CONSTRAINT fk_stock_documents_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_stock_documents_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_stock_documents_stores_target_store_id_business_id FOREIGN KEY (target_store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_stock_documents_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE TABLE cost_layers (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        variant_id uuid NOT NULL,
        batch_id uuid,
        expires_on date,
        unit_cost numeric(18,4) NOT NULL,
        original_quantity numeric(18,3) NOT NULL,
        remaining_quantity numeric(18,3) NOT NULL,
        settled_shortfall numeric(18,3) NOT NULL,
        received_at_utc timestamp with time zone NOT NULL,
        sequence bigint GENERATED ALWAYS AS IDENTITY,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_cost_layers PRIMARY KEY (id),
        CONSTRAINT ck_cost_layers_cost CHECK (unit_cost >= 0),
        CONSTRAINT ck_cost_layers_quantities CHECK (original_quantity > 0 AND remaining_quantity >= 0 AND settled_shortfall >= 0 AND remaining_quantity + settled_shortfall <= original_quantity),
        CONSTRAINT fk_cost_layers_batches_batch_id_variant_id FOREIGN KEY (batch_id, variant_id) REFERENCES batches (id, variant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_cost_layers_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_cost_layers_product_variants_variant_id_business_id FOREIGN KEY (variant_id, business_id) REFERENCES product_variants (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_cost_layers_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_cost_layers_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE TABLE stock_ledger (
        id uuid NOT NULL,
        sequence bigint GENERATED ALWAYS AS IDENTITY,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        variant_id uuid NOT NULL,
        batch_id uuid,
        layer_id uuid,
        movement_type character varying(20) NOT NULL,
        quantity numeric(18,3) NOT NULL,
        unit_cost numeric(18,4) NOT NULL,
        value numeric(20,4) NOT NULL,
        balance_after numeric(18,3) NOT NULL,
        document_type character varying(20) NOT NULL,
        document_id uuid NOT NULL,
        business_date date NOT NULL,
        occurred_at_utc timestamp with time zone NOT NULL,
        created_by_user_id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_stock_ledger PRIMARY KEY (id),
        CONSTRAINT ck_stock_ledger_cost CHECK (unit_cost >= 0),
        CONSTRAINT ck_stock_ledger_direction CHECK ((movement_type IN ('OPENING', 'ADJUSTMENT_IN', 'TRANSFER_IN', 'COUNT_GAIN', 'RECEIPT', 'SALE_RETURN') AND quantity > 0) OR (movement_type IN ('ADJUSTMENT_OUT', 'DAMAGE', 'WASTAGE', 'TRANSFER_OUT', 'COUNT_LOSS', 'PURCHASE_RETURN', 'SALE') AND quantity < 0)),
        CONSTRAINT ck_stock_ledger_quantity CHECK (quantity <> 0),
        CONSTRAINT ck_stock_ledger_value CHECK (value = round(quantity * unit_cost, 4)),
        CONSTRAINT fk_stock_ledger_batches_batch_id_variant_id FOREIGN KEY (batch_id, variant_id) REFERENCES batches (id, variant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_stock_ledger_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_stock_ledger_cost_layers_layer_id FOREIGN KEY (layer_id) REFERENCES cost_layers (id) ON DELETE RESTRICT,
        CONSTRAINT fk_stock_ledger_product_variants_variant_id_business_id FOREIGN KEY (variant_id, business_id) REFERENCES product_variants (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_stock_ledger_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_stock_ledger_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_batches_business_id_expires_on ON batches (business_id, expires_on);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_batches_business_id_tenant_id ON batches (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_batches_tenant_id ON batches (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE UNIQUE INDEX ix_batches_variant_id_batch_number ON batches (variant_id, batch_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_batches_variant_id_business_id ON batches (variant_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_cost_layers_batch_id_variant_id ON cost_layers (batch_id, variant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_cost_layers_business_id_tenant_id ON cost_layers (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_cost_layers_open ON cost_layers (store_id, variant_id, sequence) WHERE remaining_quantity > 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE UNIQUE INDEX ix_cost_layers_sequence ON cost_layers (sequence);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_cost_layers_store_id_business_id ON cost_layers (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_cost_layers_tenant_id ON cost_layers (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_cost_layers_variant_id_business_id ON cost_layers (variant_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_document_sequences_business_id_tenant_id ON document_sequences (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_document_sequences_store_id_business_id ON document_sequences (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE UNIQUE INDEX ix_document_sequences_store_id_series ON document_sequences (store_id, series);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_document_sequences_tenant_id ON document_sequences (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_inventory_settings_business_id_tenant_id ON inventory_settings (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_inventory_settings_tenant_id ON inventory_settings (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_negative_stock_rules_business_id_tenant_id ON negative_stock_rules (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_negative_stock_rules_product_id_business_id ON negative_stock_rules (product_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_negative_stock_rules_store_id_business_id ON negative_stock_rules (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_negative_stock_rules_tenant_id ON negative_stock_rules (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE UNIQUE INDEX ux_negative_stock_rules_active_scope ON negative_stock_rules (business_id, store_id, product_id) NULLS NOT DISTINCT WHERE is_active;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_reorder_levels_business_id_tenant_id ON reorder_levels (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_reorder_levels_store_id_business_id ON reorder_levels (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE UNIQUE INDEX ix_reorder_levels_store_id_variant_id ON reorder_levels (store_id, variant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_reorder_levels_tenant_id ON reorder_levels (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_reorder_levels_variant_id_business_id ON reorder_levels (variant_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_stock_balances_business_id_tenant_id ON stock_balances (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_stock_balances_store_id_business_id ON stock_balances (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE UNIQUE INDEX ix_stock_balances_store_id_variant_id ON stock_balances (store_id, variant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_stock_balances_tenant_id ON stock_balances (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_stock_balances_variant_id_business_id ON stock_balances (variant_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE UNIQUE INDEX ix_stock_documents_business_id_idempotency_key ON stock_documents (business_id, idempotency_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_stock_documents_business_id_tenant_id ON stock_documents (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_stock_documents_store_id_business_id ON stock_documents (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE UNIQUE INDEX ix_stock_documents_store_id_number ON stock_documents (store_id, number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_stock_documents_store_id_posted_at_utc ON stock_documents (store_id, posted_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_stock_documents_target_store_id_business_id ON stock_documents (target_store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_stock_documents_tenant_id ON stock_documents (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_stock_ledger_batch_id_variant_id ON stock_ledger (batch_id, variant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_stock_ledger_business_id_tenant_id ON stock_ledger (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_stock_ledger_document_type_document_id ON stock_ledger (document_type, document_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_stock_ledger_layer_id ON stock_ledger (layer_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE UNIQUE INDEX ix_stock_ledger_sequence ON stock_ledger (sequence);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_stock_ledger_store_id_business_id ON stock_ledger (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_stock_ledger_store_id_variant_id_sequence ON stock_ledger (store_id, variant_id, sequence);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_stock_ledger_tenant_id ON stock_ledger (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE INDEX ix_stock_ledger_variant_id_business_id ON stock_ledger (variant_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    ALTER TABLE inventory_settings ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON inventory_settings
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE negative_stock_rules ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON negative_stock_rules
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE batches ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON batches
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE cost_layers ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON cost_layers
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE stock_balances ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON stock_balances
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE stock_ledger ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON stock_ledger
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE stock_documents ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON stock_documents
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE reorder_levels ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON reorder_levels
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE document_sequences ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON document_sequences
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE TRIGGER trg_stock_ledger_no_update_delete
        BEFORE UPDATE OR DELETE ON stock_ledger
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_stock_ledger_no_truncate
        BEFORE TRUNCATE ON stock_ledger
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE TRIGGER trg_stock_documents_no_update_delete
        BEFORE UPDATE OR DELETE ON stock_documents
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_stock_documents_no_truncate
        BEFORE TRUNCATE ON stock_documents
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    CREATE FUNCTION sb_cost_layer_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Cost layers cannot be deleted.' USING ERRCODE = 'restrict_violation';
        END IF;

        IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.store_id, NEW.variant_id, NEW.batch_id, NEW.expires_on, NEW.unit_cost,
            NEW.original_quantity, NEW.received_at_utc, NEW.sequence)
           IS DISTINCT FROM
           (OLD.id, OLD.tenant_id, OLD.business_id, OLD.store_id, OLD.variant_id, OLD.batch_id, OLD.expires_on, OLD.unit_cost,
            OLD.original_quantity, OLD.received_at_utc, OLD.sequence) THEN
            RAISE EXCEPTION 'A cost layer''s origin cannot be changed.' USING ERRCODE = 'restrict_violation';
        END IF;

        IF NEW.remaining_quantity > OLD.remaining_quantity OR NEW.settled_shortfall < OLD.settled_shortfall THEN
            RAISE EXCEPTION 'Stock cannot be put back into a cost layer; post a new receipt instead.' USING ERRCODE = 'restrict_violation';
        END IF;

        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_cost_layers_guard BEFORE UPDATE OR DELETE ON cost_layers
        FOR EACH ROW EXECUTE FUNCTION sb_cost_layer_guard();
    CREATE TRIGGER trg_cost_layers_no_truncate BEFORE TRUNCATE ON cost_layers
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    INSERT INTO inventory_settings (business_id, tenant_id, valuation_method)
    SELECT id, tenant_id, 'FIFO' FROM businesses
    ON CONFLICT (business_id) DO NOTHING;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001075321_Inventory') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261001075321_Inventory', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE TABLE counters (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        code character varying(6) NOT NULL,
        name character varying(60) NOT NULL,
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_counters PRIMARY KEY (id),
        CONSTRAINT ak_counters_id_business_id UNIQUE (id, business_id),
        CONSTRAINT ck_counters_code CHECK (code ~ '^[A-Z0-9]{1,6}$'),
        CONSTRAINT fk_counters_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_counters_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_counters_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE TABLE counter_devices (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        counter_id uuid NOT NULL,
        name character varying(60) NOT NULL,
        token_hash bytea NOT NULL,
        enrolled_by_user_id uuid NOT NULL,
        enrolled_at_utc timestamp with time zone NOT NULL,
        last_seen_at_utc timestamp with time zone,
        revoked_at_utc timestamp with time zone,
        revoked_by_user_id uuid,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_counter_devices PRIMARY KEY (id),
        CONSTRAINT fk_counter_devices_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_counter_devices_counters_counter_id_business_id FOREIGN KEY (counter_id, business_id) REFERENCES counters (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_counter_devices_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE TABLE sales_invoices (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        counter_id uuid NOT NULL,
        device_id uuid NOT NULL,
        number character varying(16) NOT NULL,
        number_prefix character varying(7) NOT NULL,
        sequence_number bigint NOT NULL,
        kind character varying(20) NOT NULL,
        tax_mode character varying(20) NOT NULL,
        channel character varying(10) NOT NULL,
        business_date date NOT NULL,
        issued_at_utc timestamp with time zone NOT NULL,
        cashier_user_id uuid NOT NULL,
        seller_name character varying(200) NOT NULL,
        seller_gstin character varying(15),
        seller_address character varying(500) NOT NULL,
        seller_state_code character varying(2) NOT NULL,
        buyer_name character varying(100),
        buyer_gstin character varying(15),
        buyer_phone character varying(20),
        buyer_address character varying(300),
        place_of_supply_state_code character varying(2) NOT NULL,
        is_inter_state boolean NOT NULL,
        gross_total numeric(18,2) NOT NULL,
        discount_total numeric(18,2) NOT NULL,
        taxable_total numeric(18,2) NOT NULL,
        cgst_total numeric(18,2) NOT NULL,
        sgst_total numeric(18,2) NOT NULL,
        igst_total numeric(18,2) NOT NULL,
        cess_total numeric(18,2) NOT NULL,
        round_off numeric(18,2) NOT NULL,
        grand_total numeric(18,2) NOT NULL,
        paid_total numeric(18,2) NOT NULL,
        change_due numeric(18,2) NOT NULL,
        discount_approval_id uuid,
        negative_stock_override boolean NOT NULL,
        idempotency_key character varying(100) NOT NULL,
        request_hash character varying(64) NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_sales_invoices PRIMARY KEY (id),
        CONSTRAINT ak_sales_invoices_id_business_id UNIQUE (id, business_id),
        CONSTRAINT ck_sales_invoices_amounts CHECK (gross_total >= 0 AND discount_total >= 0 AND taxable_total >= 0 AND cgst_total >= 0 AND igst_total >= 0 AND cess_total >= 0),
        CONSTRAINT ck_sales_invoices_channel CHECK (channel IN ('RETAIL', 'WHOLESALE')),
        CONSTRAINT ck_sales_invoices_composition_intra_state CHECK (tax_mode <> 'GST_COMPOSITION' OR NOT is_inter_state),
        CONSTRAINT ck_sales_invoices_gst_split CHECK (cgst_total = sgst_total AND (CASE WHEN is_inter_state THEN cgst_total = 0 ELSE igst_total = 0 END)),
        CONSTRAINT ck_sales_invoices_kind CHECK (kind IN ('TAX_INVOICE', 'BILL_OF_SUPPLY', 'INVOICE')),
        CONSTRAINT ck_sales_invoices_kind_mode CHECK ((tax_mode = 'GST_REGULAR' AND kind IN ('TAX_INVOICE', 'BILL_OF_SUPPLY')) OR (tax_mode = 'GST_COMPOSITION' AND kind = 'BILL_OF_SUPPLY') OR (tax_mode = 'NOT_GST_REGISTERED' AND kind = 'INVOICE')),
        CONSTRAINT ck_sales_invoices_number CHECK (char_length(number) <= 16 AND number_prefix ~ '^[A-Z0-9]{1,7}$' AND sequence_number > 0 AND number = number_prefix || '-' || CASE WHEN sequence_number < 1000000 THEN lpad(sequence_number::text, 6, '0') ELSE sequence_number::text END),
        CONSTRAINT ck_sales_invoices_only_regular_collects_tax CHECK (tax_mode = 'GST_REGULAR' OR (cgst_total = 0 AND sgst_total = 0 AND igst_total = 0 AND cess_total = 0)),
        CONSTRAINT ck_sales_invoices_paid CHECK (change_due >= 0 AND paid_total - change_due = grand_total),
        CONSTRAINT ck_sales_invoices_tax_mode CHECK (tax_mode IN ('GST_REGULAR', 'GST_COMPOSITION', 'NOT_GST_REGISTERED')),
        CONSTRAINT ck_sales_invoices_total CHECK (grand_total = taxable_total + cgst_total + sgst_total + igst_total + cess_total + round_off AND abs(round_off) <= 0.5 AND grand_total = round(grand_total)),
        CONSTRAINT fk_sales_invoices_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_invoices_counters_counter_id_business_id FOREIGN KEY (counter_id, business_id) REFERENCES counters (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_invoices_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_invoices_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE TABLE supervisor_approvals (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        counter_id uuid NOT NULL,
        kind character varying(20) NOT NULL,
        variant_unit_id uuid,
        approved_price numeric(18,2),
        max_discount numeric(18,2),
        reason character varying(200) NOT NULL,
        approved_by_user_id uuid NOT NULL,
        requested_by_user_id uuid NOT NULL,
        token_hash bytea NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        expires_at_utc timestamp with time zone NOT NULL,
        used_at_utc timestamp with time zone,
        used_invoice_id uuid,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_supervisor_approvals PRIMARY KEY (id),
        CONSTRAINT ck_supervisor_approvals_kind CHECK ((kind = 'PRICE_OVERRIDE' AND variant_unit_id IS NOT NULL AND approved_price >= 0 AND max_discount IS NULL) OR (kind = 'DISCOUNT' AND variant_unit_id IS NULL AND approved_price IS NULL AND max_discount > 0)),
        CONSTRAINT ck_supervisor_approvals_two_people CHECK (approved_by_user_id <> requested_by_user_id),
        CONSTRAINT ck_supervisor_approvals_use CHECK ((used_at_utc IS NULL) = (used_invoice_id IS NULL)),
        CONSTRAINT fk_supervisor_approvals_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_supervisor_approvals_counters_counter_id_business_id FOREIGN KEY (counter_id, business_id) REFERENCES counters (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_supervisor_approvals_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE TABLE sales_invoice_lines (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        invoice_id uuid NOT NULL,
        line_number integer NOT NULL,
        product_id uuid NOT NULL,
        variant_id uuid NOT NULL,
        variant_unit_id uuid NOT NULL,
        description character varying(200) NOT NULL,
        hsn_sac character varying(8) NOT NULL,
        unit_code character varying(10) NOT NULL,
        quantity numeric(18,3) NOT NULL,
        base_quantity numeric(18,3) NOT NULL,
        mrp numeric(18,2),
        price_rule_id uuid,
        rate_type character varying(20) NOT NULL,
        price_override_approval_id uuid,
        unit_price numeric(18,2) NOT NULL,
        tax_inclusive boolean NOT NULL,
        supply_type character varying(12) NOT NULL,
        gst_rate_percent numeric(5,2) NOT NULL,
        cess_rate_percent numeric(5,2) NOT NULL,
        gross numeric(18,2) NOT NULL,
        item_discount numeric(18,2) NOT NULL,
        bill_discount numeric(18,2) NOT NULL,
        taxable numeric(18,2) NOT NULL,
        cgst numeric(18,2) NOT NULL,
        sgst numeric(18,2) NOT NULL,
        igst numeric(18,2) NOT NULL,
        cess numeric(18,2) NOT NULL,
        total numeric(18,2) NOT NULL,
        cost_of_goods numeric(18,4) NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_sales_invoice_lines PRIMARY KEY (id),
        CONSTRAINT ck_sales_invoice_lines_amounts CHECK (quantity > 0 AND base_quantity > 0 AND unit_price >= 0 AND item_discount >= 0 AND bill_discount >= 0 AND taxable >= 0 AND cgst >= 0 AND igst >= 0 AND cess >= 0),
        CONSTRAINT ck_sales_invoice_lines_net CHECK (gross - item_discount - bill_discount = CASE WHEN tax_inclusive OR cgst + sgst + igst + cess = 0 THEN total ELSE taxable END),
        CONSTRAINT ck_sales_invoice_lines_price_source CHECK ((rate_type = 'OVERRIDE' AND price_override_approval_id IS NOT NULL AND price_rule_id IS NULL) OR (rate_type = 'OVERRIDE_SELF' AND price_override_approval_id IS NULL AND price_rule_id IS NULL) OR (rate_type NOT IN ('OVERRIDE', 'OVERRIDE_SELF') AND price_rule_id IS NOT NULL AND price_override_approval_id IS NULL)),
        CONSTRAINT ck_sales_invoice_lines_supply_type CHECK (supply_type IN ('TAXABLE', 'EXEMPT', 'NIL_RATED', 'NON_GST')),
        CONSTRAINT ck_sales_invoice_lines_total CHECK (total = taxable + cgst + sgst + igst + cess AND cgst = sgst AND (cgst = 0 OR igst = 0)),
        CONSTRAINT ck_sales_invoice_lines_untaxed CHECK (supply_type = 'TAXABLE' OR (cgst = 0 AND igst = 0 AND cess = 0)),
        CONSTRAINT fk_sales_invoice_lines_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_invoice_lines_product_variants_variant_id_business_id FOREIGN KEY (variant_id, business_id) REFERENCES product_variants (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_invoice_lines_sales_invoices_invoice_id FOREIGN KEY (invoice_id) REFERENCES sales_invoices (id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_invoice_lines_sales_invoices_invoice_id_business_id FOREIGN KEY (invoice_id, business_id) REFERENCES sales_invoices (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_invoice_lines_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_invoice_lines_variant_units_variant_unit_id FOREIGN KEY (variant_unit_id) REFERENCES variant_units (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE TABLE sales_invoice_payments (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        invoice_id uuid NOT NULL,
        payment_order integer NOT NULL,
        method character varying(10) NOT NULL,
        amount numeric(18,2) NOT NULL,
        reference character varying(60),
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_sales_invoice_payments PRIMARY KEY (id),
        CONSTRAINT ck_sales_invoice_payments_amount CHECK (amount > 0),
        CONSTRAINT ck_sales_invoice_payments_method CHECK (method IN ('CASH', 'CARD', 'UPI', 'WALLET')),
        CONSTRAINT fk_sales_invoice_payments_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_invoice_payments_sales_invoices_invoice_id FOREIGN KEY (invoice_id) REFERENCES sales_invoices (id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_invoice_payments_sales_invoices_invoice_id_business_id FOREIGN KEY (invoice_id, business_id) REFERENCES sales_invoices (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_invoice_payments_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_counter_devices_business_id_tenant_id ON counter_devices (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_counter_devices_counter_id_business_id ON counter_devices (counter_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_counter_devices_tenant_id ON counter_devices (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE UNIQUE INDEX ix_counter_devices_token_hash ON counter_devices (token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE UNIQUE INDEX ix_counters_business_id_code ON counters (business_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_counters_business_id_tenant_id ON counters (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_counters_store_id_business_id ON counters (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_counters_tenant_id ON counters (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_sales_invoice_lines_business_id_tenant_id ON sales_invoice_lines (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_sales_invoice_lines_invoice_id_business_id ON sales_invoice_lines (invoice_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE UNIQUE INDEX ix_sales_invoice_lines_invoice_id_line_number ON sales_invoice_lines (invoice_id, line_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_sales_invoice_lines_tenant_id ON sales_invoice_lines (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_sales_invoice_lines_variant_id_business_id ON sales_invoice_lines (variant_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_sales_invoice_lines_variant_id_invoice_id ON sales_invoice_lines (variant_id, invoice_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_sales_invoice_lines_variant_unit_id ON sales_invoice_lines (variant_unit_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_sales_invoice_payments_business_id_tenant_id ON sales_invoice_payments (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_sales_invoice_payments_invoice_id_business_id ON sales_invoice_payments (invoice_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE UNIQUE INDEX ix_sales_invoice_payments_invoice_id_payment_order ON sales_invoice_payments (invoice_id, payment_order);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_sales_invoice_payments_tenant_id ON sales_invoice_payments (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE UNIQUE INDEX ix_sales_invoices_business_id_idempotency_key ON sales_invoices (business_id, idempotency_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE UNIQUE INDEX ix_sales_invoices_business_id_number ON sales_invoices (business_id, number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_sales_invoices_business_id_tenant_id ON sales_invoices (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_sales_invoices_counter_id_business_id ON sales_invoices (counter_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE UNIQUE INDEX ix_sales_invoices_counter_id_number_prefix_sequence_number ON sales_invoices (counter_id, number_prefix, sequence_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_sales_invoices_store_id_business_date ON sales_invoices (store_id, business_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_sales_invoices_store_id_business_id ON sales_invoices (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_sales_invoices_tenant_id ON sales_invoices (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_supervisor_approvals_business_id_tenant_id ON supervisor_approvals (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_supervisor_approvals_counter_id_business_id ON supervisor_approvals (counter_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_supervisor_approvals_tenant_id ON supervisor_approvals (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE UNIQUE INDEX ix_supervisor_approvals_token_hash ON supervisor_approvals (token_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE INDEX ix_supervisor_approvals_used_invoice_id ON supervisor_approvals (used_invoice_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    ALTER TABLE counters ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON counters
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE counter_devices ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON counter_devices
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE supervisor_approvals ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON supervisor_approvals
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE sales_invoices ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON sales_invoices
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE sales_invoice_lines ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON sales_invoice_lines
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE sales_invoice_payments ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON sales_invoice_payments
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE TRIGGER trg_sales_invoices_no_update_delete
        BEFORE UPDATE OR DELETE ON sales_invoices
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_sales_invoices_no_truncate
        BEFORE TRUNCATE ON sales_invoices
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE TRIGGER trg_sales_invoice_lines_no_update_delete
        BEFORE UPDATE OR DELETE ON sales_invoice_lines
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_sales_invoice_lines_no_truncate
        BEFORE TRUNCATE ON sales_invoice_lines
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE TRIGGER trg_sales_invoice_payments_no_update_delete
        BEFORE UPDATE OR DELETE ON sales_invoice_payments
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_sales_invoice_payments_no_truncate
        BEFORE TRUNCATE ON sales_invoice_payments
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    CREATE FUNCTION sb_supervisor_approval_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Supervisor approvals cannot be deleted.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF OLD.used_at_utc IS NOT NULL
           OR (NEW.id, NEW.tenant_id, NEW.business_id, NEW.counter_id, NEW.kind, NEW.variant_unit_id, NEW.approved_price, NEW.max_discount,
               NEW.reason, NEW.approved_by_user_id, NEW.requested_by_user_id, NEW.token_hash, NEW.created_at_utc, NEW.expires_at_utc)
              IS DISTINCT FROM
              (OLD.id, OLD.tenant_id, OLD.business_id, OLD.counter_id, OLD.kind, OLD.variant_unit_id, OLD.approved_price, OLD.max_discount,
               OLD.reason, OLD.approved_by_user_id, OLD.requested_by_user_id, OLD.token_hash, OLD.created_at_utc, OLD.expires_at_utc) THEN
            RAISE EXCEPTION 'A supervisor approval can only be marked used, once.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_supervisor_approvals_guard BEFORE UPDATE OR DELETE ON supervisor_approvals
        FOR EACH ROW EXECUTE FUNCTION sb_supervisor_approval_guard();

    CREATE FUNCTION sb_counter_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Counters cannot be deleted; switch them off.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF (NEW.code, NEW.store_id, NEW.business_id, NEW.tenant_id) IS DISTINCT FROM (OLD.code, OLD.store_id, OLD.business_id, OLD.tenant_id) THEN
            RAISE EXCEPTION 'A counter''s code and store cannot change (they are part of its invoice numbers).' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_counters_guard BEFORE UPDATE OR DELETE ON counters
        FOR EACH ROW EXECUTE FUNCTION sb_counter_guard();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001092205_Sales') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261001092205_Sales', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001105817_ParkedBills') THEN
    CREATE TABLE parked_bills (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        counter_id uuid NOT NULL,
        parked_by_user_id uuid NOT NULL,
        label character varying(40),
        item_count integer NOT NULL,
        cart_json jsonb NOT NULL,
        parked_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_parked_bills PRIMARY KEY (id),
        CONSTRAINT ck_parked_bills_items CHECK (item_count BETWEEN 1 AND 300),
        CONSTRAINT fk_parked_bills_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_parked_bills_counters_counter_id_business_id FOREIGN KEY (counter_id, business_id) REFERENCES counters (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_parked_bills_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001105817_ParkedBills') THEN
    CREATE INDEX ix_parked_bills_business_id_tenant_id ON parked_bills (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001105817_ParkedBills') THEN
    CREATE INDEX ix_parked_bills_counter_id_business_id ON parked_bills (counter_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001105817_ParkedBills') THEN
    CREATE INDEX ix_parked_bills_counter_id_parked_at_utc ON parked_bills (counter_id, parked_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001105817_ParkedBills') THEN
    CREATE INDEX ix_parked_bills_tenant_id ON parked_bills (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001105817_ParkedBills') THEN
    ALTER TABLE parked_bills ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON parked_bills
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001105817_ParkedBills') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261001105817_ParkedBills', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    ALTER TABLE supervisor_approvals DROP CONSTRAINT ck_supervisor_approvals_kind;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    ALTER TABLE supervisor_approvals DROP CONSTRAINT ck_supervisor_approvals_use;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    ALTER TABLE sales_invoice_payments DROP CONSTRAINT ck_sales_invoice_payments_method;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    ALTER TABLE supervisor_approvals RENAME COLUMN used_invoice_id TO used_document_id;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    ALTER TABLE supervisor_approvals RENAME COLUMN max_discount TO max_amount;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    ALTER INDEX ix_supervisor_approvals_used_invoice_id RENAME TO ix_supervisor_approvals_used_document_id;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    ALTER TABLE sales_invoice_payments ALTER COLUMN method TYPE character varying(12);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE TABLE sales_returns (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        counter_id uuid NOT NULL,
        device_id uuid NOT NULL,
        original_invoice_id uuid NOT NULL,
        original_invoice_number character varying(16) NOT NULL,
        original_invoice_date date NOT NULL,
        number character varying(16) NOT NULL,
        number_prefix character varying(7) NOT NULL,
        sequence_number bigint NOT NULL,
        tax_mode character varying(20) NOT NULL,
        is_inter_state boolean NOT NULL,
        place_of_supply_state_code character varying(2) NOT NULL,
        business_date date NOT NULL,
        issued_at_utc timestamp with time zone NOT NULL,
        cashier_user_id uuid NOT NULL,
        reason character varying(200) NOT NULL,
        approval_id uuid,
        taxable_total numeric(18,2) NOT NULL,
        cgst_total numeric(18,2) NOT NULL,
        sgst_total numeric(18,2) NOT NULL,
        igst_total numeric(18,2) NOT NULL,
        cess_total numeric(18,2) NOT NULL,
        round_off numeric(18,2) NOT NULL,
        grand_total numeric(18,2) NOT NULL,
        store_credit numeric(18,2) NOT NULL,
        idempotency_key character varying(100) NOT NULL,
        request_hash character varying(64) NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_sales_returns PRIMARY KEY (id),
        CONSTRAINT ak_sales_returns_id_business_id UNIQUE (id, business_id),
        CONSTRAINT ck_sales_returns_gst_split CHECK (cgst_total = sgst_total AND (CASE WHEN is_inter_state THEN cgst_total = 0 ELSE igst_total = 0 END)),
        CONSTRAINT ck_sales_returns_number CHECK (char_length(number) <= 16 AND number_prefix ~ '^[A-Z0-9]{1,7}$' AND sequence_number > 0 AND number = number_prefix || '/CN' || CASE WHEN sequence_number < 1000000 THEN lpad(sequence_number::text, 6, '0') ELSE sequence_number::text END),
        CONSTRAINT ck_sales_returns_store_credit CHECK (store_credit >= 0 AND store_credit <= grand_total),
        CONSTRAINT ck_sales_returns_tax_mode CHECK (tax_mode IN ('GST_REGULAR', 'GST_COMPOSITION', 'NOT_GST_REGISTERED')),
        CONSTRAINT ck_sales_returns_total CHECK (grand_total = taxable_total + cgst_total + sgst_total + igst_total + cess_total + round_off AND abs(round_off) <= 0.5 AND grand_total = round(grand_total) AND grand_total >= 0),
        CONSTRAINT fk_sales_returns_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_returns_counters_counter_id_business_id FOREIGN KEY (counter_id, business_id) REFERENCES counters (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_returns_sales_invoices_original_invoice_id_business_id FOREIGN KEY (original_invoice_id, business_id) REFERENCES sales_invoices (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_returns_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_returns_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE TABLE credit_note_redemptions (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        return_id uuid NOT NULL,
        invoice_id uuid NOT NULL,
        amount numeric(18,2) NOT NULL,
        redeemed_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_credit_note_redemptions PRIMARY KEY (id),
        CONSTRAINT ck_credit_note_redemptions_amount CHECK (amount > 0),
        CONSTRAINT fk_credit_note_redemptions_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_credit_note_redemptions_sales_invoices_invoice_id_business_ FOREIGN KEY (invoice_id, business_id) REFERENCES sales_invoices (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_credit_note_redemptions_sales_returns_return_id_business_id FOREIGN KEY (return_id, business_id) REFERENCES sales_returns (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_credit_note_redemptions_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE TABLE sales_return_lines (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        return_id uuid NOT NULL,
        line_number integer NOT NULL,
        original_line_id uuid NOT NULL,
        variant_id uuid NOT NULL,
        quantity numeric(18,3) NOT NULL,
        base_quantity numeric(18,3) NOT NULL,
        restocked boolean NOT NULL,
        gross numeric(18,2) NOT NULL,
        item_discount numeric(18,2) NOT NULL,
        bill_discount numeric(18,2) NOT NULL,
        taxable numeric(18,2) NOT NULL,
        cgst numeric(18,2) NOT NULL,
        sgst numeric(18,2) NOT NULL,
        igst numeric(18,2) NOT NULL,
        cess numeric(18,2) NOT NULL,
        total numeric(18,2) NOT NULL,
        cost_returned numeric(18,4) NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_sales_return_lines PRIMARY KEY (id),
        CONSTRAINT ck_sales_return_lines_amounts CHECK (quantity > 0 AND base_quantity > 0 AND taxable >= 0 AND cgst >= 0 AND igst >= 0 AND cess >= 0 AND cost_returned >= 0),
        CONSTRAINT ck_sales_return_lines_restock CHECK (restocked OR cost_returned = 0),
        CONSTRAINT ck_sales_return_lines_total CHECK (total = taxable + cgst + sgst + igst + cess AND cgst = sgst AND (cgst = 0 OR igst = 0)),
        CONSTRAINT fk_sales_return_lines_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_return_lines_product_variants_variant_id_business_id FOREIGN KEY (variant_id, business_id) REFERENCES product_variants (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_return_lines_sales_invoice_lines_original_line_id FOREIGN KEY (original_line_id) REFERENCES sales_invoice_lines (id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_return_lines_sales_returns_return_id FOREIGN KEY (return_id) REFERENCES sales_returns (id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_return_lines_sales_returns_return_id_business_id FOREIGN KEY (return_id, business_id) REFERENCES sales_returns (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_return_lines_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE TABLE sales_return_refunds (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        return_id uuid NOT NULL,
        refund_order integer NOT NULL,
        method character varying(12) NOT NULL,
        amount numeric(18,2) NOT NULL,
        reference character varying(60),
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_sales_return_refunds PRIMARY KEY (id),
        CONSTRAINT ck_sales_return_refunds_amount CHECK (amount > 0),
        CONSTRAINT ck_sales_return_refunds_method CHECK (method IN ('CASH', 'CARD', 'UPI', 'WALLET', 'STORE_CREDIT')),
        CONSTRAINT fk_sales_return_refunds_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_return_refunds_sales_returns_return_id FOREIGN KEY (return_id) REFERENCES sales_returns (id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_return_refunds_sales_returns_return_id_business_id FOREIGN KEY (return_id, business_id) REFERENCES sales_returns (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_sales_return_refunds_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    ALTER TABLE supervisor_approvals ADD CONSTRAINT ck_supervisor_approvals_kind CHECK ((kind = 'PRICE_OVERRIDE' AND variant_unit_id IS NOT NULL AND approved_price >= 0 AND max_amount IS NULL) OR (kind IN ('DISCOUNT', 'RETURN') AND variant_unit_id IS NULL AND approved_price IS NULL AND max_amount > 0));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    ALTER TABLE supervisor_approvals ADD CONSTRAINT ck_supervisor_approvals_use CHECK ((used_at_utc IS NULL) = (used_document_id IS NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    ALTER TABLE sales_invoice_payments ADD CONSTRAINT ck_sales_invoice_payments_credit_note CHECK (method <> 'CREDIT_NOTE' OR reference IS NOT NULL);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    ALTER TABLE sales_invoice_payments ADD CONSTRAINT ck_sales_invoice_payments_method CHECK (method IN ('CASH', 'CARD', 'UPI', 'WALLET', 'CREDIT_NOTE'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_credit_note_redemptions_business_id_tenant_id ON credit_note_redemptions (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_credit_note_redemptions_invoice_id ON credit_note_redemptions (invoice_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_credit_note_redemptions_invoice_id_business_id ON credit_note_redemptions (invoice_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_credit_note_redemptions_return_id ON credit_note_redemptions (return_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_credit_note_redemptions_return_id_business_id ON credit_note_redemptions (return_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_credit_note_redemptions_tenant_id ON credit_note_redemptions (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_sales_return_lines_business_id_tenant_id ON sales_return_lines (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_sales_return_lines_original_line_id ON sales_return_lines (original_line_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_sales_return_lines_return_id_business_id ON sales_return_lines (return_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE UNIQUE INDEX ix_sales_return_lines_return_id_line_number ON sales_return_lines (return_id, line_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_sales_return_lines_tenant_id ON sales_return_lines (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_sales_return_lines_variant_id_business_id ON sales_return_lines (variant_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_sales_return_refunds_business_id_tenant_id ON sales_return_refunds (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_sales_return_refunds_return_id_business_id ON sales_return_refunds (return_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE UNIQUE INDEX ix_sales_return_refunds_return_id_refund_order ON sales_return_refunds (return_id, refund_order);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_sales_return_refunds_tenant_id ON sales_return_refunds (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE UNIQUE INDEX ix_sales_returns_business_id_idempotency_key ON sales_returns (business_id, idempotency_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE UNIQUE INDEX ix_sales_returns_business_id_number ON sales_returns (business_id, number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_sales_returns_business_id_tenant_id ON sales_returns (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_sales_returns_counter_id_business_id ON sales_returns (counter_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE UNIQUE INDEX ix_sales_returns_counter_id_number_prefix_sequence_number ON sales_returns (counter_id, number_prefix, sequence_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_sales_returns_original_invoice_id ON sales_returns (original_invoice_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_sales_returns_original_invoice_id_business_id ON sales_returns (original_invoice_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_sales_returns_store_id_business_id ON sales_returns (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE INDEX ix_sales_returns_tenant_id ON sales_returns (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE OR REPLACE FUNCTION sb_supervisor_approval_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Supervisor approvals cannot be deleted.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF OLD.used_at_utc IS NOT NULL
           OR (NEW.id, NEW.tenant_id, NEW.business_id, NEW.counter_id, NEW.kind, NEW.variant_unit_id, NEW.approved_price, NEW.max_amount,
               NEW.reason, NEW.approved_by_user_id, NEW.requested_by_user_id, NEW.token_hash, NEW.created_at_utc, NEW.expires_at_utc)
              IS DISTINCT FROM
              (OLD.id, OLD.tenant_id, OLD.business_id, OLD.counter_id, OLD.kind, OLD.variant_unit_id, OLD.approved_price, OLD.max_amount,
               OLD.reason, OLD.approved_by_user_id, OLD.requested_by_user_id, OLD.token_hash, OLD.created_at_utc, OLD.expires_at_utc) THEN
            RAISE EXCEPTION 'A supervisor approval can only be marked used, once.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    ALTER TABLE sales_returns ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON sales_returns
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE sales_return_lines ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON sales_return_lines
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE sales_return_refunds ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON sales_return_refunds
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE credit_note_redemptions ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON credit_note_redemptions
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE TRIGGER trg_sales_returns_no_update_delete
        BEFORE UPDATE OR DELETE ON sales_returns
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_sales_returns_no_truncate
        BEFORE TRUNCATE ON sales_returns
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE TRIGGER trg_sales_return_lines_no_update_delete
        BEFORE UPDATE OR DELETE ON sales_return_lines
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_sales_return_lines_no_truncate
        BEFORE TRUNCATE ON sales_return_lines
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE TRIGGER trg_sales_return_refunds_no_update_delete
        BEFORE UPDATE OR DELETE ON sales_return_refunds
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_sales_return_refunds_no_truncate
        BEFORE TRUNCATE ON sales_return_refunds
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    CREATE TRIGGER trg_credit_note_redemptions_no_update_delete
        BEFORE UPDATE OR DELETE ON credit_note_redemptions
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_credit_note_redemptions_no_truncate
        BEFORE TRUNCATE ON credit_note_redemptions
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261001142752_Returns') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261001142752_Returns', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    ALTER TABLE supervisor_approvals DROP CONSTRAINT ck_supervisor_approvals_kind;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    ALTER TABLE sales_returns ADD shift_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    ALTER TABLE sales_invoices ADD shift_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE TABLE shifts (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        counter_id uuid NOT NULL,
        cashier_user_id uuid NOT NULL,
        status character varying(10) NOT NULL,
        business_date date NOT NULL,
        opened_at_utc timestamp with time zone NOT NULL,
        opening_float numeric(18,2) NOT NULL,
        closed_at_utc timestamp with time zone,
        closed_by_user_id uuid,
        expected_cash numeric(18,2),
        counted_cash numeric(18,2),
        difference numeric(18,2),
        close_note character varying(300),
        reviewed_by_user_id uuid,
        reviewed_at_utc timestamp with time zone,
        review_note character varying(300),
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_shifts PRIMARY KEY (id),
        CONSTRAINT ck_shifts_close CHECK ((status = 'OPEN' AND closed_at_utc IS NULL AND expected_cash IS NULL AND counted_cash IS NULL AND difference IS NULL) OR (status = 'CLOSED' AND closed_at_utc IS NOT NULL AND expected_cash IS NOT NULL AND counted_cash >= 0 AND difference = counted_cash - expected_cash)),
        CONSTRAINT ck_shifts_float CHECK (opening_float >= 0),
        CONSTRAINT ck_shifts_note CHECK (difference IS NULL OR difference = 0 OR close_note IS NOT NULL),
        CONSTRAINT ck_shifts_review CHECK ((reviewed_by_user_id IS NULL) = (reviewed_at_utc IS NULL) AND (reviewed_by_user_id IS NULL OR (reviewed_by_user_id <> cashier_user_id AND reviewed_by_user_id <> closed_by_user_id))),
        CONSTRAINT ck_shifts_status CHECK (status IN ('OPEN', 'CLOSED')),
        CONSTRAINT fk_shifts_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_shifts_counters_counter_id_business_id FOREIGN KEY (counter_id, business_id) REFERENCES counters (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_shifts_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_shifts_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE TABLE cash_movements (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        shift_id uuid NOT NULL,
        kind character varying(10) NOT NULL,
        amount numeric(18,2) NOT NULL,
        reason character varying(200) NOT NULL,
        recorded_by_user_id uuid NOT NULL,
        approval_id uuid,
        recorded_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_cash_movements PRIMARY KEY (id),
        CONSTRAINT ck_cash_movements_amount CHECK (amount > 0),
        CONSTRAINT ck_cash_movements_kind CHECK (kind IN ('PAY_IN', 'PAY_OUT', 'DROP')),
        CONSTRAINT fk_cash_movements_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_cash_movements_shifts_shift_id FOREIGN KEY (shift_id) REFERENCES shifts (id) ON DELETE RESTRICT,
        CONSTRAINT fk_cash_movements_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE TABLE shift_counts (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        shift_id uuid NOT NULL,
        kind character varying(10) NOT NULL,
        denomination numeric(18,2) NOT NULL,
        count integer NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_shift_counts PRIMARY KEY (id),
        CONSTRAINT ck_shift_counts_kind CHECK (kind IN ('OPENING', 'CLOSING')),
        CONSTRAINT ck_shift_counts_values CHECK (denomination IN (2000, 500, 200, 100, 50, 20, 10, 5, 2, 1) AND count > 0),
        CONSTRAINT fk_shift_counts_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_shift_counts_shifts_shift_id FOREIGN KEY (shift_id) REFERENCES shifts (id) ON DELETE RESTRICT,
        CONSTRAINT fk_shift_counts_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    ALTER TABLE supervisor_approvals ADD CONSTRAINT ck_supervisor_approvals_kind CHECK ((kind = 'PRICE_OVERRIDE' AND variant_unit_id IS NOT NULL AND approved_price >= 0 AND max_amount IS NULL) OR (kind IN ('DISCOUNT', 'RETURN', 'PAY_OUT') AND variant_unit_id IS NULL AND approved_price IS NULL AND max_amount > 0));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE INDEX ix_sales_returns_shift_id ON sales_returns (shift_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE INDEX ix_sales_invoices_shift_id ON sales_invoices (shift_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE INDEX ix_cash_movements_business_id_tenant_id ON cash_movements (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE INDEX ix_cash_movements_shift_id ON cash_movements (shift_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE INDEX ix_cash_movements_tenant_id ON cash_movements (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE INDEX ix_shift_counts_business_id_tenant_id ON shift_counts (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE UNIQUE INDEX ix_shift_counts_shift_id_kind_denomination ON shift_counts (shift_id, kind, denomination);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE INDEX ix_shift_counts_tenant_id ON shift_counts (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE INDEX ix_shifts_business_id_tenant_id ON shifts (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE INDEX ix_shifts_counter_id_business_id ON shifts (counter_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE INDEX ix_shifts_store_id_business_date ON shifts (store_id, business_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE INDEX ix_shifts_store_id_business_id ON shifts (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE INDEX ix_shifts_tenant_id ON shifts (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE UNIQUE INDEX ux_shifts_open_per_cashier ON shifts (cashier_user_id) WHERE status = 'OPEN';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE UNIQUE INDEX ux_shifts_open_per_counter ON shifts (counter_id) WHERE status = 'OPEN';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    ALTER TABLE sales_invoices ADD CONSTRAINT fk_sales_invoices_shifts_shift_id FOREIGN KEY (shift_id) REFERENCES shifts (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    ALTER TABLE sales_returns ADD CONSTRAINT fk_sales_returns_shifts_shift_id FOREIGN KEY (shift_id) REFERENCES shifts (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    ALTER TABLE shifts ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON shifts
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE shift_counts ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON shift_counts
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE cash_movements ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON cash_movements
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE TRIGGER trg_shift_counts_no_update_delete
        BEFORE UPDATE OR DELETE ON shift_counts
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_shift_counts_no_truncate
        BEFORE TRUNCATE ON shift_counts
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE TRIGGER trg_cash_movements_no_update_delete
        BEFORE UPDATE OR DELETE ON cash_movements
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_cash_movements_no_truncate
        BEFORE TRUNCATE ON cash_movements
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    CREATE FUNCTION sb_shift_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Shifts cannot be deleted.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.store_id, NEW.counter_id, NEW.cashier_user_id, NEW.business_date, NEW.opened_at_utc, NEW.opening_float)
           IS DISTINCT FROM
           (OLD.id, OLD.tenant_id, OLD.business_id, OLD.store_id, OLD.counter_id, OLD.cashier_user_id, OLD.business_date, OLD.opened_at_utc, OLD.opening_float) THEN
            RAISE EXCEPTION 'A shift''s opening cannot be changed.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF OLD.status = 'CLOSED' AND (NEW.status, NEW.closed_at_utc, NEW.closed_by_user_id, NEW.expected_cash, NEW.counted_cash, NEW.difference, NEW.close_note)
           IS DISTINCT FROM (OLD.status, OLD.closed_at_utc, OLD.closed_by_user_id, OLD.expected_cash, OLD.counted_cash, OLD.difference, OLD.close_note) THEN
            RAISE EXCEPTION 'A closed shift''s count cannot be changed.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF OLD.reviewed_by_user_id IS NOT NULL AND (NEW.reviewed_by_user_id, NEW.reviewed_at_utc, NEW.review_note)
           IS DISTINCT FROM (OLD.reviewed_by_user_id, OLD.reviewed_at_utc, OLD.review_note) THEN
            RAISE EXCEPTION 'A shift''s review cannot be changed.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF NEW.status = 'OPEN' AND NEW.reviewed_by_user_id IS NOT NULL THEN
            RAISE EXCEPTION 'An open shift cannot be reviewed.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_shifts_guard BEFORE UPDATE OR DELETE ON shifts FOR EACH ROW EXECUTE FUNCTION sb_shift_guard();
    CREATE TRIGGER trg_shifts_no_truncate BEFORE TRUNCATE ON shifts FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();

    CREATE FUNCTION sb_require_open_shift() RETURNS trigger
    LANGUAGE plpgsql AS $$
    DECLARE
        s shifts%ROWTYPE;
    BEGIN
        IF NEW.shift_id IS NULL THEN
            IF TG_TABLE_NAME = 'cash_movements' THEN
                RAISE EXCEPTION 'A cash movement needs a shift.' USING ERRCODE = 'restrict_violation';
            END IF;
            RAISE EXCEPTION 'A % must be recorded in a shift.', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
        END IF;
        SELECT * INTO s FROM shifts WHERE id = NEW.shift_id;
        IF s.status IS DISTINCT FROM 'OPEN' THEN
            RAISE EXCEPTION 'Shift % is not open.', NEW.shift_id USING ERRCODE = 'restrict_violation';
        END IF;
        -- Nested: NEW.counter_id does not exist on cash_movements, and PL/pgSQL would evaluate it inside an AND.
        IF TG_TABLE_NAME <> 'cash_movements' THEN
            IF s.counter_id <> NEW.counter_id OR s.cashier_user_id <> NEW.cashier_user_id THEN
                RAISE EXCEPTION 'The document belongs to another counter''s or cashier''s shift.' USING ERRCODE = 'restrict_violation';
            END IF;
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_sales_invoices_open_shift BEFORE INSERT ON sales_invoices FOR EACH ROW EXECUTE FUNCTION sb_require_open_shift();
    CREATE TRIGGER trg_sales_returns_open_shift BEFORE INSERT ON sales_returns FOR EACH ROW EXECUTE FUNCTION sb_require_open_shift();
    CREATE TRIGGER trg_cash_movements_open_shift BEFORE INSERT ON cash_movements FOR EACH ROW EXECUTE FUNCTION sb_require_open_shift();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002045018_Shifts') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261002045018_Shifts', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE TABLE purchase_settings (
        business_id uuid NOT NULL,
        cost_reason_threshold_percent numeric(7,2) NOT NULL,
        cost_approval_threshold_percent numeric(7,2) NOT NULL,
        allow_loss_leader boolean NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_purchase_settings PRIMARY KEY (business_id),
        CONSTRAINT ck_purchase_settings_thresholds CHECK (cost_reason_threshold_percent >= 0 AND cost_approval_threshold_percent >= cost_reason_threshold_percent),
        CONSTRAINT fk_purchase_settings_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_purchase_settings_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE TABLE suppliers (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        code character varying(20) NOT NULL,
        name character varying(200) NOT NULL,
        gstin character varying(15),
        state_code character varying(2) NOT NULL,
        address character varying(500),
        phone character varying(20),
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_suppliers PRIMARY KEY (id),
        CONSTRAINT ak_suppliers_id_business_id UNIQUE (id, business_id),
        CONSTRAINT ck_suppliers_code CHECK (code ~ '^[A-Z0-9-]{1,20}$'),
        CONSTRAINT ck_suppliers_gstin_state CHECK (gstin IS NULL OR left(gstin, 2) = state_code),
        CONSTRAINT fk_suppliers_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_suppliers_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE TABLE grns (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        supplier_id uuid NOT NULL,
        number character varying(40) NOT NULL,
        sequence_number bigint NOT NULL,
        supplier_invoice_number character varying(30) NOT NULL,
        supplier_invoice_date date NOT NULL,
        classification character varying(20) NOT NULL,
        purchase_order_reference character varying(40),
        is_inter_state boolean NOT NULL,
        tax_recoverable boolean NOT NULL,
        status character varying(20) NOT NULL,
        business_date date NOT NULL,
        notes character varying(500),
        gross_total numeric(18,2) NOT NULL,
        discount_total numeric(18,2) NOT NULL,
        taxable_total numeric(18,2) NOT NULL,
        cgst_total numeric(18,2) NOT NULL,
        sgst_total numeric(18,2) NOT NULL,
        igst_total numeric(18,2) NOT NULL,
        cess_total numeric(18,2) NOT NULL,
        round_off numeric(18,2) NOT NULL,
        invoice_total numeric(18,2) NOT NULL,
        expenses_total numeric(18,2) NOT NULL,
        landed_total numeric(18,2) NOT NULL,
        approval_request_id uuid,
        received_by_user_id uuid NOT NULL,
        received_at_utc timestamp with time zone NOT NULL,
        posted_at_utc timestamp with time zone,
        idempotency_key character varying(100) NOT NULL,
        request_hash character varying(64) NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_grns PRIMARY KEY (id),
        CONSTRAINT ak_grns_id_business_id UNIQUE (id, business_id),
        CONSTRAINT ck_grns_classification CHECK (classification IN ('GST_TAX_INVOICE', 'BILL_OF_SUPPLY', 'UNREGISTERED', 'IMPORT', 'REVERSE_CHARGE', 'PENDING_DOCUMENT', 'OTHER')),
        CONSTRAINT ck_grns_posted CHECK ((status = 'POSTED') = (posted_at_utc IS NOT NULL)),
        CONSTRAINT ck_grns_status CHECK (status IN ('PENDING_APPROVAL', 'POSTED', 'REJECTED')),
        CONSTRAINT ck_grns_totals CHECK (invoice_total = taxable_total + cgst_total + sgst_total + igst_total + cess_total + round_off AND abs(round_off) <= 1 AND taxable_total = gross_total - discount_total AND cgst_total = sgst_total),
        CONSTRAINT fk_grns_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_grns_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_grns_suppliers_supplier_id_business_id FOREIGN KEY (supplier_id, business_id) REFERENCES suppliers (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_grns_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE TABLE grn_expenses (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        grn_id uuid NOT NULL,
        expense_order integer NOT NULL,
        kind character varying(12) NOT NULL,
        amount numeric(18,2) NOT NULL,
        method character varying(10) NOT NULL,
        note character varying(200),
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_grn_expenses PRIMARY KEY (id),
        CONSTRAINT ck_grn_expenses_amount CHECK (amount > 0),
        CONSTRAINT ck_grn_expenses_kind CHECK (kind IN ('FREIGHT', 'LOADING', 'INSURANCE', 'PACKING', 'HANDLING', 'TRANSPORT', 'CUSTOMS', 'OTHER')),
        CONSTRAINT ck_grn_expenses_method CHECK (method IN ('QUANTITY', 'VALUE', 'WEIGHT', 'VOLUME', 'EQUAL', 'MANUAL')),
        CONSTRAINT fk_grn_expenses_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_grn_expenses_grns_grn_id_business_id FOREIGN KEY (grn_id, business_id) REFERENCES grns (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_grn_expenses_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE TABLE grn_lines (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        grn_id uuid NOT NULL,
        line_number integer NOT NULL,
        product_id uuid NOT NULL,
        variant_id uuid NOT NULL,
        variant_unit_id uuid NOT NULL,
        description character varying(200) NOT NULL,
        unit_code character varying(10) NOT NULL,
        factor_to_base numeric(18,6) NOT NULL,
        quantity numeric(18,3) NOT NULL,
        free_quantity numeric(18,3) NOT NULL,
        base_quantity numeric(18,3) NOT NULL,
        mrp numeric(18,2),
        rate numeric(18,4) NOT NULL,
        discount numeric(18,2) NOT NULL,
        gst_rate_percent numeric(5,2) NOT NULL,
        cess_rate_percent numeric(5,2) NOT NULL,
        gross numeric(18,2) NOT NULL,
        taxable numeric(18,2) NOT NULL,
        cgst numeric(18,2) NOT NULL,
        sgst numeric(18,2) NOT NULL,
        igst numeric(18,2) NOT NULL,
        cess numeric(18,2) NOT NULL,
        total numeric(18,2) NOT NULL,
        expense_share numeric(18,2) NOT NULL,
        non_recoverable_tax numeric(18,2) NOT NULL,
        landed_total numeric(18,2) NOT NULL,
        landed_unit_cost numeric(18,4) NOT NULL,
        batch_number character varying(30),
        manufactured_on date,
        expires_on date,
        selling_price numeric(18,2),
        previous_unit_cost numeric(18,4),
        cost_change_percent numeric(9,2),
        cost_change_reason character varying(300),
        loss_leader_reason character varying(300),
        weight numeric(18,3),
        volume numeric(18,3),
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_grn_lines PRIMARY KEY (id),
        CONSTRAINT ck_grn_lines_amounts CHECK (quantity >= 0 AND free_quantity >= 0 AND quantity + free_quantity > 0 AND factor_to_base > 0 AND rate >= 0 AND discount >= 0 AND taxable = gross - discount AND total = taxable + cgst + sgst + igst + cess AND cgst = sgst AND (cgst = 0 OR igst = 0)),
        CONSTRAINT ck_grn_lines_landed CHECK (landed_total = taxable + non_recoverable_tax + expense_share AND base_quantity = (quantity + free_quantity) * factor_to_base AND landed_unit_cost >= 0),
        CONSTRAINT fk_grn_lines_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_grn_lines_grns_grn_id_business_id FOREIGN KEY (grn_id, business_id) REFERENCES grns (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_grn_lines_product_variants_variant_id_business_id FOREIGN KEY (variant_id, business_id) REFERENCES product_variants (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_grn_lines_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_grn_lines_variant_units_variant_unit_id FOREIGN KEY (variant_unit_id) REFERENCES variant_units (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE TABLE grn_allocations (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        expense_id uuid NOT NULL,
        line_id uuid NOT NULL,
        amount numeric(18,2) NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_grn_allocations PRIMARY KEY (id),
        CONSTRAINT ck_grn_allocations_amount CHECK (amount >= 0),
        CONSTRAINT fk_grn_allocations_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_grn_allocations_grn_expenses_expense_id FOREIGN KEY (expense_id) REFERENCES grn_expenses (id) ON DELETE RESTRICT,
        CONSTRAINT fk_grn_allocations_grn_lines_line_id FOREIGN KEY (line_id) REFERENCES grn_lines (id) ON DELETE RESTRICT,
        CONSTRAINT fk_grn_allocations_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_grn_allocations_business_id_tenant_id ON grn_allocations (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE UNIQUE INDEX ix_grn_allocations_expense_id_line_id ON grn_allocations (expense_id, line_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_grn_allocations_line_id ON grn_allocations (line_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_grn_allocations_tenant_id ON grn_allocations (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_grn_expenses_business_id_tenant_id ON grn_expenses (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_grn_expenses_grn_id_business_id ON grn_expenses (grn_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE UNIQUE INDEX ix_grn_expenses_grn_id_expense_order ON grn_expenses (grn_id, expense_order);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_grn_expenses_tenant_id ON grn_expenses (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_grn_lines_business_id_tenant_id ON grn_lines (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_grn_lines_grn_id_business_id ON grn_lines (grn_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE UNIQUE INDEX ix_grn_lines_grn_id_line_number ON grn_lines (grn_id, line_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_grn_lines_tenant_id ON grn_lines (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_grn_lines_variant_id_business_id ON grn_lines (variant_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_grn_lines_variant_id_mrp ON grn_lines (variant_id, mrp);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_grn_lines_variant_unit_id ON grn_lines (variant_unit_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE UNIQUE INDEX ix_grns_business_id_idempotency_key ON grns (business_id, idempotency_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_grns_business_id_tenant_id ON grns (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_grns_store_id_business_date ON grns (store_id, business_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_grns_store_id_business_id ON grns (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE UNIQUE INDEX ix_grns_store_id_sequence_number ON grns (store_id, sequence_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_grns_supplier_id_business_id ON grns (supplier_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_grns_tenant_id ON grns (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE UNIQUE INDEX ux_grns_supplier_invoice ON grns (business_id, supplier_id, supplier_invoice_number) WHERE status <> 'REJECTED';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_purchase_settings_business_id_tenant_id ON purchase_settings (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_purchase_settings_tenant_id ON purchase_settings (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE UNIQUE INDEX ix_suppliers_business_id_code ON suppliers (business_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_suppliers_business_id_gstin ON suppliers (business_id, gstin);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_suppliers_business_id_tenant_id ON suppliers (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE INDEX ix_suppliers_tenant_id ON suppliers (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    ALTER TABLE suppliers ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON suppliers
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE purchase_settings ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON purchase_settings
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE grns ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON grns
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE grn_lines ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON grn_lines
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE grn_expenses ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON grn_expenses
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE grn_allocations ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON grn_allocations
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE TRIGGER trg_grn_lines_no_update_delete
        BEFORE UPDATE OR DELETE ON grn_lines
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_grn_lines_no_truncate
        BEFORE TRUNCATE ON grn_lines
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE TRIGGER trg_grn_expenses_no_update_delete
        BEFORE UPDATE OR DELETE ON grn_expenses
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_grn_expenses_no_truncate
        BEFORE TRUNCATE ON grn_expenses
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE TRIGGER trg_grn_allocations_no_update_delete
        BEFORE UPDATE OR DELETE ON grn_allocations
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_grn_allocations_no_truncate
        BEFORE TRUNCATE ON grn_allocations
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    CREATE FUNCTION sb_grn_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Goods receipts cannot be deleted.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.store_id, NEW.supplier_id, NEW.number, NEW.sequence_number, NEW.supplier_invoice_number,
            NEW.supplier_invoice_date, NEW.classification, NEW.purchase_order_reference, NEW.is_inter_state, NEW.tax_recoverable, NEW.business_date,
            NEW.notes, NEW.gross_total, NEW.discount_total, NEW.taxable_total, NEW.cgst_total, NEW.sgst_total, NEW.igst_total, NEW.cess_total,
            NEW.round_off, NEW.invoice_total, NEW.expenses_total, NEW.landed_total, NEW.received_by_user_id, NEW.received_at_utc,
            NEW.idempotency_key, NEW.request_hash)
           IS DISTINCT FROM
           (OLD.id, OLD.tenant_id, OLD.business_id, OLD.store_id, OLD.supplier_id, OLD.number, OLD.sequence_number, OLD.supplier_invoice_number,
            OLD.supplier_invoice_date, OLD.classification, OLD.purchase_order_reference, OLD.is_inter_state, OLD.tax_recoverable, OLD.business_date,
            OLD.notes, OLD.gross_total, OLD.discount_total, OLD.taxable_total, OLD.cgst_total, OLD.sgst_total, OLD.igst_total, OLD.cess_total,
            OLD.round_off, OLD.invoice_total, OLD.expenses_total, OLD.landed_total, OLD.received_by_user_id, OLD.received_at_utc,
            OLD.idempotency_key, OLD.request_hash) THEN
            RAISE EXCEPTION 'A goods receipt''s figures cannot be changed.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF OLD.status <> 'PENDING_APPROVAL' AND (NEW.status, NEW.posted_at_utc, NEW.approval_request_id) IS DISTINCT FROM (OLD.status, OLD.posted_at_utc, OLD.approval_request_id) THEN
            RAISE EXCEPTION 'A % goods receipt cannot change.', lower(OLD.status) USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_grns_guard BEFORE UPDATE OR DELETE ON grns FOR EACH ROW EXECUTE FUNCTION sb_grn_guard();
    CREATE TRIGGER trg_grns_no_truncate BEFORE TRUNCATE ON grns FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    INSERT INTO purchase_settings (business_id, tenant_id, cost_reason_threshold_percent, cost_approval_threshold_percent, allow_loss_leader)
    SELECT id, tenant_id, 5, 15, false FROM businesses
    ON CONFLICT (business_id) DO NOTHING;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002065434_Purchases') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261002065434_Purchases', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    ALTER TABLE grns ADD purchase_order_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    ALTER TABLE grn_lines ADD update_selling_price boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE TABLE attachments (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        owner_type character varying(20) NOT NULL,
        owner_id uuid NOT NULL,
        file_name character varying(100) NOT NULL,
        content_type character varying(40) NOT NULL,
        size bigint NOT NULL,
        sha256 character varying(64) NOT NULL,
        content bytea NOT NULL,
        uploaded_by_user_id uuid NOT NULL,
        uploaded_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_attachments PRIMARY KEY (id),
        CONSTRAINT ck_attachments_owner CHECK (owner_type IN ('GRN')),
        CONSTRAINT ck_attachments_size CHECK (size > 0 AND size <= 10485760 AND size = octet_length(content)),
        CONSTRAINT ck_attachments_type CHECK (content_type IN ('application/pdf', 'image/jpeg', 'image/png')),
        CONSTRAINT fk_attachments_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_attachments_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE TABLE purchase_orders (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        supplier_id uuid NOT NULL,
        number character varying(40) NOT NULL,
        sequence_number bigint NOT NULL,
        order_date date NOT NULL,
        expected_date date,
        status character varying(10) NOT NULL,
        notes character varying(500),
        created_by_user_id uuid NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        closed_at_utc timestamp with time zone,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_purchase_orders PRIMARY KEY (id),
        CONSTRAINT ak_purchase_orders_id_business_id UNIQUE (id, business_id),
        CONSTRAINT ck_purchase_orders_closed CHECK ((status = 'OPEN') = (closed_at_utc IS NULL)),
        CONSTRAINT ck_purchase_orders_dates CHECK (expected_date IS NULL OR expected_date >= order_date),
        CONSTRAINT ck_purchase_orders_status CHECK (status IN ('OPEN', 'CLOSED', 'CANCELLED')),
        CONSTRAINT fk_purchase_orders_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_purchase_orders_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_purchase_orders_suppliers_supplier_id_business_id FOREIGN KEY (supplier_id, business_id) REFERENCES suppliers (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_purchase_orders_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE TABLE purchase_order_lines (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        purchase_order_id uuid NOT NULL,
        line_number integer NOT NULL,
        variant_id uuid NOT NULL,
        variant_unit_id uuid NOT NULL,
        quantity numeric(18,3) NOT NULL,
        rate numeric(18,4),
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_purchase_order_lines PRIMARY KEY (id),
        CONSTRAINT ck_purchase_order_lines_quantity CHECK (quantity > 0 AND (rate IS NULL OR rate >= 0)),
        CONSTRAINT fk_purchase_order_lines_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_purchase_order_lines_product_variants_variant_id_business_id FOREIGN KEY (variant_id, business_id) REFERENCES product_variants (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_purchase_order_lines_purchase_orders_purchase_order_id_busi FOREIGN KEY (purchase_order_id, business_id) REFERENCES purchase_orders (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_purchase_order_lines_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_purchase_order_lines_variant_units_variant_unit_id FOREIGN KEY (variant_unit_id) REFERENCES variant_units (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE INDEX ix_grns_purchase_order_id_business_id ON grns (purchase_order_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE INDEX ix_attachments_business_id_tenant_id ON attachments (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE INDEX ix_attachments_owner_type_owner_id ON attachments (owner_type, owner_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE INDEX ix_attachments_tenant_id ON attachments (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE INDEX ix_purchase_order_lines_business_id_tenant_id ON purchase_order_lines (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE INDEX ix_purchase_order_lines_purchase_order_id_business_id ON purchase_order_lines (purchase_order_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE UNIQUE INDEX ix_purchase_order_lines_purchase_order_id_line_number ON purchase_order_lines (purchase_order_id, line_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE UNIQUE INDEX ix_purchase_order_lines_purchase_order_id_variant_unit_id ON purchase_order_lines (purchase_order_id, variant_unit_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE INDEX ix_purchase_order_lines_tenant_id ON purchase_order_lines (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE INDEX ix_purchase_order_lines_variant_id_business_id ON purchase_order_lines (variant_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE INDEX ix_purchase_order_lines_variant_unit_id ON purchase_order_lines (variant_unit_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE INDEX ix_purchase_orders_business_id_tenant_id ON purchase_orders (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE INDEX ix_purchase_orders_store_id_business_id ON purchase_orders (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE UNIQUE INDEX ix_purchase_orders_store_id_sequence_number ON purchase_orders (store_id, sequence_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE INDEX ix_purchase_orders_store_id_status ON purchase_orders (store_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE INDEX ix_purchase_orders_supplier_id_business_id ON purchase_orders (supplier_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE INDEX ix_purchase_orders_tenant_id ON purchase_orders (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    ALTER TABLE grns ADD CONSTRAINT fk_grns_purchase_orders_purchase_order_id_business_id FOREIGN KEY (purchase_order_id, business_id) REFERENCES purchase_orders (id, business_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    ALTER TABLE purchase_orders ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON purchase_orders
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE purchase_order_lines ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON purchase_order_lines
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE attachments ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON attachments
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE TRIGGER trg_purchase_order_lines_no_update_delete
        BEFORE UPDATE OR DELETE ON purchase_order_lines
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_purchase_order_lines_no_truncate
        BEFORE TRUNCATE ON purchase_order_lines
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE TRIGGER trg_attachments_no_update_delete
        BEFORE UPDATE OR DELETE ON attachments
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_attachments_no_truncate
        BEFORE TRUNCATE ON attachments
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE FUNCTION sb_purchase_order_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Purchase orders cannot be deleted.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.store_id, NEW.supplier_id, NEW.number, NEW.sequence_number, NEW.order_date,
            NEW.expected_date, NEW.notes, NEW.created_by_user_id, NEW.created_at_utc)
           IS DISTINCT FROM
           (OLD.id, OLD.tenant_id, OLD.business_id, OLD.store_id, OLD.supplier_id, OLD.number, OLD.sequence_number, OLD.order_date,
            OLD.expected_date, OLD.notes, OLD.created_by_user_id, OLD.created_at_utc) THEN
            RAISE EXCEPTION 'A purchase order''s terms cannot be changed.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF OLD.status <> 'OPEN' AND (NEW.status, NEW.closed_at_utc) IS DISTINCT FROM (OLD.status, OLD.closed_at_utc) THEN
            RAISE EXCEPTION 'A % purchase order cannot change.', lower(OLD.status) USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_purchase_orders_guard BEFORE UPDATE OR DELETE ON purchase_orders FOR EACH ROW EXECUTE FUNCTION sb_purchase_order_guard();
    CREATE TRIGGER trg_purchase_orders_no_truncate BEFORE TRUNCATE ON purchase_orders FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    CREATE OR REPLACE FUNCTION sb_grn_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Goods receipts cannot be deleted.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.store_id, NEW.supplier_id, NEW.number, NEW.sequence_number, NEW.supplier_invoice_number,
            NEW.supplier_invoice_date, NEW.classification, NEW.purchase_order_reference, NEW.is_inter_state, NEW.tax_recoverable, NEW.business_date,
            NEW.notes, NEW.gross_total, NEW.discount_total, NEW.taxable_total, NEW.cgst_total, NEW.sgst_total, NEW.igst_total, NEW.cess_total,
            NEW.round_off, NEW.invoice_total, NEW.expenses_total, NEW.landed_total, NEW.received_by_user_id, NEW.received_at_utc,
            NEW.idempotency_key, NEW.request_hash, NEW.purchase_order_id)
           IS DISTINCT FROM
           (OLD.id, OLD.tenant_id, OLD.business_id, OLD.store_id, OLD.supplier_id, OLD.number, OLD.sequence_number, OLD.supplier_invoice_number,
            OLD.supplier_invoice_date, OLD.classification, OLD.purchase_order_reference, OLD.is_inter_state, OLD.tax_recoverable, OLD.business_date,
            OLD.notes, OLD.gross_total, OLD.discount_total, OLD.taxable_total, OLD.cgst_total, OLD.sgst_total, OLD.igst_total, OLD.cess_total,
            OLD.round_off, OLD.invoice_total, OLD.expenses_total, OLD.landed_total, OLD.received_by_user_id, OLD.received_at_utc,
            OLD.idempotency_key, OLD.request_hash, OLD.purchase_order_id) THEN
            RAISE EXCEPTION 'A goods receipt''s figures cannot be changed.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF OLD.status <> 'PENDING_APPROVAL' AND (NEW.status, NEW.posted_at_utc, NEW.approval_request_id) IS DISTINCT FROM (OLD.status, OLD.posted_at_utc, OLD.approval_request_id) THEN
            RAISE EXCEPTION 'A % goods receipt cannot change.', lower(OLD.status) USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;

    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002073046_PurchaseOrders') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261002073046_PurchaseOrders', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    ALTER TABLE suppliers ADD consent_changed_at_utc timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    ALTER TABLE suppliers ADD contact_person character varying(100);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    ALTER TABLE suppliers ADD credit_period_days integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    ALTER TABLE suppliers ADD email character varying(200);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    ALTER TABLE suppliers ADD sms_consent boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    ALTER TABLE suppliers ADD sms_number character varying(13);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    ALTER TABLE suppliers ADD trade_name character varying(200);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    ALTER TABLE suppliers ADD whatsapp_consent boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    ALTER TABLE suppliers ADD whatsapp_number character varying(13);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE TABLE debtors (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        code character varying(20) NOT NULL,
        legal_name character varying(200) NOT NULL,
        trade_name character varying(200),
        gstin character varying(15),
        state_code character varying(2) NOT NULL,
        address character varying(500),
        contact_person character varying(100),
        phone character varying(20),
        email character varying(200),
        whatsapp_number character varying(13),
        sms_number character varying(13),
        whatsapp_consent boolean NOT NULL,
        sms_consent boolean NOT NULL,
        consent_changed_at_utc timestamp with time zone,
        credit_period_days integer NOT NULL,
        credit_limit numeric(18,2) NOT NULL,
        customer_group_id uuid,
        status character varying(10) NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_debtors PRIMARY KEY (id),
        CONSTRAINT ak_debtors_id_business_id UNIQUE (id, business_id),
        CONSTRAINT ck_debtors_code CHECK (code ~ '^[A-Z0-9-]{1,20}$'),
        CONSTRAINT ck_debtors_consent CHECK ((NOT whatsapp_consent OR whatsapp_number IS NOT NULL) AND (NOT sms_consent OR sms_number IS NOT NULL)),
        CONSTRAINT ck_debtors_credit CHECK (credit_limit >= 0 AND credit_period_days BETWEEN 0 AND 365),
        CONSTRAINT ck_debtors_gstin_state CHECK (gstin IS NULL OR left(gstin, 2) = state_code),
        CONSTRAINT ck_debtors_status CHECK (status IN ('ACTIVE', 'ON_HOLD', 'CLOSED')),
        CONSTRAINT fk_debtors_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_debtors_customer_groups_customer_group_id_business_id FOREIGN KEY (customer_group_id, business_id) REFERENCES customer_groups (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_debtors_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE TABLE supplier_ledger (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        business_id uuid NOT NULL,
        supplier_id uuid NOT NULL,
        sequence bigint NOT NULL,
        entry_type character varying(20) NOT NULL,
        store_id uuid,
        document_id uuid,
        document_number character varying(40),
        entry_date date NOT NULL,
        due_date date,
        amount numeric(18,2) NOT NULL,
        balance_after numeric(18,2) NOT NULL,
        narration character varying(300) NOT NULL,
        created_by_user_id uuid NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT pk_supplier_ledger PRIMARY KEY (id),
        CONSTRAINT ak_supplier_ledger_id_supplier_id UNIQUE (id, supplier_id),
        CONSTRAINT ck_supplier_ledger_amount CHECK (amount <> 0 AND sequence > 0),
        CONSTRAINT ck_supplier_ledger_due CHECK ((amount > 0) = (due_date IS NOT NULL)),
        CONSTRAINT ck_supplier_ledger_sign CHECK ((entry_type NOT IN ('GRN', 'INVOICE') OR amount > 0) AND (entry_type NOT IN ('PAYMENT', 'DEBIT_NOTE', 'RECEIPT', 'CREDIT_NOTE') OR amount < 0)),
        CONSTRAINT ck_supplier_ledger_type CHECK (entry_type IN ('OPENING', 'GRN', 'PAYMENT', 'DEBIT_NOTE', 'ADJUSTMENT')),
        CONSTRAINT fk_supplier_ledger_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_supplier_ledger_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_supplier_ledger_suppliers_supplier_id_business_id FOREIGN KEY (supplier_id, business_id) REFERENCES suppliers (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_supplier_ledger_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE TABLE supplier_payments (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        supplier_id uuid NOT NULL,
        number character varying(40) NOT NULL,
        sequence_number bigint NOT NULL,
        payment_date date NOT NULL,
        method character varying(20) NOT NULL,
        reference character varying(40),
        amount numeric(18,2) NOT NULL,
        note character varying(300) NOT NULL,
        paid_by_user_id uuid NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        idempotency_key character varying(100) NOT NULL,
        request_hash character varying(64) NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_supplier_payments PRIMARY KEY (id),
        CONSTRAINT ck_supplier_payments_amount CHECK (amount > 0),
        CONSTRAINT ck_supplier_payments_cheque CHECK (method <> 'CHEQUE' OR reference IS NOT NULL),
        CONSTRAINT ck_supplier_payments_method CHECK (method IN ('CASH', 'BANK_TRANSFER', 'UPI', 'CHEQUE')),
        CONSTRAINT fk_supplier_payments_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_supplier_payments_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_supplier_payments_suppliers_supplier_id_business_id FOREIGN KEY (supplier_id, business_id) REFERENCES suppliers (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_supplier_payments_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE TABLE debtor_ledger (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        business_id uuid NOT NULL,
        debtor_id uuid NOT NULL,
        sequence bigint NOT NULL,
        entry_type character varying(20) NOT NULL,
        store_id uuid,
        document_id uuid,
        document_number character varying(40),
        entry_date date NOT NULL,
        due_date date,
        amount numeric(18,2) NOT NULL,
        balance_after numeric(18,2) NOT NULL,
        narration character varying(300) NOT NULL,
        created_by_user_id uuid NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT pk_debtor_ledger PRIMARY KEY (id),
        CONSTRAINT ak_debtor_ledger_id_debtor_id UNIQUE (id, debtor_id),
        CONSTRAINT ck_debtor_ledger_amount CHECK (amount <> 0 AND sequence > 0),
        CONSTRAINT ck_debtor_ledger_due CHECK ((amount > 0) = (due_date IS NOT NULL)),
        CONSTRAINT ck_debtor_ledger_sign CHECK ((entry_type NOT IN ('GRN', 'INVOICE') OR amount > 0) AND (entry_type NOT IN ('PAYMENT', 'DEBIT_NOTE', 'RECEIPT', 'CREDIT_NOTE') OR amount < 0)),
        CONSTRAINT ck_debtor_ledger_type CHECK (entry_type IN ('OPENING', 'INVOICE', 'RECEIPT', 'CREDIT_NOTE', 'ADJUSTMENT')),
        CONSTRAINT fk_debtor_ledger_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_debtor_ledger_debtors_debtor_id_business_id FOREIGN KEY (debtor_id, business_id) REFERENCES debtors (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_debtor_ledger_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_debtor_ledger_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE TABLE supplier_settlements (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        business_id uuid NOT NULL,
        supplier_id uuid NOT NULL,
        charge_entry_id uuid NOT NULL,
        payment_entry_id uuid NOT NULL,
        amount numeric(18,2) NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT pk_supplier_settlements PRIMARY KEY (id),
        CONSTRAINT ck_supplier_settlements_amount CHECK (amount > 0 AND charge_entry_id <> payment_entry_id),
        CONSTRAINT fk_supplier_settlements_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_supplier_settlements_supplier_ledger_charge_entry_id_suppli FOREIGN KEY (charge_entry_id, supplier_id) REFERENCES supplier_ledger (id, supplier_id) ON DELETE RESTRICT,
        CONSTRAINT fk_supplier_settlements_supplier_ledger_payment_entry_id_suppl FOREIGN KEY (payment_entry_id, supplier_id) REFERENCES supplier_ledger (id, supplier_id) ON DELETE RESTRICT,
        CONSTRAINT fk_supplier_settlements_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE TABLE debtor_settlements (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        business_id uuid NOT NULL,
        debtor_id uuid NOT NULL,
        charge_entry_id uuid NOT NULL,
        payment_entry_id uuid NOT NULL,
        amount numeric(18,2) NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        CONSTRAINT pk_debtor_settlements PRIMARY KEY (id),
        CONSTRAINT ck_debtor_settlements_amount CHECK (amount > 0 AND charge_entry_id <> payment_entry_id),
        CONSTRAINT fk_debtor_settlements_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_debtor_settlements_debtor_ledger_charge_entry_id_debtor_id FOREIGN KEY (charge_entry_id, debtor_id) REFERENCES debtor_ledger (id, debtor_id) ON DELETE RESTRICT,
        CONSTRAINT fk_debtor_settlements_debtor_ledger_payment_entry_id_debtor_id FOREIGN KEY (payment_entry_id, debtor_id) REFERENCES debtor_ledger (id, debtor_id) ON DELETE RESTRICT,
        CONSTRAINT fk_debtor_settlements_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    ALTER TABLE suppliers ADD CONSTRAINT ck_suppliers_consent CHECK ((NOT whatsapp_consent OR whatsapp_number IS NOT NULL) AND (NOT sms_consent OR sms_number IS NOT NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    ALTER TABLE suppliers ADD CONSTRAINT ck_suppliers_credit_period CHECK (credit_period_days BETWEEN 0 AND 365);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_debtor_ledger_business_id_tenant_id ON debtor_ledger (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_debtor_ledger_debtor_id_business_id ON debtor_ledger (debtor_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE UNIQUE INDEX ix_debtor_ledger_debtor_id_sequence ON debtor_ledger (debtor_id, sequence);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_debtor_ledger_document_id_entry_type ON debtor_ledger (document_id, entry_type);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_debtor_ledger_store_id_business_id ON debtor_ledger (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_debtor_ledger_tenant_id ON debtor_ledger (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_debtor_settlements_business_id_tenant_id ON debtor_settlements (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_debtor_settlements_charge_entry_id ON debtor_settlements (charge_entry_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_debtor_settlements_charge_entry_id_debtor_id ON debtor_settlements (charge_entry_id, debtor_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_debtor_settlements_payment_entry_id ON debtor_settlements (payment_entry_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_debtor_settlements_payment_entry_id_debtor_id ON debtor_settlements (payment_entry_id, debtor_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_debtor_settlements_tenant_id ON debtor_settlements (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE UNIQUE INDEX ix_debtors_business_id_code ON debtors (business_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_debtors_business_id_gstin ON debtors (business_id, gstin);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_debtors_business_id_tenant_id ON debtors (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_debtors_customer_group_id_business_id ON debtors (customer_group_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_debtors_tenant_id ON debtors (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_supplier_ledger_business_id_tenant_id ON supplier_ledger (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_supplier_ledger_document_id_entry_type ON supplier_ledger (document_id, entry_type);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_supplier_ledger_store_id_business_id ON supplier_ledger (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_supplier_ledger_supplier_id_business_id ON supplier_ledger (supplier_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE UNIQUE INDEX ix_supplier_ledger_supplier_id_sequence ON supplier_ledger (supplier_id, sequence);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_supplier_ledger_tenant_id ON supplier_ledger (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE UNIQUE INDEX ix_supplier_payments_business_id_idempotency_key ON supplier_payments (business_id, idempotency_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_supplier_payments_business_id_tenant_id ON supplier_payments (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_supplier_payments_store_id_business_id ON supplier_payments (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE UNIQUE INDEX ix_supplier_payments_store_id_sequence_number ON supplier_payments (store_id, sequence_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_supplier_payments_supplier_id_business_id ON supplier_payments (supplier_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_supplier_payments_supplier_id_payment_date ON supplier_payments (supplier_id, payment_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_supplier_payments_tenant_id ON supplier_payments (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_supplier_settlements_business_id_tenant_id ON supplier_settlements (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_supplier_settlements_charge_entry_id ON supplier_settlements (charge_entry_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_supplier_settlements_charge_entry_id_supplier_id ON supplier_settlements (charge_entry_id, supplier_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_supplier_settlements_payment_entry_id ON supplier_settlements (payment_entry_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_supplier_settlements_payment_entry_id_supplier_id ON supplier_settlements (payment_entry_id, supplier_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE INDEX ix_supplier_settlements_tenant_id ON supplier_settlements (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    ALTER TABLE debtors ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON debtors
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE supplier_ledger ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON supplier_ledger
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE debtor_ledger ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON debtor_ledger
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE supplier_settlements ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON supplier_settlements
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE debtor_settlements ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON debtor_settlements
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE supplier_payments ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON supplier_payments
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    INSERT INTO supplier_ledger (id, tenant_id, business_id, supplier_id, sequence, entry_type, store_id, document_id, document_number, entry_date,
                                 due_date, amount, balance_after, narration, created_by_user_id, created_at_utc)
    SELECT gen_random_uuid(), g.tenant_id, g.business_id, g.supplier_id,
           row_number() OVER w, 'GRN', g.store_id, g.id, g.number, g.business_date, g.supplier_invoice_date, g.invoice_total,
           sum(g.invoice_total) OVER w, 'Goods receipt ' || g.number || ', invoice ' || g.supplier_invoice_number,
           g.received_by_user_id, g.posted_at_utc
      FROM grns g
     WHERE g.status = 'POSTED' AND g.invoice_total > 0
    WINDOW w AS (PARTITION BY g.supplier_id ORDER BY g.posted_at_utc, g.id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)
     ORDER BY g.supplier_id, g.posted_at_utc, g.id;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE FUNCTION sb_supplier_ledger_chain() RETURNS trigger
    LANGUAGE plpgsql AS $$
    DECLARE
        previous_sequence bigint;
        previous_balance numeric;
    BEGIN
        SELECT sequence, balance_after INTO previous_sequence, previous_balance
          FROM supplier_ledger WHERE supplier_id = NEW.supplier_id ORDER BY sequence DESC LIMIT 1;
        IF NEW.sequence <> coalesce(previous_sequence, 0) + 1 OR NEW.balance_after <> coalesce(previous_balance, 0) + NEW.amount THEN
            RAISE EXCEPTION 'Account entry % does not follow the previous entry of the account.', NEW.sequence USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_supplier_ledger_chain BEFORE INSERT ON supplier_ledger FOR EACH ROW EXECUTE FUNCTION sb_supplier_ledger_chain();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE FUNCTION sb_debtor_ledger_chain() RETURNS trigger
    LANGUAGE plpgsql AS $$
    DECLARE
        previous_sequence bigint;
        previous_balance numeric;
    BEGIN
        SELECT sequence, balance_after INTO previous_sequence, previous_balance
          FROM debtor_ledger WHERE debtor_id = NEW.debtor_id ORDER BY sequence DESC LIMIT 1;
        IF NEW.sequence <> coalesce(previous_sequence, 0) + 1 OR NEW.balance_after <> coalesce(previous_balance, 0) + NEW.amount THEN
            RAISE EXCEPTION 'Account entry % does not follow the previous entry of the account.', NEW.sequence USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_debtor_ledger_chain BEFORE INSERT ON debtor_ledger FOR EACH ROW EXECUTE FUNCTION sb_debtor_ledger_chain();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE FUNCTION sb_supplier_settlements_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    DECLARE
        charge numeric;
        payment numeric;
    BEGIN
        SELECT amount INTO charge FROM supplier_ledger WHERE id = NEW.charge_entry_id FOR UPDATE;
        SELECT amount INTO payment FROM supplier_ledger WHERE id = NEW.payment_entry_id FOR UPDATE;
        IF charge IS NULL OR charge <= 0 OR payment IS NULL OR payment >= 0 THEN
            RAISE EXCEPTION 'A settlement applies a payment to a charge.' USING ERRCODE = 'check_violation';
        END IF;
        IF (SELECT coalesce(sum(amount), 0) FROM supplier_settlements WHERE charge_entry_id = NEW.charge_entry_id) + NEW.amount > charge
           OR (SELECT coalesce(sum(amount), 0) FROM supplier_settlements WHERE payment_entry_id = NEW.payment_entry_id) + NEW.amount > -payment THEN
            RAISE EXCEPTION 'A settlement cannot exceed what is left of the charge or the payment.' USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_supplier_settlements_guard BEFORE INSERT ON supplier_settlements FOR EACH ROW EXECUTE FUNCTION sb_supplier_settlements_guard();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE FUNCTION sb_debtor_settlements_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    DECLARE
        charge numeric;
        payment numeric;
    BEGIN
        SELECT amount INTO charge FROM debtor_ledger WHERE id = NEW.charge_entry_id FOR UPDATE;
        SELECT amount INTO payment FROM debtor_ledger WHERE id = NEW.payment_entry_id FOR UPDATE;
        IF charge IS NULL OR charge <= 0 OR payment IS NULL OR payment >= 0 THEN
            RAISE EXCEPTION 'A settlement applies a payment to a charge.' USING ERRCODE = 'check_violation';
        END IF;
        IF (SELECT coalesce(sum(amount), 0) FROM debtor_settlements WHERE charge_entry_id = NEW.charge_entry_id) + NEW.amount > charge
           OR (SELECT coalesce(sum(amount), 0) FROM debtor_settlements WHERE payment_entry_id = NEW.payment_entry_id) + NEW.amount > -payment THEN
            RAISE EXCEPTION 'A settlement cannot exceed what is left of the charge or the payment.' USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_debtor_settlements_guard BEFORE INSERT ON debtor_settlements FOR EACH ROW EXECUTE FUNCTION sb_debtor_settlements_guard();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE TRIGGER trg_supplier_ledger_no_update_delete
        BEFORE UPDATE OR DELETE ON supplier_ledger
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_supplier_ledger_no_truncate
        BEFORE TRUNCATE ON supplier_ledger
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE TRIGGER trg_debtor_ledger_no_update_delete
        BEFORE UPDATE OR DELETE ON debtor_ledger
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_debtor_ledger_no_truncate
        BEFORE TRUNCATE ON debtor_ledger
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE TRIGGER trg_supplier_settlements_no_update_delete
        BEFORE UPDATE OR DELETE ON supplier_settlements
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_supplier_settlements_no_truncate
        BEFORE TRUNCATE ON supplier_settlements
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE TRIGGER trg_debtor_settlements_no_update_delete
        BEFORE UPDATE OR DELETE ON debtor_settlements
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_debtor_settlements_no_truncate
        BEFORE TRUNCATE ON debtor_settlements
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE TRIGGER trg_supplier_payments_no_update_delete
        BEFORE UPDATE OR DELETE ON supplier_payments
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_supplier_payments_no_truncate
        BEFORE TRUNCATE ON supplier_payments
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    CREATE FUNCTION sb_debtor_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Debtors cannot be deleted; close the account instead.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF NEW.status = 'CLOSED' AND OLD.status <> 'CLOSED'
           AND (SELECT coalesce(sum(amount), 0) FROM debtor_ledger WHERE debtor_id = NEW.id) <> 0 THEN
            RAISE EXCEPTION 'A debtor account can be closed only at a zero balance.' USING ERRCODE = 'check_violation';
        END IF;
        IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.code) IS DISTINCT FROM (OLD.id, OLD.tenant_id, OLD.business_id, OLD.code) THEN
            RAISE EXCEPTION 'A debtor''s code cannot change.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_debtors_guard BEFORE UPDATE OR DELETE ON debtors FOR EACH ROW EXECUTE FUNCTION sb_debtor_guard();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002090702_Accounts') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261002090702_Accounts', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE TABLE purchase_returns (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        supplier_id uuid NOT NULL,
        grn_id uuid NOT NULL,
        number character varying(40) NOT NULL,
        sequence_number bigint NOT NULL,
        business_date date NOT NULL,
        reason character varying(200) NOT NULL,
        is_inter_state boolean NOT NULL,
        tax_recoverable boolean NOT NULL,
        taxable numeric(18,2) NOT NULL,
        cgst numeric(18,2) NOT NULL,
        sgst numeric(18,2) NOT NULL,
        igst numeric(18,2) NOT NULL,
        cess numeric(18,2) NOT NULL,
        round_off numeric(18,2) NOT NULL,
        total numeric(18,2) NOT NULL,
        stock_value numeric(18,2) NOT NULL,
        created_by_user_id uuid NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        idempotency_key character varying(100) NOT NULL,
        request_hash character varying(64) NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_purchase_returns PRIMARY KEY (id),
        CONSTRAINT ak_purchase_returns_id_business_id UNIQUE (id, business_id),
        CONSTRAINT ck_purchase_returns_amounts CHECK (taxable >= 0 AND cgst >= 0 AND sgst >= 0 AND igst >= 0 AND cess >= 0 AND stock_value >= 0 AND total = taxable + cgst + sgst + igst + cess + round_off),
        CONSTRAINT ck_purchase_returns_tax_kind CHECK ((is_inter_state AND cgst = 0 AND sgst = 0) OR (NOT is_inter_state AND igst = 0)),
        CONSTRAINT fk_purchase_returns_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_purchase_returns_grns_grn_id_business_id FOREIGN KEY (grn_id, business_id) REFERENCES grns (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_purchase_returns_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_purchase_returns_suppliers_supplier_id_business_id FOREIGN KEY (supplier_id, business_id) REFERENCES suppliers (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_purchase_returns_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE TABLE purchase_return_lines (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        purchase_return_id uuid NOT NULL,
        grn_line_id uuid NOT NULL,
        line_number integer NOT NULL,
        variant_id uuid NOT NULL,
        variant_unit_id uuid NOT NULL,
        description character varying(200) NOT NULL,
        unit_code character varying(20) NOT NULL,
        quantity numeric(18,3) NOT NULL,
        base_quantity numeric(18,3) NOT NULL,
        taxable numeric(18,2) NOT NULL,
        cgst numeric(18,2) NOT NULL,
        sgst numeric(18,2) NOT NULL,
        igst numeric(18,2) NOT NULL,
        cess numeric(18,2) NOT NULL,
        total numeric(18,2) NOT NULL,
        stock_value numeric(18,2) NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_purchase_return_lines PRIMARY KEY (id),
        CONSTRAINT ck_purchase_return_lines_amounts CHECK (quantity > 0 AND base_quantity > 0 AND taxable >= 0 AND cgst >= 0 AND sgst >= 0 AND igst >= 0 AND cess >= 0 AND stock_value >= 0 AND total = taxable + cgst + sgst + igst + cess),
        CONSTRAINT fk_purchase_return_lines_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_purchase_return_lines_grn_lines_grn_line_id FOREIGN KEY (grn_line_id) REFERENCES grn_lines (id) ON DELETE RESTRICT,
        CONSTRAINT fk_purchase_return_lines_product_variants_variant_id_business_ FOREIGN KEY (variant_id, business_id) REFERENCES product_variants (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_purchase_return_lines_purchase_returns_purchase_return_id_b FOREIGN KEY (purchase_return_id, business_id) REFERENCES purchase_returns (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_purchase_return_lines_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE INDEX ix_purchase_return_lines_business_id_tenant_id ON purchase_return_lines (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE INDEX ix_purchase_return_lines_grn_line_id ON purchase_return_lines (grn_line_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE INDEX ix_purchase_return_lines_purchase_return_id_business_id ON purchase_return_lines (purchase_return_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE UNIQUE INDEX ix_purchase_return_lines_purchase_return_id_grn_line_id ON purchase_return_lines (purchase_return_id, grn_line_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE UNIQUE INDEX ix_purchase_return_lines_purchase_return_id_line_number ON purchase_return_lines (purchase_return_id, line_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE INDEX ix_purchase_return_lines_tenant_id ON purchase_return_lines (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE INDEX ix_purchase_return_lines_variant_id_business_id ON purchase_return_lines (variant_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE UNIQUE INDEX ix_purchase_returns_business_id_idempotency_key ON purchase_returns (business_id, idempotency_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE INDEX ix_purchase_returns_business_id_tenant_id ON purchase_returns (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE INDEX ix_purchase_returns_grn_id ON purchase_returns (grn_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE INDEX ix_purchase_returns_grn_id_business_id ON purchase_returns (grn_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE INDEX ix_purchase_returns_store_id_business_id ON purchase_returns (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE UNIQUE INDEX ix_purchase_returns_store_id_sequence_number ON purchase_returns (store_id, sequence_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE INDEX ix_purchase_returns_supplier_id_business_date ON purchase_returns (supplier_id, business_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE INDEX ix_purchase_returns_supplier_id_business_id ON purchase_returns (supplier_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE INDEX ix_purchase_returns_tenant_id ON purchase_returns (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    ALTER TABLE purchase_returns ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON purchase_returns
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE purchase_return_lines ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON purchase_return_lines
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE TRIGGER trg_purchase_returns_no_update_delete
        BEFORE UPDATE OR DELETE ON purchase_returns
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_purchase_returns_no_truncate
        BEFORE TRUNCATE ON purchase_returns
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE TRIGGER trg_purchase_return_lines_no_update_delete
        BEFORE UPDATE OR DELETE ON purchase_return_lines
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_purchase_return_lines_no_truncate
        BEFORE TRUNCATE ON purchase_return_lines
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    CREATE FUNCTION sb_purchase_return_line_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    DECLARE
        received numeric;
        line_grn uuid;
    BEGIN
        SELECT quantity + free_quantity, grn_id INTO received, line_grn FROM grn_lines WHERE id = NEW.grn_line_id FOR UPDATE;
        IF line_grn IS DISTINCT FROM (SELECT grn_id FROM purchase_returns WHERE id = NEW.purchase_return_id) THEN
            RAISE EXCEPTION 'A return line must be against a line of the receipt the return is for.' USING ERRCODE = 'check_violation';
        END IF;
        IF (SELECT coalesce(sum(quantity), 0) FROM purchase_return_lines WHERE grn_line_id = NEW.grn_line_id) + NEW.quantity > received THEN
            RAISE EXCEPTION 'More would be returned than the receipt line received.' USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_purchase_return_lines_guard BEFORE INSERT ON purchase_return_lines FOR EACH ROW EXECUTE FUNCTION sb_purchase_return_line_guard();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002113841_PurchaseReturns') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261002113841_PurchaseReturns', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    ALTER TABLE supervisor_approvals DROP CONSTRAINT ck_supervisor_approvals_kind;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    ALTER TABLE sales_return_refunds DROP CONSTRAINT ck_sales_return_refunds_method;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    ALTER TABLE sales_invoice_payments DROP CONSTRAINT ck_sales_invoice_payments_method;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    ALTER TABLE sales_invoices ADD credit_approval_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    ALTER TABLE sales_invoices ADD debtor_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    ALTER TABLE sales_invoices ADD due_date date;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    CREATE TABLE debtor_receipts (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        debtor_id uuid NOT NULL,
        number character varying(40) NOT NULL,
        sequence_number bigint NOT NULL,
        receipt_date date NOT NULL,
        method character varying(20) NOT NULL,
        reference character varying(40),
        amount numeric(18,2) NOT NULL,
        note character varying(300) NOT NULL,
        counter_id uuid,
        device_id uuid,
        shift_id uuid,
        cashier_user_id uuid NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        idempotency_key character varying(100) NOT NULL,
        request_hash character varying(64) NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_debtor_receipts PRIMARY KEY (id),
        CONSTRAINT ck_debtor_receipts_amount CHECK (amount > 0),
        CONSTRAINT ck_debtor_receipts_cheque CHECK (method <> 'CHEQUE' OR reference IS NOT NULL),
        CONSTRAINT ck_debtor_receipts_counter CHECK ((shift_id IS NULL) = (counter_id IS NULL) AND (shift_id IS NULL) = (device_id IS NULL)),
        CONSTRAINT ck_debtor_receipts_method CHECK (method IN ('CASH', 'CARD', 'UPI', 'BANK_TRANSFER', 'CHEQUE')),
        CONSTRAINT fk_debtor_receipts_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_debtor_receipts_counters_counter_id FOREIGN KEY (counter_id) REFERENCES counters (id) ON DELETE RESTRICT,
        CONSTRAINT fk_debtor_receipts_debtors_debtor_id_business_id FOREIGN KEY (debtor_id, business_id) REFERENCES debtors (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_debtor_receipts_shifts_shift_id FOREIGN KEY (shift_id) REFERENCES shifts (id) ON DELETE RESTRICT,
        CONSTRAINT fk_debtor_receipts_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_debtor_receipts_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    ALTER TABLE supervisor_approvals ADD CONSTRAINT ck_supervisor_approvals_kind CHECK ((kind = 'PRICE_OVERRIDE' AND variant_unit_id IS NOT NULL AND approved_price >= 0 AND max_amount IS NULL) OR (kind IN ('DISCOUNT', 'RETURN', 'PAY_OUT', 'CREDIT_LIMIT') AND variant_unit_id IS NULL AND approved_price IS NULL AND max_amount > 0));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    ALTER TABLE sales_return_refunds ADD CONSTRAINT ck_sales_return_refunds_method CHECK (method IN ('CASH', 'CARD', 'UPI', 'WALLET', 'STORE_CREDIT', 'ON_ACCOUNT'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    CREATE INDEX ix_sales_invoices_credit_approval_id ON sales_invoices (credit_approval_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    CREATE INDEX ix_sales_invoices_debtor_id_business_date ON sales_invoices (debtor_id, business_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    CREATE INDEX ix_sales_invoices_debtor_id_business_id ON sales_invoices (debtor_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    ALTER TABLE sales_invoices ADD CONSTRAINT ck_sales_invoices_credit CHECK ((due_date IS NULL OR debtor_id IS NOT NULL) AND (credit_approval_id IS NULL OR due_date IS NOT NULL) AND (due_date IS NULL OR due_date >= business_date));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    ALTER TABLE sales_invoice_payments ADD CONSTRAINT ck_sales_invoice_payments_method CHECK (method IN ('CASH', 'CARD', 'UPI', 'WALLET', 'CREDIT_NOTE', 'ON_ACCOUNT'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    CREATE UNIQUE INDEX ix_debtor_receipts_business_id_idempotency_key ON debtor_receipts (business_id, idempotency_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    CREATE INDEX ix_debtor_receipts_business_id_tenant_id ON debtor_receipts (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    CREATE INDEX ix_debtor_receipts_counter_id ON debtor_receipts (counter_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    CREATE INDEX ix_debtor_receipts_debtor_id_business_id ON debtor_receipts (debtor_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    CREATE INDEX ix_debtor_receipts_debtor_id_receipt_date ON debtor_receipts (debtor_id, receipt_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    CREATE INDEX ix_debtor_receipts_shift_id ON debtor_receipts (shift_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    CREATE INDEX ix_debtor_receipts_store_id_business_id ON debtor_receipts (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    CREATE UNIQUE INDEX ix_debtor_receipts_store_id_sequence_number ON debtor_receipts (store_id, sequence_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    CREATE INDEX ix_debtor_receipts_tenant_id ON debtor_receipts (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    ALTER TABLE sales_invoices ADD CONSTRAINT fk_sales_invoices_debtors_debtor_id_business_id FOREIGN KEY (debtor_id, business_id) REFERENCES debtors (id, business_id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    ALTER TABLE sales_invoices ADD CONSTRAINT fk_sales_invoices_supervisor_approvals_credit_approval_id FOREIGN KEY (credit_approval_id) REFERENCES supervisor_approvals (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    ALTER TABLE debtor_receipts ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON debtor_receipts
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    CREATE TRIGGER trg_debtor_receipts_no_update_delete
        BEFORE UPDATE OR DELETE ON debtor_receipts
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_debtor_receipts_no_truncate
        BEFORE TRUNCATE ON debtor_receipts
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    CREATE FUNCTION sb_debtor_receipt_shift() RETURNS trigger
    LANGUAGE plpgsql AS $$
    DECLARE
        s shifts%ROWTYPE;
    BEGIN
        IF NEW.shift_id IS NULL THEN
            RETURN NEW;
        END IF;
        SELECT * INTO s FROM shifts WHERE id = NEW.shift_id;
        IF s.status IS DISTINCT FROM 'OPEN' THEN
            RAISE EXCEPTION 'Shift % is not open.', NEW.shift_id USING ERRCODE = 'restrict_violation';
        END IF;
        IF s.counter_id <> NEW.counter_id OR s.cashier_user_id <> NEW.cashier_user_id THEN
            RAISE EXCEPTION 'The receipt belongs to another counter''s or cashier''s shift.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_debtor_receipts_open_shift BEFORE INSERT ON debtor_receipts FOR EACH ROW EXECUTE FUNCTION sb_debtor_receipt_shift();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002124521_CreditSales') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261002124521_CreditSales', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE TABLE collection_visits (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        debtor_id uuid NOT NULL,
        collector_user_id uuid NOT NULL,
        visit_date date NOT NULL,
        note character varying(300) NOT NULL,
        is_cancelled boolean NOT NULL,
        assigned_by_user_id uuid NOT NULL,
        assigned_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_collection_visits PRIMARY KEY (id),
        CONSTRAINT fk_collection_visits_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_collection_visits_debtors_debtor_id_business_id FOREIGN KEY (debtor_id, business_id) REFERENCES debtors (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_collection_visits_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_collection_visits_users_collector_user_id FOREIGN KEY (collector_user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE TABLE collector_absences (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        collector_user_id uuid NOT NULL,
        absent_on date NOT NULL,
        reason character varying(200) NOT NULL,
        recorded_by_user_id uuid NOT NULL,
        recorded_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_collector_absences PRIMARY KEY (id),
        CONSTRAINT fk_collector_absences_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_collector_absences_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_collector_absences_users_collector_user_id FOREIGN KEY (collector_user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE TABLE payment_promises (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        debtor_id uuid NOT NULL,
        amount numeric(18,2) NOT NULL,
        promised_date date NOT NULL,
        note character varying(300) NOT NULL,
        is_cancelled boolean NOT NULL,
        recorded_by_user_id uuid NOT NULL,
        recorded_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_payment_promises PRIMARY KEY (id),
        CONSTRAINT ck_payment_promises_amount CHECK (amount > 0),
        CONSTRAINT fk_payment_promises_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_payment_promises_debtors_debtor_id_business_id FOREIGN KEY (debtor_id, business_id) REFERENCES debtors (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_payment_promises_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE TABLE routes (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        code character varying(20) NOT NULL,
        name character varying(100) NOT NULL,
        description character varying(300),
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_routes PRIMARY KEY (id),
        CONSTRAINT ak_routes_id_business_id UNIQUE (id, business_id),
        CONSTRAINT ck_routes_code CHECK (code ~ '^[A-Z0-9-]{1,20}$'),
        CONSTRAINT fk_routes_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_routes_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE TABLE collection_plans (
        debtor_id uuid NOT NULL,
        business_id uuid NOT NULL,
        route_id uuid,
        visit_sequence integer,
        primary_collector_user_id uuid,
        backup_collector_user_id uuid,
        preferred_from time without time zone,
        preferred_to time without time zone,
        schedule_type character varying(20) NOT NULL,
        weekday_mask integer NOT NULL,
        anchor_date date,
        month_day integer,
        due_offset_days integer,
        updated_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_collection_plans PRIMARY KEY (debtor_id),
        CONSTRAINT ck_collection_plans_backup CHECK (backup_collector_user_id IS NULL OR backup_collector_user_id <> primary_collector_user_id),
        CONSTRAINT ck_collection_plans_schedule CHECK (schedule_type IN ('MANUAL', 'WEEKDAYS', 'FORTNIGHTLY', 'MONTHLY', 'DUE_DATE', 'SPECIFIC_DATE')),
        CONSTRAINT ck_collection_plans_values CHECK (weekday_mask BETWEEN 0 AND 127 AND (month_day IS NULL OR month_day BETWEEN 1 AND 31) AND (due_offset_days IS NULL OR due_offset_days BETWEEN -60 AND 60) AND (visit_sequence IS NULL OR visit_sequence BETWEEN 1 AND 9999)),
        CONSTRAINT fk_collection_plans_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_collection_plans_debtors_debtor_id_business_id FOREIGN KEY (debtor_id, business_id) REFERENCES debtors (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_collection_plans_routes_route_id_business_id FOREIGN KEY (route_id, business_id) REFERENCES routes (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_collection_plans_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_collection_plans_users_backup_collector_user_id FOREIGN KEY (backup_collector_user_id) REFERENCES users (id) ON DELETE RESTRICT,
        CONSTRAINT fk_collection_plans_users_primary_collector_user_id FOREIGN KEY (primary_collector_user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_collection_plans_backup_collector_user_id ON collection_plans (backup_collector_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_collection_plans_business_id_tenant_id ON collection_plans (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE UNIQUE INDEX ix_collection_plans_debtor_id_business_id ON collection_plans (debtor_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_collection_plans_primary_collector_user_id ON collection_plans (primary_collector_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_collection_plans_route_id_business_id ON collection_plans (route_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_collection_plans_route_id_visit_sequence ON collection_plans (route_id, visit_sequence);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_collection_plans_tenant_id ON collection_plans (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_collection_visits_business_id_tenant_id ON collection_visits (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_collection_visits_collector_user_id_visit_date ON collection_visits (collector_user_id, visit_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_collection_visits_debtor_id ON collection_visits (debtor_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_collection_visits_debtor_id_business_id ON collection_visits (debtor_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_collection_visits_tenant_id ON collection_visits (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE UNIQUE INDEX ix_collector_absences_business_id_collector_user_id_absent_on ON collector_absences (business_id, collector_user_id, absent_on);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_collector_absences_business_id_tenant_id ON collector_absences (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_collector_absences_collector_user_id ON collector_absences (collector_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_collector_absences_tenant_id ON collector_absences (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_payment_promises_business_id_tenant_id ON payment_promises (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_payment_promises_debtor_id_business_id ON payment_promises (debtor_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_payment_promises_debtor_id_promised_date ON payment_promises (debtor_id, promised_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_payment_promises_tenant_id ON payment_promises (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE UNIQUE INDEX ix_routes_business_id_code ON routes (business_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_routes_business_id_tenant_id ON routes (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE INDEX ix_routes_tenant_id ON routes (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    ALTER TABLE routes ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON routes
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE collection_plans ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON collection_plans
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE collection_visits ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON collection_visits
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE payment_promises ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON payment_promises
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE collector_absences ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON collector_absences
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE FUNCTION sb_collection_visits_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION '% rows cannot be deleted; cancel them instead.', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
        END IF;
        IF OLD.is_cancelled OR NOT NEW.is_cancelled
           OR (to_jsonb(NEW) - 'is_cancelled') IS DISTINCT FROM (to_jsonb(OLD) - 'is_cancelled') THEN
            RAISE EXCEPTION '% rows can only be cancelled, once.', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_collection_visits_guard BEFORE UPDATE OR DELETE ON collection_visits FOR EACH ROW EXECUTE FUNCTION sb_collection_visits_guard();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    CREATE FUNCTION sb_payment_promises_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION '% rows cannot be deleted; cancel them instead.', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
        END IF;
        IF OLD.is_cancelled OR NOT NEW.is_cancelled
           OR (to_jsonb(NEW) - 'is_cancelled') IS DISTINCT FROM (to_jsonb(OLD) - 'is_cancelled') THEN
            RAISE EXCEPTION '% rows can only be cancelled, once.', TG_TABLE_NAME USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_payment_promises_guard BEFORE UPDATE OR DELETE ON payment_promises FOR EACH ROW EXECUTE FUNCTION sb_payment_promises_guard();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261002132054_Collections') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261002132054_Collections', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE supplier_settlements DROP CONSTRAINT ck_supplier_settlements_amount;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE supplier_ledger DROP CONSTRAINT ck_supplier_ledger_sign;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE debtor_settlements DROP CONSTRAINT ck_debtor_settlements_amount;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE debtor_receipts DROP CONSTRAINT ck_debtor_receipts_cheque;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE debtor_receipts DROP CONSTRAINT ck_debtor_receipts_counter;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE debtor_receipts DROP CONSTRAINT ck_debtor_receipts_method;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE debtor_ledger DROP CONSTRAINT ck_debtor_ledger_sign;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE debtor_ledger DROP CONSTRAINT ck_debtor_ledger_type;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE debtor_receipts ADD collector_session_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE TABLE cheques (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        receipt_id uuid NOT NULL,
        debtor_id uuid NOT NULL,
        kind character varying(20) NOT NULL,
        number character varying(40) NOT NULL,
        bank_name character varying(100),
        cheque_date date,
        amount numeric(18,2) NOT NULL,
        status character varying(12) NOT NULL,
        replaced_by_receipt_id uuid,
        received_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_cheques PRIMARY KEY (id),
        CONSTRAINT ck_cheques_kind CHECK (kind IN ('CHEQUE', 'DEMAND_DRAFT') AND amount > 0),
        CONSTRAINT ck_cheques_replaced CHECK ((status = 'REPLACED') = (replaced_by_receipt_id IS NOT NULL)),
        CONSTRAINT ck_cheques_status CHECK (status IN ('RECEIVED', 'DEPOSITED', 'CLEARED', 'BOUNCED', 'CANCELLED', 'REPLACED')),
        CONSTRAINT fk_cheques_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_cheques_debtor_receipts_receipt_id FOREIGN KEY (receipt_id) REFERENCES debtor_receipts (id) ON DELETE RESTRICT,
        CONSTRAINT fk_cheques_debtor_receipts_replaced_by_receipt_id FOREIGN KEY (replaced_by_receipt_id) REFERENCES debtor_receipts (id) ON DELETE RESTRICT,
        CONSTRAINT fk_cheques_debtors_debtor_id_business_id FOREIGN KEY (debtor_id, business_id) REFERENCES debtors (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_cheques_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE TABLE collector_sessions (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        collector_user_id uuid NOT NULL,
        business_date date NOT NULL,
        status character varying(12) NOT NULL,
        opened_at_utc timestamp with time zone NOT NULL,
        expected_cash numeric(18,2),
        declared_cash numeric(18,2),
        handed_over_at_utc timestamp with time zone,
        received_by_user_id uuid,
        counted_cash numeric(18,2),
        variance numeric(18,2),
        note character varying(300),
        confirmed_at_utc timestamp with time zone,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_collector_sessions PRIMARY KEY (id),
        CONSTRAINT ck_collector_sessions_status CHECK (status IN ('OPEN', 'HANDED_OVER', 'CONFIRMED')),
        CONSTRAINT ck_collector_sessions_steps CHECK ((status = 'OPEN') = (handed_over_at_utc IS NULL) AND (status = 'CONFIRMED') = (confirmed_at_utc IS NOT NULL) AND (status = 'OPEN' OR (expected_cash IS NOT NULL AND declared_cash IS NOT NULL)) AND (status <> 'CONFIRMED' OR (counted_cash IS NOT NULL AND variance = counted_cash - expected_cash AND received_by_user_id <> collector_user_id))),
        CONSTRAINT fk_collector_sessions_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_collector_sessions_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_collector_sessions_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_collector_sessions_users_collector_user_id FOREIGN KEY (collector_user_id) REFERENCES users (id) ON DELETE RESTRICT,
        CONSTRAINT fk_collector_sessions_users_received_by_user_id FOREIGN KEY (received_by_user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE TABLE receipt_reversals (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        receipt_id uuid NOT NULL,
        kind character varying(12) NOT NULL,
        reason character varying(300) NOT NULL,
        approval_request_id uuid,
        reversed_by_user_id uuid NOT NULL,
        reversed_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_receipt_reversals PRIMARY KEY (id),
        CONSTRAINT ck_receipt_reversals_kind CHECK (kind IN ('BOUNCED', 'CANCELLED', 'CORRECTION')),
        CONSTRAINT fk_receipt_reversals_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_receipt_reversals_debtor_receipts_receipt_id FOREIGN KEY (receipt_id) REFERENCES debtor_receipts (id) ON DELETE RESTRICT,
        CONSTRAINT fk_receipt_reversals_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE TABLE visit_outcomes (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        debtor_id uuid NOT NULL,
        collector_user_id uuid NOT NULL,
        visit_date date NOT NULL,
        outcome character varying(20) NOT NULL,
        note character varying(300) NOT NULL,
        recorded_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_visit_outcomes PRIMARY KEY (id),
        CONSTRAINT ck_visit_outcomes_outcome CHECK (outcome IN ('NO_PAYMENT', 'NOT_AVAILABLE', 'SHOP_CLOSED', 'DISPUTED')),
        CONSTRAINT fk_visit_outcomes_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_visit_outcomes_debtors_debtor_id_business_id FOREIGN KEY (debtor_id, business_id) REFERENCES debtors (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_visit_outcomes_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE TABLE cheque_events (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        cheque_id uuid NOT NULL,
        status character varying(12) NOT NULL,
        event_date date NOT NULL,
        note character varying(300),
        recorded_by_user_id uuid NOT NULL,
        recorded_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_cheque_events PRIMARY KEY (id),
        CONSTRAINT ck_cheque_events_status CHECK (status IN ('RECEIVED', 'DEPOSITED', 'CLEARED', 'BOUNCED', 'CANCELLED', 'REPLACED')),
        CONSTRAINT fk_cheque_events_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_cheque_events_cheques_cheque_id FOREIGN KEY (cheque_id) REFERENCES cheques (id) ON DELETE RESTRICT,
        CONSTRAINT fk_cheque_events_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE TABLE collector_session_counts (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        session_id uuid NOT NULL,
        kind character varying(10) NOT NULL,
        denomination numeric(10,2) NOT NULL,
        count integer NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_collector_session_counts PRIMARY KEY (id),
        CONSTRAINT ck_collector_session_counts CHECK (kind IN ('DECLARED', 'COUNTED') AND count > 0 AND denomination > 0),
        CONSTRAINT fk_collector_session_counts_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_collector_session_counts_collector_sessions_session_id FOREIGN KEY (session_id) REFERENCES collector_sessions (id) ON DELETE RESTRICT,
        CONSTRAINT fk_collector_session_counts_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE supplier_settlements ADD CONSTRAINT ck_supplier_settlements_amount CHECK (amount <> 0 AND charge_entry_id <> payment_entry_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE supplier_ledger ADD CONSTRAINT ck_supplier_ledger_sign CHECK ((entry_type NOT IN ('GRN', 'INVOICE', 'RECEIPT_REVERSAL') OR amount > 0) AND (entry_type NOT IN ('PAYMENT', 'DEBIT_NOTE', 'RECEIPT', 'CREDIT_NOTE') OR amount < 0));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE debtor_settlements ADD CONSTRAINT ck_debtor_settlements_amount CHECK (amount <> 0 AND charge_entry_id <> payment_entry_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_debtor_receipts_collector_session_id ON debtor_receipts (collector_session_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE debtor_receipts ADD CONSTRAINT ck_debtor_receipts_cheque CHECK (method NOT IN ('CHEQUE', 'DEMAND_DRAFT', 'OTHER') OR reference IS NOT NULL);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE debtor_receipts ADD CONSTRAINT ck_debtor_receipts_counter CHECK ((shift_id IS NULL) = (counter_id IS NULL) AND (shift_id IS NULL) = (device_id IS NULL) AND (shift_id IS NULL OR collector_session_id IS NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE debtor_receipts ADD CONSTRAINT ck_debtor_receipts_method CHECK (method IN ('CASH', 'CARD', 'UPI', 'BANK_TRANSFER', 'CHEQUE', 'DEMAND_DRAFT', 'OTHER'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE debtor_ledger ADD CONSTRAINT ck_debtor_ledger_sign CHECK ((entry_type NOT IN ('GRN', 'INVOICE', 'RECEIPT_REVERSAL') OR amount > 0) AND (entry_type NOT IN ('PAYMENT', 'DEBIT_NOTE', 'RECEIPT', 'CREDIT_NOTE') OR amount < 0));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE debtor_ledger ADD CONSTRAINT ck_debtor_ledger_type CHECK (entry_type IN ('OPENING', 'INVOICE', 'RECEIPT', 'CREDIT_NOTE', 'ADJUSTMENT', 'RECEIPT_REVERSAL'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_cheque_events_business_id_tenant_id ON cheque_events (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_cheque_events_cheque_id ON cheque_events (cheque_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_cheque_events_tenant_id ON cheque_events (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_cheques_business_id_status ON cheques (business_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_cheques_business_id_tenant_id ON cheques (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_cheques_debtor_id ON cheques (debtor_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_cheques_debtor_id_business_id ON cheques (debtor_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE UNIQUE INDEX ix_cheques_receipt_id ON cheques (receipt_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_cheques_replaced_by_receipt_id ON cheques (replaced_by_receipt_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_cheques_tenant_id ON cheques (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_collector_session_counts_business_id_tenant_id ON collector_session_counts (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE UNIQUE INDEX ix_collector_session_counts_session_id_kind_denomination ON collector_session_counts (session_id, kind, denomination);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_collector_session_counts_tenant_id ON collector_session_counts (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE UNIQUE INDEX ix_collector_sessions_business_id_collector_user_id ON collector_sessions (business_id, collector_user_id) WHERE status = 'OPEN';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_collector_sessions_business_id_tenant_id ON collector_sessions (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_collector_sessions_collector_user_id ON collector_sessions (collector_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_collector_sessions_received_by_user_id ON collector_sessions (received_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_collector_sessions_store_id_business_id ON collector_sessions (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_collector_sessions_store_id_status ON collector_sessions (store_id, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_collector_sessions_tenant_id ON collector_sessions (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_receipt_reversals_business_id_tenant_id ON receipt_reversals (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE UNIQUE INDEX ix_receipt_reversals_receipt_id ON receipt_reversals (receipt_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_receipt_reversals_tenant_id ON receipt_reversals (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_visit_outcomes_business_id_tenant_id ON visit_outcomes (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_visit_outcomes_debtor_id_business_id ON visit_outcomes (debtor_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_visit_outcomes_debtor_id_visit_date ON visit_outcomes (debtor_id, visit_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE INDEX ix_visit_outcomes_tenant_id ON visit_outcomes (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE debtor_receipts ADD CONSTRAINT fk_debtor_receipts_collector_sessions_collector_session_id FOREIGN KEY (collector_session_id) REFERENCES collector_sessions (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    ALTER TABLE collector_sessions ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON collector_sessions
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE collector_session_counts ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON collector_session_counts
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE cheques ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON cheques
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE cheque_events ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON cheque_events
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE receipt_reversals ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON receipt_reversals
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE visit_outcomes ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON visit_outcomes
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE TRIGGER trg_collector_session_counts_no_update_delete
        BEFORE UPDATE OR DELETE ON collector_session_counts
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_collector_session_counts_no_truncate
        BEFORE TRUNCATE ON collector_session_counts
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE TRIGGER trg_cheque_events_no_update_delete
        BEFORE UPDATE OR DELETE ON cheque_events
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_cheque_events_no_truncate
        BEFORE TRUNCATE ON cheque_events
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE TRIGGER trg_receipt_reversals_no_update_delete
        BEFORE UPDATE OR DELETE ON receipt_reversals
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_receipt_reversals_no_truncate
        BEFORE TRUNCATE ON receipt_reversals
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE TRIGGER trg_visit_outcomes_no_update_delete
        BEFORE UPDATE OR DELETE ON visit_outcomes
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_visit_outcomes_no_truncate
        BEFORE TRUNCATE ON visit_outcomes
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE OR REPLACE FUNCTION sb_debtor_settlements_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    DECLARE
        charge numeric;
        payment numeric;
    BEGIN
        SELECT amount INTO charge FROM debtor_ledger WHERE id = NEW.charge_entry_id FOR UPDATE;
        SELECT amount INTO payment FROM debtor_ledger WHERE id = NEW.payment_entry_id FOR UPDATE;
        IF charge IS NULL OR charge <= 0 OR payment IS NULL OR payment >= 0 THEN
            RAISE EXCEPTION 'A settlement applies a payment to a charge.' USING ERRCODE = 'check_violation';
        END IF;
        IF NEW.amount < 0 THEN
            IF (SELECT coalesce(sum(amount), 0) FROM debtor_settlements WHERE charge_entry_id = NEW.charge_entry_id AND payment_entry_id = NEW.payment_entry_id) + NEW.amount < 0 THEN
                RAISE EXCEPTION 'A settlement can only be taken back as far as it was made.' USING ERRCODE = 'check_violation';
            END IF;
            RETURN NEW;
        END IF;
        IF (SELECT coalesce(sum(amount), 0) FROM debtor_settlements WHERE charge_entry_id = NEW.charge_entry_id) + NEW.amount > charge
           OR (SELECT coalesce(sum(amount), 0) FROM debtor_settlements WHERE payment_entry_id = NEW.payment_entry_id) + NEW.amount > -payment THEN
            RAISE EXCEPTION 'A settlement cannot exceed what is left of the charge or the payment.' USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE OR REPLACE FUNCTION sb_supplier_settlements_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    DECLARE
        charge numeric;
        payment numeric;
    BEGIN
        SELECT amount INTO charge FROM supplier_ledger WHERE id = NEW.charge_entry_id FOR UPDATE;
        SELECT amount INTO payment FROM supplier_ledger WHERE id = NEW.payment_entry_id FOR UPDATE;
        IF charge IS NULL OR charge <= 0 OR payment IS NULL OR payment >= 0 THEN
            RAISE EXCEPTION 'A settlement applies a payment to a charge.' USING ERRCODE = 'check_violation';
        END IF;
        IF NEW.amount < 0 THEN
            IF (SELECT coalesce(sum(amount), 0) FROM supplier_settlements WHERE charge_entry_id = NEW.charge_entry_id AND payment_entry_id = NEW.payment_entry_id) + NEW.amount < 0 THEN
                RAISE EXCEPTION 'A settlement can only be taken back as far as it was made.' USING ERRCODE = 'check_violation';
            END IF;
            RETURN NEW;
        END IF;
        IF (SELECT coalesce(sum(amount), 0) FROM supplier_settlements WHERE charge_entry_id = NEW.charge_entry_id) + NEW.amount > charge
           OR (SELECT coalesce(sum(amount), 0) FROM supplier_settlements WHERE payment_entry_id = NEW.payment_entry_id) + NEW.amount > -payment THEN
            RAISE EXCEPTION 'A settlement cannot exceed what is left of the charge or the payment.' USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE FUNCTION sb_debtor_receipt_round() RETURNS trigger
    LANGUAGE plpgsql AS $$
    DECLARE
        s collector_sessions%ROWTYPE;
    BEGIN
        IF NEW.collector_session_id IS NULL THEN
            RETURN NEW;
        END IF;
        SELECT * INTO s FROM collector_sessions WHERE id = NEW.collector_session_id;
        IF s.status IS DISTINCT FROM 'OPEN' OR s.collector_user_id <> NEW.cashier_user_id OR s.store_id <> NEW.store_id THEN
            RAISE EXCEPTION 'A field receipt must be in the collector''s own open round.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_debtor_receipts_round BEFORE INSERT ON debtor_receipts FOR EACH ROW EXECUTE FUNCTION sb_debtor_receipt_round();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE FUNCTION sb_collector_session_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Collection rounds cannot be deleted.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.store_id, NEW.collector_user_id, NEW.business_date, NEW.opened_at_utc)
           IS DISTINCT FROM (OLD.id, OLD.tenant_id, OLD.business_id, OLD.store_id, OLD.collector_user_id, OLD.business_date, OLD.opened_at_utc)
           OR NOT ((OLD.status = 'OPEN' AND NEW.status = 'HANDED_OVER') OR (OLD.status = 'HANDED_OVER' AND NEW.status = 'CONFIRMED'))
           OR (OLD.status = 'HANDED_OVER' AND (NEW.expected_cash, NEW.declared_cash, NEW.handed_over_at_utc)
                                              IS DISTINCT FROM (OLD.expected_cash, OLD.declared_cash, OLD.handed_over_at_utc)) THEN
            RAISE EXCEPTION 'A collection round can only move forward, once per step.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_collector_sessions_guard BEFORE UPDATE OR DELETE ON collector_sessions FOR EACH ROW EXECUTE FUNCTION sb_collector_session_guard();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    CREATE FUNCTION sb_cheque_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Cheques cannot be deleted.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.receipt_id, NEW.debtor_id, NEW.kind, NEW.number, NEW.bank_name, NEW.cheque_date, NEW.amount, NEW.received_at_utc)
           IS DISTINCT FROM (OLD.id, OLD.tenant_id, OLD.business_id, OLD.receipt_id, OLD.debtor_id, OLD.kind, OLD.number, OLD.bank_name, OLD.cheque_date, OLD.amount, OLD.received_at_utc)
           OR NOT ((OLD.status, NEW.status) IN (('RECEIVED', 'DEPOSITED'), ('RECEIVED', 'CANCELLED'), ('DEPOSITED', 'CLEARED'), ('DEPOSITED', 'BOUNCED'),
                                                 ('BOUNCED', 'REPLACED'), ('CANCELLED', 'REPLACED'))) THEN
            RAISE EXCEPTION 'A cheque can only move from % to an allowed next step.', OLD.status USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_cheques_guard BEFORE UPDATE OR DELETE ON cheques FOR EACH ROW EXECUTE FUNCTION sb_cheque_guard();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004060340_Custody') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261004060340_Custody', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE TABLE message_templates (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        kind character varying(20) NOT NULL,
        channel character varying(10) NOT NULL,
        provider_template_name character varying(100),
        dlt_template_id character varying(30),
        language_code character varying(10) NOT NULL,
        body character varying(1000) NOT NULL,
        is_active boolean NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_message_templates PRIMARY KEY (id),
        CONSTRAINT ck_message_templates_kind CHECK (kind IN ('CREDIT_INVOICE', 'RECEIPT') AND channel IN ('WHATSAPP', 'SMS')),
        CONSTRAINT fk_message_templates_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_message_templates_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE TABLE messaging_settings (
        business_id uuid NOT NULL,
        whatsapp_enabled boolean NOT NULL,
        sms_enabled boolean NOT NULL,
        send_invoices boolean NOT NULL,
        send_receipts boolean NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_messaging_settings PRIMARY KEY (business_id),
        CONSTRAINT fk_messaging_settings_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_messaging_settings_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE TABLE outbound_messages (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        debtor_id uuid NOT NULL,
        channel character varying(10) NOT NULL,
        kind character varying(20) NOT NULL,
        document_id uuid NOT NULL,
        document_number character varying(40) NOT NULL,
        to_number character varying(13),
        parameters_json jsonb NOT NULL,
        body character varying(1000) NOT NULL,
        status character varying(10) NOT NULL,
        skip_reason character varying(100),
        attempts integer NOT NULL,
        next_attempt_at_utc timestamp with time zone,
        last_error character varying(500),
        provider_message_id character varying(200),
        attachment_sha256 character varying(64),
        created_at_utc timestamp with time zone NOT NULL,
        sent_at_utc timestamp with time zone,
        delivered_at_utc timestamp with time zone,
        read_at_utc timestamp with time zone,
        failed_at_utc timestamp with time zone,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_outbound_messages PRIMARY KEY (id),
        CONSTRAINT ck_outbound_messages_kind CHECK (kind IN ('CREDIT_INVOICE', 'RECEIPT') AND channel IN ('WHATSAPP', 'SMS')),
        CONSTRAINT ck_outbound_messages_status CHECK (status IN ('QUEUED', 'SENT', 'DELIVERED', 'READ', 'FAILED', 'SKIPPED')),
        CONSTRAINT ck_outbound_messages_steps CHECK ((status = 'SKIPPED') = (skip_reason IS NOT NULL) AND (status <> 'QUEUED' OR next_attempt_at_utc IS NOT NULL) AND (status NOT IN ('SENT', 'DELIVERED', 'READ') OR (provider_message_id IS NOT NULL AND sent_at_utc IS NOT NULL)) AND attempts BETWEEN 0 AND 5),
        CONSTRAINT fk_outbound_messages_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_outbound_messages_debtors_debtor_id_business_id FOREIGN KEY (debtor_id, business_id) REFERENCES debtors (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_outbound_messages_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE TABLE provider_message_refs (
        provider_message_id character varying(200) NOT NULL,
        tenant_id uuid NOT NULL,
        message_id uuid NOT NULL,
        CONSTRAINT pk_provider_message_refs PRIMARY KEY (provider_message_id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE TABLE message_events (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        message_id uuid NOT NULL,
        status character varying(20) NOT NULL,
        detail character varying(500),
        at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_message_events PRIMARY KEY (id),
        CONSTRAINT fk_message_events_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_message_events_outbound_messages_message_id FOREIGN KEY (message_id) REFERENCES outbound_messages (id) ON DELETE RESTRICT,
        CONSTRAINT fk_message_events_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE INDEX ix_message_events_business_id_tenant_id ON message_events (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE INDEX ix_message_events_message_id ON message_events (message_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE INDEX ix_message_events_tenant_id ON message_events (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE UNIQUE INDEX ix_message_templates_business_id_kind_channel ON message_templates (business_id, kind, channel);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE INDEX ix_message_templates_business_id_tenant_id ON message_templates (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE INDEX ix_message_templates_tenant_id ON message_templates (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE INDEX ix_messaging_settings_business_id_tenant_id ON messaging_settings (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE INDEX ix_messaging_settings_tenant_id ON messaging_settings (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE UNIQUE INDEX ix_outbound_messages_business_id_kind_channel_document_id ON outbound_messages (business_id, kind, channel, document_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE INDEX ix_outbound_messages_business_id_tenant_id ON outbound_messages (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE INDEX ix_outbound_messages_debtor_id ON outbound_messages (debtor_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE INDEX ix_outbound_messages_debtor_id_business_id ON outbound_messages (debtor_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE INDEX ix_outbound_messages_provider_message_id ON outbound_messages (provider_message_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE INDEX ix_outbound_messages_status_next_attempt_at_utc ON outbound_messages (status, next_attempt_at_utc);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE INDEX ix_outbound_messages_tenant_id ON outbound_messages (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    ALTER TABLE message_templates ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON message_templates
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE outbound_messages ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON outbound_messages
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE message_events ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON message_events
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE messaging_settings ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON messaging_settings
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE provider_message_refs ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON provider_message_refs
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE TRIGGER trg_message_events_no_update_delete
        BEFORE UPDATE OR DELETE ON message_events
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_message_events_no_truncate
        BEFORE TRUNCATE ON message_events
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE FUNCTION sb_outbound_message_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Messages cannot be deleted.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.debtor_id, NEW.channel, NEW.kind, NEW.document_id, NEW.document_number, NEW.to_number,
            NEW.parameters_json, NEW.body, NEW.skip_reason, NEW.created_at_utc)
           IS DISTINCT FROM
           (OLD.id, OLD.tenant_id, OLD.business_id, OLD.debtor_id, OLD.channel, OLD.kind, OLD.document_id, OLD.document_number, OLD.to_number,
            OLD.parameters_json, OLD.body, OLD.skip_reason, OLD.created_at_utc)
           OR (OLD.provider_message_id IS NOT NULL AND NEW.provider_message_id IS DISTINCT FROM OLD.provider_message_id)
           OR NOT ((OLD.status, NEW.status) IN (('QUEUED', 'QUEUED'), ('QUEUED', 'SENT'), ('QUEUED', 'FAILED'), ('SENT', 'DELIVERED'), ('SENT', 'READ'),
                                                 ('SENT', 'FAILED'), ('DELIVERED', 'READ'), ('DELIVERED', 'FAILED'))
                   OR (OLD.status = 'FAILED' AND NEW.status = 'QUEUED' AND OLD.provider_message_id IS NULL)) THEN
            RAISE EXCEPTION 'A message can only move forward (from %).', OLD.status USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_outbound_messages_guard BEFORE UPDATE OR DELETE ON outbound_messages FOR EACH ROW EXECUTE FUNCTION sb_outbound_message_guard();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE FUNCTION sb_active_tenants() RETURNS SETOF uuid
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public
        AS $$ SELECT id FROM public.tenants WHERE is_active $$;
    REVOKE ALL ON FUNCTION sb_active_tenants() FROM PUBLIC;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    CREATE FUNCTION sb_provider_message_ref(p_provider_message_id text) RETURNS TABLE (tenant_id uuid, message_id uuid)
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public
        AS $$ SELECT r.tenant_id, r.message_id FROM public.provider_message_refs r WHERE r.provider_message_id = p_provider_message_id $$;
    REVOKE ALL ON FUNCTION sb_provider_message_ref(text) FROM PUBLIC;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004064920_Messaging') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261004064920_Messaging', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE TABLE transporters (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        code character varying(20) NOT NULL,
        name character varying(100) NOT NULL,
        gstin character(15),
        phone character varying(20),
        address character varying(300),
        is_active boolean NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_transporters PRIMARY KEY (id),
        CONSTRAINT ak_transporters_id_business_id UNIQUE (id, business_id),
        CONSTRAINT ck_transporters_code CHECK (code ~ '^[A-Z0-9-]{1,20}$'),
        CONSTRAINT fk_transporters_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_transporters_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE TABLE transporter_branches (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        transporter_id uuid NOT NULL,
        name character varying(100) NOT NULL,
        city character varying(60) NOT NULL,
        address character varying(300),
        phone character varying(20),
        is_booking_office boolean NOT NULL,
        is_destination boolean NOT NULL,
        is_active boolean NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_transporter_branches PRIMARY KEY (id),
        CONSTRAINT ak_transporter_branches_id_transporter_id_business_id UNIQUE (id, transporter_id, business_id),
        CONSTRAINT ck_transporter_branches_role CHECK (is_booking_office OR is_destination),
        CONSTRAINT fk_transporter_branches_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_transporter_branches_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_transporter_branches_transporters_transporter_id_business_id FOREIGN KEY (transporter_id, business_id) REFERENCES transporters (id, business_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE TABLE consignments (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        number character varying(40) NOT NULL,
        mode character varying(20) NOT NULL,
        party_name character varying(200) NOT NULL,
        delivery_address character varying(500) NOT NULL,
        transporter_id uuid,
        transporter_name character varying(100),
        transporter_gstin character(15),
        booking_branch_id uuid,
        booking_office character varying(170),
        destination_branch_id uuid,
        destination_branch character varying(170),
        vehicle_number character varying(12),
        driver_name character varying(100),
        driver_phone character varying(20),
        lr_number character varying(30),
        lr_date date,
        package_count integer NOT NULL,
        weight_kg numeric(18,3),
        freight_terms character varying(10),
        freight_amount numeric(18,2) NOT NULL,
        dispatch_date date NOT NULL,
        expected_delivery_date date,
        eway_bill_number character varying(12),
        goods_value numeric(18,2) NOT NULL,
        status character varying(20) NOT NULL,
        cancel_reason character varying(300),
        cancelled_by_user_id uuid,
        cancelled_at_utc timestamp with time zone,
        created_by_user_id uuid NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        idempotency_key character varying(100) NOT NULL,
        request_hash character varying(64) NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_consignments PRIMARY KEY (id),
        CONSTRAINT ak_consignments_id_business_id UNIQUE (id, business_id),
        CONSTRAINT ck_consignments_cancel CHECK ((status = 'CANCELLED') = (cancel_reason IS NOT NULL AND cancelled_by_user_id IS NOT NULL AND cancelled_at_utc IS NOT NULL)),
        CONSTRAINT ck_consignments_lorry CHECK ((mode = 'LORRY') = (transporter_id IS NOT NULL) AND (mode <> 'LORRY' OR (booking_branch_id IS NOT NULL AND destination_branch_id IS NOT NULL AND lr_number IS NOT NULL AND lr_date IS NOT NULL AND lr_date <= dispatch_date AND freight_terms IN ('PAID', 'TO_PAY'))) AND (mode = 'LORRY' OR (lr_number IS NULL AND freight_terms IS NULL AND freight_amount = 0))),
        CONSTRAINT ck_consignments_mode CHECK (mode IN ('OWN_VEHICLE', 'LORRY', 'LOCAL_DELIVERY') AND status IN ('DISPATCHED', 'CANCELLED')),
        CONSTRAINT ck_consignments_trip CHECK ((mode <> 'OWN_VEHICLE' OR vehicle_number IS NOT NULL) AND (mode <> 'LOCAL_DELIVERY' OR driver_name IS NOT NULL)),
        CONSTRAINT ck_consignments_values CHECK (package_count BETWEEN 1 AND 9999 AND (weight_kg IS NULL OR weight_kg > 0) AND freight_amount >= 0 AND goods_value >= 0 AND (expected_delivery_date IS NULL OR expected_delivery_date >= dispatch_date) AND (eway_bill_number IS NULL OR eway_bill_number ~ '^[0-9]{12}$')),
        CONSTRAINT fk_consignments_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_consignments_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_consignments_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_consignments_transporter_branches_booking_branch_id_transpo FOREIGN KEY (booking_branch_id, transporter_id, business_id) REFERENCES transporter_branches (id, transporter_id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_consignments_transporter_branches_destination_branch_id_tra FOREIGN KEY (destination_branch_id, transporter_id, business_id) REFERENCES transporter_branches (id, transporter_id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_consignments_transporters_transporter_id_business_id FOREIGN KEY (transporter_id, business_id) REFERENCES transporters (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_consignments_users_cancelled_by_user_id FOREIGN KEY (cancelled_by_user_id) REFERENCES users (id) ON DELETE RESTRICT,
        CONSTRAINT fk_consignments_users_created_by_user_id FOREIGN KEY (created_by_user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE TABLE delivery_preferences (
        debtor_id uuid NOT NULL,
        business_id uuid NOT NULL,
        mode character varying(20) NOT NULL,
        transporter_id uuid,
        destination_branch_id uuid,
        delivery_address character varying(500),
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_delivery_preferences PRIMARY KEY (debtor_id),
        CONSTRAINT ck_delivery_preferences_lorry CHECK ((mode = 'LORRY') = (transporter_id IS NOT NULL) AND (destination_branch_id IS NULL OR transporter_id IS NOT NULL)),
        CONSTRAINT ck_delivery_preferences_mode CHECK (mode IN ('PICKUP', 'OWN_VEHICLE', 'LORRY', 'LOCAL_DELIVERY')),
        CONSTRAINT fk_delivery_preferences_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_delivery_preferences_debtors_debtor_id_business_id FOREIGN KEY (debtor_id, business_id) REFERENCES debtors (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_delivery_preferences_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_delivery_preferences_transporter_branches_destination_branc FOREIGN KEY (destination_branch_id, transporter_id, business_id) REFERENCES transporter_branches (id, transporter_id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_delivery_preferences_transporters_transporter_id_business_id FOREIGN KEY (transporter_id, business_id) REFERENCES transporters (id, business_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE TABLE invoice_fulfilments (
        invoice_id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        mode character varying(20) NOT NULL,
        delivery_address character varying(500),
        contact_phone character varying(20),
        transporter_id uuid,
        destination_branch_id uuid,
        note character varying(300),
        chosen_by_user_id uuid NOT NULL,
        created_at_utc timestamp with time zone NOT NULL,
        updated_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_invoice_fulfilments PRIMARY KEY (invoice_id),
        CONSTRAINT ak_invoice_fulfilments_invoice_id_business_id UNIQUE (invoice_id, business_id),
        CONSTRAINT ck_invoice_fulfilments_details CHECK ((mode = 'PICKUP' OR delivery_address IS NOT NULL) AND ((mode = 'LORRY') = (transporter_id IS NOT NULL)) AND (destination_branch_id IS NULL OR transporter_id IS NOT NULL)),
        CONSTRAINT ck_invoice_fulfilments_mode CHECK (mode IN ('PICKUP', 'OWN_VEHICLE', 'LORRY', 'LOCAL_DELIVERY')),
        CONSTRAINT fk_invoice_fulfilments_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_invoice_fulfilments_sales_invoices_invoice_id_business_id FOREIGN KEY (invoice_id, business_id) REFERENCES sales_invoices (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_invoice_fulfilments_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_invoice_fulfilments_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_invoice_fulfilments_transporter_branches_destination_branch FOREIGN KEY (destination_branch_id, transporter_id, business_id) REFERENCES transporter_branches (id, transporter_id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_invoice_fulfilments_transporters_transporter_id_business_id FOREIGN KEY (transporter_id, business_id) REFERENCES transporters (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_invoice_fulfilments_users_chosen_by_user_id FOREIGN KEY (chosen_by_user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE TABLE transporter_routes (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        transporter_id uuid NOT NULL,
        from_branch_id uuid NOT NULL,
        to_branch_id uuid NOT NULL,
        transit_days integer NOT NULL,
        is_active boolean NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_transporter_routes PRIMARY KEY (id),
        CONSTRAINT ck_transporter_routes_branches CHECK (from_branch_id <> to_branch_id),
        CONSTRAINT ck_transporter_routes_transit CHECK (transit_days BETWEEN 0 AND 60),
        CONSTRAINT fk_transporter_routes_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_transporter_routes_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_transporter_routes_transporter_branches_from_branch_id_tran FOREIGN KEY (from_branch_id, transporter_id, business_id) REFERENCES transporter_branches (id, transporter_id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_transporter_routes_transporter_branches_to_branch_id_transp FOREIGN KEY (to_branch_id, transporter_id, business_id) REFERENCES transporter_branches (id, transporter_id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_transporter_routes_transporters_transporter_id_business_id FOREIGN KEY (transporter_id, business_id) REFERENCES transporters (id, business_id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE TABLE consignment_invoices (
        consignment_id uuid NOT NULL,
        invoice_id uuid NOT NULL,
        business_id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_consignment_invoices PRIMARY KEY (consignment_id, invoice_id),
        CONSTRAINT fk_consignment_invoices_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_consignment_invoices_consignments_consignment_id_business_id FOREIGN KEY (consignment_id, business_id) REFERENCES consignments (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_consignment_invoices_invoice_fulfilments_invoice_id_busines FOREIGN KEY (invoice_id, business_id) REFERENCES invoice_fulfilments (invoice_id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_consignment_invoices_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_consignment_invoices_business_id_tenant_id ON consignment_invoices (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_consignment_invoices_consignment_id_business_id ON consignment_invoices (consignment_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_consignment_invoices_invoice_id ON consignment_invoices (invoice_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_consignment_invoices_invoice_id_business_id ON consignment_invoices (invoice_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_consignment_invoices_tenant_id ON consignment_invoices (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_consignments_booking_branch_id_transporter_id_business_id ON consignments (booking_branch_id, transporter_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_consignments_business_id_dispatch_date ON consignments (business_id, dispatch_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE UNIQUE INDEX ix_consignments_business_id_idempotency_key ON consignments (business_id, idempotency_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE UNIQUE INDEX ix_consignments_business_id_number ON consignments (business_id, number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_consignments_business_id_tenant_id ON consignments (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_consignments_cancelled_by_user_id ON consignments (cancelled_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_consignments_created_by_user_id ON consignments (created_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_consignments_destination_branch_id_transporter_id_business_ ON consignments (destination_branch_id, transporter_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_consignments_store_id_business_id ON consignments (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_consignments_tenant_id ON consignments (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_consignments_transporter_id_business_id ON consignments (transporter_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE UNIQUE INDEX ux_consignments_lr ON consignments (business_id, transporter_id, lr_number) WHERE lr_number IS NOT NULL AND status = 'DISPATCHED';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_delivery_preferences_business_id_tenant_id ON delivery_preferences (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE UNIQUE INDEX ix_delivery_preferences_debtor_id_business_id ON delivery_preferences (debtor_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_delivery_preferences_destination_branch_id_transporter_id_b ON delivery_preferences (destination_branch_id, transporter_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_delivery_preferences_tenant_id ON delivery_preferences (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_delivery_preferences_transporter_id_business_id ON delivery_preferences (transporter_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_invoice_fulfilments_business_id_mode ON invoice_fulfilments (business_id, mode);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_invoice_fulfilments_business_id_tenant_id ON invoice_fulfilments (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_invoice_fulfilments_chosen_by_user_id ON invoice_fulfilments (chosen_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_invoice_fulfilments_destination_branch_id_transporter_id_bu ON invoice_fulfilments (destination_branch_id, transporter_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_invoice_fulfilments_store_id_business_id ON invoice_fulfilments (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_invoice_fulfilments_tenant_id ON invoice_fulfilments (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_invoice_fulfilments_transporter_id_business_id ON invoice_fulfilments (transporter_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_transporter_branches_business_id_tenant_id ON transporter_branches (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_transporter_branches_tenant_id ON transporter_branches (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_transporter_branches_transporter_id_business_id ON transporter_branches (transporter_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE UNIQUE INDEX ix_transporter_branches_transporter_id_name_city ON transporter_branches (transporter_id, name, city);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_transporter_routes_business_id_tenant_id ON transporter_routes (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_transporter_routes_from_branch_id_transporter_id_business_id ON transporter_routes (from_branch_id, transporter_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_transporter_routes_tenant_id ON transporter_routes (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_transporter_routes_to_branch_id_transporter_id_business_id ON transporter_routes (to_branch_id, transporter_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_transporter_routes_transporter_id_business_id ON transporter_routes (transporter_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE UNIQUE INDEX ix_transporter_routes_transporter_id_from_branch_id_to_branch_ ON transporter_routes (transporter_id, from_branch_id, to_branch_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE UNIQUE INDEX ix_transporters_business_id_code ON transporters (business_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_transporters_business_id_tenant_id ON transporters (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE INDEX ix_transporters_tenant_id ON transporters (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    ALTER TABLE transporters ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON transporters
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE transporter_branches ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON transporter_branches
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE transporter_routes ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON transporter_routes
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE invoice_fulfilments ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON invoice_fulfilments
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE consignments ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON consignments
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE consignment_invoices ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON consignment_invoices
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE delivery_preferences ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON delivery_preferences
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE TRIGGER trg_consignment_invoices_no_update_delete
        BEFORE UPDATE OR DELETE ON consignment_invoices
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_consignment_invoices_no_truncate
        BEFORE TRUNCATE ON consignment_invoices
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    CREATE FUNCTION sb_consignment_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Dispatches cannot be deleted; cancel them instead.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF OLD.status <> 'DISPATCHED' OR NEW.status <> 'CANCELLED'
           OR (to_jsonb(NEW) - ARRAY['status', 'cancel_reason', 'cancelled_by_user_id', 'cancelled_at_utc'])
              IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['status', 'cancel_reason', 'cancelled_by_user_id', 'cancelled_at_utc']) THEN
            RAISE EXCEPTION 'A dispatch can only be cancelled, once.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_consignments_guard BEFORE UPDATE OR DELETE ON consignments FOR EACH ROW EXECUTE FUNCTION sb_consignment_guard();

    CREATE FUNCTION sb_consignment_invoice_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM consignments c WHERE c.id = NEW.consignment_id AND c.status = 'DISPATCHED') THEN
            RAISE EXCEPTION 'Bills are added to a dispatch only when it is recorded.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF EXISTS (SELECT 1 FROM consignment_invoices ci JOIN consignments c ON c.id = ci.consignment_id
                    WHERE ci.invoice_id = NEW.invoice_id AND ci.consignment_id <> NEW.consignment_id AND c.status = 'DISPATCHED') THEN
            RAISE EXCEPTION 'This bill is already in a dispatch.' USING ERRCODE = 'unique_violation';
        END IF;
        IF NOT EXISTS (SELECT 1 FROM consignments c JOIN invoice_fulfilments f ON f.invoice_id = NEW.invoice_id
                        WHERE c.id = NEW.consignment_id AND f.store_id = c.store_id AND f.mode = c.mode) THEN
            RAISE EXCEPTION 'A dispatch carries bills of its own store, delivered its way.' USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_consignment_invoices_guard BEFORE INSERT ON consignment_invoices FOR EACH ROW EXECUTE FUNCTION sb_consignment_invoice_guard();

    CREATE FUNCTION sb_invoice_fulfilment_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'A bill''s delivery is never deleted (set it to pickup instead).' USING ERRCODE = 'restrict_violation';
        END IF;
        IF (NEW.invoice_id, NEW.tenant_id, NEW.business_id, NEW.store_id, NEW.created_at_utc)
           IS DISTINCT FROM (OLD.invoice_id, OLD.tenant_id, OLD.business_id, OLD.store_id, OLD.created_at_utc) THEN
            RAISE EXCEPTION 'A bill''s delivery stays with its bill.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF EXISTS (SELECT 1 FROM consignment_invoices ci JOIN consignments c ON c.id = ci.consignment_id
                    WHERE ci.invoice_id = OLD.invoice_id AND c.status = 'DISPATCHED') THEN
            RAISE EXCEPTION 'The goods on this bill have been dispatched; cancel the dispatch first.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_invoice_fulfilments_guard BEFORE UPDATE OR DELETE ON invoice_fulfilments FOR EACH ROW EXECUTE FUNCTION sb_invoice_fulfilment_guard();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261004080305_Dispatch') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261004080305_Dispatch', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    ALTER TABLE consignments ADD delivered_on date;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    ALTER TABLE consignments ADD delivery_note character varying(300);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    ALTER TABLE consignments ADD delivery_outcome character varying(20);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    ALTER TABLE consignments ADD delivery_reported_at_utc timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    ALTER TABLE consignments ADD delivery_reported_by_user_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    ALTER TABLE consignments ADD return_recorded_at_utc timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    ALTER TABLE consignments ADD return_recorded_by_user_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE TABLE packing_challans (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        store_id uuid NOT NULL,
        invoice_id uuid NOT NULL,
        number character varying(40) NOT NULL,
        party_name character varying(200) NOT NULL,
        status character varying(20) NOT NULL,
        picked_by_user_id uuid,
        picked_at_utc timestamp with time zone,
        checked_by_user_id uuid,
        checked_at_utc timestamp with time zone,
        packed_by_user_id uuid,
        package_count integer NOT NULL,
        cancel_reason character varying(300),
        created_at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_packing_challans PRIMARY KEY (id),
        CONSTRAINT ak_packing_challans_id_business_id UNIQUE (id, business_id),
        CONSTRAINT ck_packing_challans_status CHECK (status IN ('OPEN', 'CANCELLED') AND (status = 'CANCELLED') = (cancel_reason IS NOT NULL)),
        CONSTRAINT ck_packing_challans_steps CHECK ((checked_by_user_id IS NULL OR (picked_by_user_id IS NOT NULL AND checked_by_user_id <> picked_by_user_id)) AND (packed_by_user_id IS NULL OR checked_by_user_id IS NOT NULL) AND package_count BETWEEN 0 AND 99999 AND (picked_by_user_id IS NULL) = (picked_at_utc IS NULL) AND (checked_by_user_id IS NULL) = (checked_at_utc IS NULL)),
        CONSTRAINT fk_packing_challans_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_packing_challans_sales_invoices_invoice_id_business_id FOREIGN KEY (invoice_id, business_id) REFERENCES sales_invoices (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_packing_challans_stores_store_id_business_id FOREIGN KEY (store_id, business_id) REFERENCES stores (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_packing_challans_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_packing_challans_users_checked_by_user_id FOREIGN KEY (checked_by_user_id) REFERENCES users (id) ON DELETE RESTRICT,
        CONSTRAINT fk_packing_challans_users_packed_by_user_id FOREIGN KEY (packed_by_user_id) REFERENCES users (id) ON DELETE RESTRICT,
        CONSTRAINT fk_packing_challans_users_picked_by_user_id FOREIGN KEY (picked_by_user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE TABLE packing_challan_lines (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        challan_id uuid NOT NULL,
        invoice_line_id uuid NOT NULL,
        line_number integer NOT NULL,
        item_name character varying(200) NOT NULL,
        variant_name character varying(200),
        unit_code character varying(10) NOT NULL,
        quantity numeric(18,3) NOT NULL,
        free_quantity numeric(18,3) NOT NULL,
        picked_quantity numeric(18,3),
        checked_quantity numeric(18,3),
        short_reason character varying(420),
        packed_quantity numeric(18,3) NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_packing_challan_lines PRIMARY KEY (id),
        CONSTRAINT ak_packing_challan_lines_id_business_id UNIQUE (id, business_id),
        CONSTRAINT ck_packing_challan_lines_quantities CHECK (quantity > 0 AND free_quantity >= 0 AND (picked_quantity IS NULL OR picked_quantity BETWEEN 0 AND quantity) AND (checked_quantity IS NULL OR (picked_quantity IS NOT NULL AND checked_quantity BETWEEN 0 AND picked_quantity)) AND packed_quantity >= 0 AND packed_quantity <= coalesce(checked_quantity, 0) AND (short_reason IS NOT NULL OR ((picked_quantity IS NULL OR picked_quantity = quantity) AND (checked_quantity IS NULL OR checked_quantity = picked_quantity)))),
        CONSTRAINT fk_packing_challan_lines_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_packing_challan_lines_packing_challans_challan_id_business_ FOREIGN KEY (challan_id, business_id) REFERENCES packing_challans (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_packing_challan_lines_sales_invoice_lines_invoice_line_id FOREIGN KEY (invoice_line_id) REFERENCES sales_invoice_lines (id) ON DELETE RESTRICT,
        CONSTRAINT fk_packing_challan_lines_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE TABLE packing_events (
        id uuid NOT NULL,
        business_id uuid NOT NULL,
        challan_id uuid NOT NULL,
        kind character varying(20) NOT NULL,
        detail character varying(500),
        user_id uuid NOT NULL,
        at_utc timestamp with time zone NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_packing_events PRIMARY KEY (id),
        CONSTRAINT fk_packing_events_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_packing_events_packing_challans_challan_id_business_id FOREIGN KEY (challan_id, business_id) REFERENCES packing_challans (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_packing_events_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT,
        CONSTRAINT fk_packing_events_users_user_id FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE TABLE consignment_lines (
        consignment_id uuid NOT NULL,
        challan_line_id uuid NOT NULL,
        business_id uuid NOT NULL,
        quantity numeric(18,3) NOT NULL,
        delivered_quantity numeric(18,3),
        returned_quantity numeric(18,3),
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_consignment_lines PRIMARY KEY (consignment_id, challan_line_id),
        CONSTRAINT ck_consignment_lines_quantities CHECK (quantity > 0 AND (delivered_quantity IS NULL OR delivered_quantity BETWEEN 0 AND quantity) AND (returned_quantity IS NULL OR (delivered_quantity IS NOT NULL AND returned_quantity >= 0 AND delivered_quantity + returned_quantity <= quantity))),
        CONSTRAINT fk_consignment_lines_businesses_business_id_tenant_id FOREIGN KEY (business_id, tenant_id) REFERENCES businesses (id, tenant_id) ON DELETE RESTRICT,
        CONSTRAINT fk_consignment_lines_consignments_consignment_id_business_id FOREIGN KEY (consignment_id, business_id) REFERENCES consignments (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_consignment_lines_packing_challan_lines_challan_line_id_bus FOREIGN KEY (challan_line_id, business_id) REFERENCES packing_challan_lines (id, business_id) ON DELETE RESTRICT,
        CONSTRAINT fk_consignment_lines_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    ALTER TABLE consignments ADD CONSTRAINT ck_consignments_delivery CHECK ((delivery_outcome IS NULL OR delivery_outcome IN ('DELIVERED', 'PARTLY_DELIVERED', 'FAILED')) AND ((delivery_outcome IS NULL) = (delivered_on IS NULL) AND (delivery_outcome IS NULL) = (delivery_reported_at_utc IS NULL)) AND (delivery_outcome IS NULL OR delivery_outcome = 'DELIVERED' OR delivery_note IS NOT NULL) AND (delivered_on IS NULL OR delivered_on >= dispatch_date) AND (return_recorded_at_utc IS NULL OR delivery_outcome IN ('PARTLY_DELIVERED', 'FAILED')) AND (status = 'DISPATCHED' OR delivery_outcome IS NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_consignment_lines_business_id_tenant_id ON consignment_lines (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_consignment_lines_challan_line_id ON consignment_lines (challan_line_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_consignment_lines_challan_line_id_business_id ON consignment_lines (challan_line_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_consignment_lines_consignment_id_business_id ON consignment_lines (consignment_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_consignment_lines_tenant_id ON consignment_lines (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_packing_challan_lines_business_id_tenant_id ON packing_challan_lines (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_packing_challan_lines_challan_id_business_id ON packing_challan_lines (challan_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE UNIQUE INDEX ix_packing_challan_lines_challan_id_line_number ON packing_challan_lines (challan_id, line_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_packing_challan_lines_invoice_line_id ON packing_challan_lines (invoice_line_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_packing_challan_lines_tenant_id ON packing_challan_lines (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE UNIQUE INDEX ix_packing_challans_business_id_number ON packing_challans (business_id, number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_packing_challans_business_id_tenant_id ON packing_challans (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_packing_challans_checked_by_user_id ON packing_challans (checked_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_packing_challans_invoice_id_business_id ON packing_challans (invoice_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_packing_challans_packed_by_user_id ON packing_challans (packed_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_packing_challans_picked_by_user_id ON packing_challans (picked_by_user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_packing_challans_store_id_business_id ON packing_challans (store_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_packing_challans_tenant_id ON packing_challans (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE UNIQUE INDEX ux_packing_challans_open_invoice ON packing_challans (invoice_id) WHERE status = 'OPEN';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_packing_events_business_id_tenant_id ON packing_events (business_id, tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_packing_events_challan_id ON packing_events (challan_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_packing_events_challan_id_business_id ON packing_events (challan_id, business_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_packing_events_tenant_id ON packing_events (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE INDEX ix_packing_events_user_id ON packing_events (user_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    DO $$
    BEGIN
        IF EXISTS (SELECT 1 FROM consignments WHERE status = 'DISPATCHED') THEN
            RAISE EXCEPTION 'Dispatches recorded before packing challans exist; cancel them (they are re-recorded after packing) before upgrading.';
        END IF;
    END
    $$;

    CREATE TEMP TABLE sb_new_challans ON COMMIT DROP AS
    SELECT gen_random_uuid() AS id, f.tenant_id, f.business_id, f.store_id, f.invoice_id, s.code AS store_code,
           coalesce(d.trade_name, d.legal_name, i.buyer_name, 'Walk-in customer') AS party_name,
           row_number() OVER (PARTITION BY f.store_id ORDER BY f.created_at_utc, f.invoice_id)
             + coalesce((SELECT q.next_number - 1 FROM document_sequences q WHERE q.store_id = f.store_id AND q.series = 'PCH'), 0) AS seq
      FROM invoice_fulfilments f
      JOIN sales_invoices i ON i.id = f.invoice_id
      JOIN stores s ON s.id = f.store_id
      LEFT JOIN debtors d ON d.id = i.debtor_id
     WHERE f.mode <> 'PICKUP';

    INSERT INTO packing_challans (id, tenant_id, business_id, store_id, invoice_id, number, party_name, status, package_count, created_at_utc)
    SELECT id, tenant_id, business_id, store_id, invoice_id, store_code || '/PCH/' || lpad(seq::text, 6, '0'), party_name, 'OPEN', 0, now()
      FROM sb_new_challans;

    INSERT INTO packing_challan_lines (id, tenant_id, business_id, challan_id, invoice_line_id, line_number, item_name, variant_name, unit_code, quantity,
                                       free_quantity, packed_quantity)
    SELECT gen_random_uuid(), c.tenant_id, c.business_id, c.id, l.id, l.line_number, coalesce(p.name, l.description),
           CASE WHEN p.name IS NULL OR p.name = l.description THEN NULL ELSE l.description END, l.unit_code, l.quantity, 0, 0
      FROM sb_new_challans c
      JOIN sales_invoice_lines l ON l.invoice_id = c.invoice_id
      LEFT JOIN products p ON p.id = l.product_id;

    INSERT INTO document_sequences (id, tenant_id, business_id, store_id, series, next_number)
    SELECT gen_random_uuid(), tenant_id, business_id, store_id, 'PCH', max(seq) + 1 FROM sb_new_challans GROUP BY tenant_id, business_id, store_id
    ON CONFLICT (store_id, series) DO UPDATE SET next_number = EXCLUDED.next_number;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    ALTER TABLE packing_challans ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON packing_challans
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE packing_challan_lines ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON packing_challan_lines
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE packing_events ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON packing_events
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    ALTER TABLE consignment_lines ENABLE ROW LEVEL SECURITY;
    CREATE POLICY tenant_isolation ON consignment_lines
        USING (tenant_id = sb_current_tenant())
        WITH CHECK (tenant_id = sb_current_tenant());
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE TRIGGER trg_packing_events_no_update_delete
        BEFORE UPDATE OR DELETE ON packing_events
        FOR EACH ROW EXECUTE FUNCTION sb_reject_mutation();
    CREATE TRIGGER trg_packing_events_no_truncate
        BEFORE TRUNCATE ON packing_events
        FOR EACH STATEMENT EXECUTE FUNCTION sb_reject_mutation();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    CREATE FUNCTION sb_packing_challan_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Packing challans cannot be deleted.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.store_id, NEW.invoice_id, NEW.number, NEW.party_name, NEW.created_at_utc)
             IS DISTINCT FROM (OLD.id, OLD.tenant_id, OLD.business_id, OLD.store_id, OLD.invoice_id, OLD.number, OLD.party_name, OLD.created_at_utc)
           OR (OLD.picked_by_user_id IS NOT NULL AND (NEW.picked_by_user_id, NEW.picked_at_utc) IS DISTINCT FROM (OLD.picked_by_user_id, OLD.picked_at_utc))
           OR (OLD.checked_by_user_id IS NOT NULL AND (NEW.checked_by_user_id, NEW.checked_at_utc) IS DISTINCT FROM (OLD.checked_by_user_id, OLD.checked_at_utc))
           OR (OLD.packed_by_user_id IS NOT NULL AND NEW.packed_by_user_id IS DISTINCT FROM OLD.packed_by_user_id)
           OR NEW.package_count < OLD.package_count
           OR (OLD.status = 'CANCELLED' AND NEW IS DISTINCT FROM OLD) THEN
            RAISE EXCEPTION 'A packing challan keeps what was recorded.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF OLD.status = 'OPEN' AND NEW.status = 'CANCELLED' AND EXISTS (
            SELECT 1 FROM consignment_lines cl JOIN consignments c ON c.id = cl.consignment_id
              JOIN packing_challan_lines l ON l.id = cl.challan_line_id
             WHERE l.challan_id = OLD.id AND c.status = 'DISPATCHED') THEN
            RAISE EXCEPTION 'Goods on this challan have been dispatched.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_packing_challans_guard BEFORE UPDATE OR DELETE ON packing_challans FOR EACH ROW EXECUTE FUNCTION sb_packing_challan_guard();

    CREATE FUNCTION sb_packing_challan_line_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Challan lines cannot be deleted.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.challan_id, NEW.invoice_line_id, NEW.line_number, NEW.item_name, NEW.variant_name, NEW.unit_code,
            NEW.quantity, NEW.free_quantity)
             IS DISTINCT FROM (OLD.id, OLD.tenant_id, OLD.business_id, OLD.challan_id, OLD.invoice_line_id, OLD.line_number, OLD.item_name, OLD.variant_name,
            OLD.unit_code, OLD.quantity, OLD.free_quantity)
           OR (OLD.picked_quantity IS NOT NULL AND NEW.picked_quantity IS DISTINCT FROM OLD.picked_quantity)
           OR (OLD.checked_quantity IS NOT NULL AND NEW.checked_quantity IS DISTINCT FROM OLD.checked_quantity)
           OR NEW.packed_quantity < OLD.packed_quantity THEN
            RAISE EXCEPTION 'A challan line keeps what was picked, checked and packed.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_packing_challan_lines_guard BEFORE UPDATE OR DELETE ON packing_challan_lines FOR EACH ROW EXECUTE FUNCTION sb_packing_challan_line_guard();

    CREATE FUNCTION sb_consignment_line_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    DECLARE
        v_out numeric;
        v_packed numeric;
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Dispatch lines cannot be deleted.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF TG_OP = 'UPDATE' THEN
            IF (NEW.consignment_id, NEW.challan_line_id, NEW.tenant_id, NEW.business_id, NEW.quantity)
                 IS DISTINCT FROM (OLD.consignment_id, OLD.challan_line_id, OLD.tenant_id, OLD.business_id, OLD.quantity)
               OR (OLD.delivered_quantity IS NOT NULL AND NEW.delivered_quantity IS DISTINCT FROM OLD.delivered_quantity)
               OR (OLD.returned_quantity IS NOT NULL AND NEW.returned_quantity IS DISTINCT FROM OLD.returned_quantity) THEN
                RAISE EXCEPTION 'A dispatch line keeps what was sent, delivered and returned.' USING ERRCODE = 'restrict_violation';
            END IF;
            RETURN NEW;
        END IF;
        IF NEW.delivered_quantity IS NOT NULL OR NEW.returned_quantity IS NOT NULL THEN
            RAISE EXCEPTION 'Delivery is reported after dispatch.' USING ERRCODE = 'check_violation';
        END IF;
        IF NOT EXISTS (SELECT 1 FROM consignments c JOIN packing_challan_lines l ON l.id = NEW.challan_line_id
                         JOIN packing_challans ch ON ch.id = l.challan_id
                        WHERE c.id = NEW.consignment_id AND c.status = 'DISPATCHED' AND ch.status = 'OPEN' AND ch.store_id = c.store_id) THEN
            RAISE EXCEPTION 'A dispatch carries goods of open challans of its own store.' USING ERRCODE = 'check_violation';
        END IF;
        SELECT coalesce(sum(cl.quantity - coalesce(cl.returned_quantity, 0)), 0) INTO v_out
          FROM consignment_lines cl JOIN consignments c ON c.id = cl.consignment_id
         WHERE cl.challan_line_id = NEW.challan_line_id AND c.status = 'DISPATCHED';
        SELECT packed_quantity INTO v_packed FROM packing_challan_lines WHERE id = NEW.challan_line_id;
        IF v_out + NEW.quantity > v_packed THEN
            RAISE EXCEPTION 'More would be dispatched than was packed.' USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_consignment_lines_guard BEFORE INSERT OR UPDATE OR DELETE ON consignment_lines FOR EACH ROW EXECUTE FUNCTION sb_consignment_line_guard();

    DROP TRIGGER trg_consignments_guard ON consignments;
    DROP FUNCTION sb_consignment_guard();
    CREATE FUNCTION sb_consignment_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    DECLARE
        v_cancel text[] := ARRAY['status', 'cancel_reason', 'cancelled_by_user_id', 'cancelled_at_utc'];
        v_delivery text[] := ARRAY['delivery_outcome', 'delivered_on', 'delivery_note', 'delivery_reported_by_user_id', 'delivery_reported_at_utc'];
        v_return text[] := ARRAY['return_recorded_by_user_id', 'return_recorded_at_utc'];
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Dispatches cannot be deleted; cancel them instead.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF (to_jsonb(NEW) - v_cancel - v_delivery - v_return) IS DISTINCT FROM (to_jsonb(OLD) - v_cancel - v_delivery - v_return)
           OR (OLD.status = 'CANCELLED' AND NEW IS DISTINCT FROM OLD)
           OR (NEW.status = 'CANCELLED' AND (OLD.delivery_outcome IS NOT NULL OR (to_jsonb(NEW) - v_cancel) IS DISTINCT FROM (to_jsonb(OLD) - v_cancel)))
           OR (OLD.delivery_outcome IS NOT NULL AND (NEW.delivery_outcome, NEW.delivered_on, NEW.delivery_note, NEW.delivery_reported_by_user_id,
                 NEW.delivery_reported_at_utc) IS DISTINCT FROM (OLD.delivery_outcome, OLD.delivered_on, OLD.delivery_note, OLD.delivery_reported_by_user_id,
                 OLD.delivery_reported_at_utc))
           OR (OLD.return_recorded_at_utc IS NOT NULL AND (NEW.return_recorded_at_utc, NEW.return_recorded_by_user_id)
                 IS DISTINCT FROM (OLD.return_recorded_at_utc, OLD.return_recorded_by_user_id))
           OR (NEW.return_recorded_at_utc IS NOT NULL AND NEW.delivery_outcome IS NULL) THEN
            RAISE EXCEPTION 'A dispatch changes only by being cancelled, by its delivery report and by goods coming back, once each.' USING ERRCODE = 'restrict_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    CREATE TRIGGER trg_consignments_guard BEFORE UPDATE OR DELETE ON consignments FOR EACH ROW EXECUTE FUNCTION sb_consignment_guard();

    -- A bill can now go in several dispatches (in parts); what each carries is checked on its lines.
    CREATE OR REPLACE FUNCTION sb_consignment_invoice_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM consignments c WHERE c.id = NEW.consignment_id AND c.status = 'DISPATCHED') THEN
            RAISE EXCEPTION 'Bills are added to a dispatch only when it is recorded.' USING ERRCODE = 'restrict_violation';
        END IF;
        IF NOT EXISTS (SELECT 1 FROM consignments c JOIN invoice_fulfilments f ON f.invoice_id = NEW.invoice_id
                        WHERE c.id = NEW.consignment_id AND f.store_id = c.store_id AND f.mode = c.mode) THEN
            RAISE EXCEPTION 'A dispatch carries bills of its own store, delivered its way.' USING ERRCODE = 'check_violation';
        END IF;
        RETURN NEW;
    END;
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005064545_Packing') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261005064545_Packing', '10.0.12');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005080415_StockOrigin') THEN
    ALTER TABLE cost_layers ADD origin character varying(10) NOT NULL DEFAULT 'OTHER';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005080415_StockOrigin') THEN
    ALTER TABLE cost_layers ADD CONSTRAINT ck_cost_layers_origin CHECK (origin IN ('GST', 'NON_GST', 'OTHER'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005080415_StockOrigin') THEN
    UPDATE cost_layers l
       SET origin = CASE g.classification
                        WHEN 'GST_TAX_INVOICE' THEN 'GST' WHEN 'IMPORT' THEN 'GST' WHEN 'REVERSE_CHARGE' THEN 'GST'
                        WHEN 'BILL_OF_SUPPLY' THEN 'NON_GST' WHEN 'UNREGISTERED' THEN 'NON_GST'
                        ELSE 'OTHER' END
      FROM stock_ledger e
      JOIN grns g ON g.id = e.document_id
     WHERE e.layer_id = l.id AND e.movement_type = 'RECEIPT' AND e.document_type = 'GRN';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005080415_StockOrigin') THEN
    CREATE OR REPLACE FUNCTION sb_cost_layer_guard() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Cost layers cannot be deleted.' USING ERRCODE = 'restrict_violation';
        END IF;

        IF (NEW.id, NEW.tenant_id, NEW.business_id, NEW.store_id, NEW.variant_id, NEW.batch_id, NEW.expires_on, NEW.unit_cost,
            NEW.original_quantity, NEW.received_at_utc, NEW.sequence, NEW.origin)
           IS DISTINCT FROM
           (OLD.id, OLD.tenant_id, OLD.business_id, OLD.store_id, OLD.variant_id, OLD.batch_id, OLD.expires_on, OLD.unit_cost,
            OLD.original_quantity, OLD.received_at_utc, OLD.sequence, OLD.origin) THEN
            RAISE EXCEPTION 'A cost layer''s origin cannot be changed.' USING ERRCODE = 'restrict_violation';
        END IF;

        IF NEW.remaining_quantity > OLD.remaining_quantity OR NEW.settled_shortfall < OLD.settled_shortfall THEN
            RAISE EXCEPTION 'Stock cannot be put back into a cost layer; post a new receipt instead.' USING ERRCODE = 'restrict_violation';
        END IF;

        RETURN NEW;
    END;
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261005080415_StockOrigin') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261005080415_StockOrigin', '10.0.12');
    END IF;
END $EF$;
COMMIT;

