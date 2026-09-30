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

