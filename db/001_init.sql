-- =============================================================================
-- 001_init.sql — Agentic AI Platform Core Schema
-- Run as: PGPASSWORD='Atlas_71' psql -h 172.16.0.8 -U setadmin -d mydb -f 001_init.sql
-- =============================================================================

BEGIN;

-- ── Extensions ────────────────────────────────────────────────────────────────
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS pg_trgm;     -- BM25 / full-text trigram support
CREATE EXTENSION IF NOT EXISTS unaccent;    -- Türkçe karakter normalize

-- ── Text search config (Türkçe) ───────────────────────────────────────────────
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_ts_config WHERE cfgname = 'turkish_unaccent'
    ) THEN
        CREATE TEXT SEARCH CONFIGURATION turkish_unaccent (COPY = turkish);
        ALTER TEXT SEARCH CONFIGURATION turkish_unaccent
            ALTER MAPPING FOR hword, hword_part, word WITH unaccent, turkish_stem;
    END IF;
END;
$$;

-- =============================================================================
-- 1. KNOWLEDGE BASE
-- =============================================================================
CREATE TABLE IF NOT EXISTS kb_documents (
    id           UUID         PRIMARY KEY DEFAULT uuid_generate_v4(),
    collection   TEXT         NOT NULL,                  -- logical namespace (e.g. 'finance', 'hr')
    source       TEXT         NOT NULL DEFAULT '',       -- file path, URL, system name
    title        TEXT         NOT NULL DEFAULT '',
    content      TEXT         NOT NULL,
    chunk_index  INT          NOT NULL DEFAULT 0,        -- position within source document
    chunk_total  INT          NOT NULL DEFAULT 1,
    embedding    vector(768),                            -- nomic-embed-text-v1.5
    metadata     JSONB        NOT NULL DEFAULT '{}',
    ts_content   TSVECTOR     GENERATED ALWAYS AS (
                     to_tsvector('turkish_unaccent', coalesce(title,'') || ' ' || content)
                 ) STORED,
    created_at   TIMESTAMPTZ  NOT NULL DEFAULT now(),
    updated_at   TIMESTAMPTZ  NOT NULL DEFAULT now()
);

-- HNSW vector index
CREATE INDEX IF NOT EXISTS kb_documents_embedding_hnsw_idx
    ON kb_documents USING hnsw (embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);

-- Full-text index
CREATE INDEX IF NOT EXISTS kb_documents_ts_idx
    ON kb_documents USING gin (ts_content);

-- Metadata & collection filters
CREATE INDEX IF NOT EXISTS kb_documents_collection_idx ON kb_documents (collection);
CREATE INDEX IF NOT EXISTS kb_documents_metadata_idx   ON kb_documents USING gin (metadata);

-- =============================================================================
-- 2. SESSION MEMORY
-- =============================================================================
CREATE TABLE IF NOT EXISTS session_memories (
    id           UUID         PRIMARY KEY DEFAULT uuid_generate_v4(),
    session_id   TEXT         NOT NULL,
    user_id      TEXT         NOT NULL,
    agent_id     TEXT         NOT NULL DEFAULT 'default',
    role         TEXT         NOT NULL CHECK (role IN ('system','user','assistant','tool')),
    content      TEXT         NOT NULL,
    token_count  INT          NOT NULL DEFAULT 0,
    metadata     JSONB        NOT NULL DEFAULT '{}',
    created_at   TIMESTAMPTZ  NOT NULL DEFAULT now(),
    expires_at   TIMESTAMPTZ  NOT NULL DEFAULT now() + INTERVAL '24 hours'
);

CREATE INDEX IF NOT EXISTS session_memories_session_idx
    ON session_memories (session_id, created_at DESC);

CREATE INDEX IF NOT EXISTS session_memories_user_idx
    ON session_memories (user_id, created_at DESC);

-- Auto-cleanup: expired rows (call periodically or via pg_cron)
-- NOTE: cannot use WHERE expires_at < now() — now() is STABLE not IMMUTABLE
CREATE INDEX IF NOT EXISTS session_memories_expires_idx
    ON session_memories (expires_at);

-- =============================================================================
-- 3. AGENT MEMORY (skill-scoped persistent facts)
-- =============================================================================
CREATE TABLE IF NOT EXISTS agent_memories (
    id           UUID         PRIMARY KEY DEFAULT uuid_generate_v4(),
    agent_id     TEXT         NOT NULL,
    skill_name   TEXT         NOT NULL,
    user_id      TEXT         NOT NULL,
    content      TEXT         NOT NULL,
    embedding    vector(768),
    importance   SMALLINT     NOT NULL DEFAULT 5 CHECK (importance BETWEEN 1 AND 10),
    metadata     JSONB        NOT NULL DEFAULT '{}',
    created_at   TIMESTAMPTZ  NOT NULL DEFAULT now(),
    updated_at   TIMESTAMPTZ  NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS agent_memories_embedding_hnsw_idx
    ON agent_memories USING hnsw (embedding vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);

CREATE INDEX IF NOT EXISTS agent_memories_lookup_idx
    ON agent_memories (agent_id, skill_name, user_id);

-- =============================================================================
-- 4. HELPER FUNCTIONS
-- =============================================================================

-- Hybrid search score = weighted cosine + BM25
-- weight_vec ∈ [0,1], weight_fts = 1 - weight_vec
CREATE OR REPLACE FUNCTION hybrid_score(
    vec_distance FLOAT,
    fts_rank     FLOAT,
    weight_vec   FLOAT DEFAULT 0.7
) RETURNS FLOAT AS $$
BEGIN
    -- vec_distance ∈ [0,2], convert to similarity ∈ [0,1]
    RETURN (weight_vec * (1.0 - vec_distance / 2.0))
         + ((1.0 - weight_vec) * fts_rank);
END;
$$ LANGUAGE plpgsql IMMUTABLE;


-- Cleanup expired sessions
CREATE OR REPLACE FUNCTION cleanup_expired_sessions() RETURNS INT AS $$
DECLARE
    deleted INT;
BEGIN
    DELETE FROM session_memories WHERE expires_at < now();
    GET DIAGNOSTICS deleted = ROW_COUNT;
    RETURN deleted;
END;
$$ LANGUAGE plpgsql;


-- Update updated_at trigger
CREATE OR REPLACE FUNCTION set_updated_at()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = now();
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE TRIGGER kb_documents_updated_at
    BEFORE UPDATE ON kb_documents
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

CREATE OR REPLACE TRIGGER agent_memories_updated_at
    BEFORE UPDATE ON agent_memories
    FOR EACH ROW EXECUTE FUNCTION set_updated_at();

-- =============================================================================
-- 5. ROW LEVEL SECURITY
-- =============================================================================

-- Session memories: user sees only own sessions
ALTER TABLE session_memories ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS session_memories_user_policy ON session_memories;
CREATE POLICY session_memories_user_policy ON session_memories
    USING (user_id = current_setting('app.current_user', true));

-- Agent memories: user sees only own memories
ALTER TABLE agent_memories ENABLE ROW LEVEL SECURITY;

DROP POLICY IF EXISTS agent_memories_user_policy ON agent_memories;
CREATE POLICY agent_memories_user_policy ON agent_memories
    USING (user_id = current_setting('app.current_user', true));

-- KB is shared (no RLS) — filtered by collection + metadata instead
-- Add RLS later if multi-tenant isolation needed per collection

-- Optional: dedicated app/admin roles (requires CREATEROLE privilege).
-- Skipped silently if current user lacks the privilege — MVP just uses setadmin.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'llm_admin') THEN
        CREATE ROLE llm_admin BYPASSRLS;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'llm_app') THEN
        CREATE ROLE llm_app;
    END IF;

    GRANT SELECT, INSERT, UPDATE, DELETE ON kb_documents     TO llm_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON session_memories TO llm_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON agent_memories   TO llm_app;
    GRANT llm_admin TO setadmin;
EXCEPTION
    WHEN insufficient_privilege THEN
        RAISE NOTICE 'Skipping role creation: current user lacks CREATEROLE. Using direct privileges instead.';
END;
$$;

COMMIT;

-- =============================================================================
-- Verify
-- =============================================================================
SELECT
    tablename,
    (SELECT count(*) FROM information_schema.columns
     WHERE table_name = t.tablename AND table_schema = 'public') AS col_count
FROM (VALUES ('kb_documents'),('session_memories'),('agent_memories')) AS t(tablename);
