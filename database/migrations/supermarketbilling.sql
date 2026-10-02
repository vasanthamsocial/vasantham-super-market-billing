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

