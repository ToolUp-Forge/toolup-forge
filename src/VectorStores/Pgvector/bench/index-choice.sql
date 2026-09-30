-- SPDX-License-Identifier: Apache-2.0
--
-- Index-choice measurement for the pgvector vector store (Phase 928).
--
-- Answers two questions on ONE corpus, with EXPLAIN output rather than a
-- reading of the planner:
--
--   1. Does the store's two-key ORDER BY (distance, then chunk_id) stop the
--      approximate index being used? (It does not: the planner serves it
--      through an Incremental Sort presorted on the distance.)
--   2. For many scopes in one table, which posture: one shared HNSW index
--      with a filtered scan and a raised width, a partial HNSW index per
--      scope, or a table partitioned by scope?
--
-- Corpus: 165,000 rows of 128-dim vectors drawn around 200 shared topic
-- centroids (uniform noise +/-0.4 per dimension), so every scope's content
-- spans every topic — the case where a scope filter starves an approximate
-- scan. Scopes: one of 100,000 rows; one each of 20,000 / 10,000 / 5,000;
-- ten of 2,000; two hundred of 50. Fifty query vectors from the same
-- distribution. Recall and short pages are measured against the exact
-- ranking (a MATERIALIZED scan no index can serve); latency is server-side
-- execution of one statement, measured inside PL/pgSQL, so it excludes the
-- client round-trip.
--
-- Run against a scratch database on a pgvector 0.8+ server, for example
-- the `pgvector` service in compose.parity.yml:
--
--   docker compose -f compose.parity.yml up -d --wait pgvector
--   docker compose -f compose.parity.yml exec -T pgvector createdb -U postgres pgv_bench
--   docker compose -f compose.parity.yml exec -T pgvector psql -U postgres -d pgv_bench -q -f - < src/VectorStores/Pgvector/bench/index-choice.sql
--
-- About ten minutes on a laptop, most of it building HNSW indexes. HNSW
-- construction is randomised inside pgvector, so a re-run reproduces the
-- shape of the figures, not every digit.

\pset pager off
SET max_parallel_maintenance_workers = 0;   -- the default /dev/shm of a container is too small for a parallel build
SET maintenance_work_mem = '512MB';
CREATE EXTENSION IF NOT EXISTS vector;

-- ─── Corpus ──────────────────────────────────────────────────────────

DROP TABLE IF EXISTS bench, benchp, centroids, qv CASCADE;

CREATE TABLE bench (
    scope      text        NOT NULL,
    chunk_id   text        NOT NULL,
    content    text        NOT NULL,
    metadata   jsonb       NOT NULL DEFAULT '{}'::jsonb,
    embedding  vector(128) NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT bench_pkey PRIMARY KEY (scope, chunk_id)
);

SELECT setseed(0.928);

CREATE TABLE centroids AS
SELECT c AS id, (SELECT array_agg((random() * 2 - 1)::real) FROM generate_series(1, 128) WHERE c IS NOT NULL) AS v
FROM generate_series(1, 200) c;

CREATE OR REPLACE FUNCTION pt(cid int, noise real) RETURNS vector LANGUAGE sql VOLATILE AS
$$ SELECT array_agg((c.v[i] + noise * (random() * 2 - 1))::real ORDER BY i)::vector
   FROM centroids c, generate_series(1, 128) i WHERE c.id = cid $$;

CREATE TEMP TABLE sizes (scope text, n int);
INSERT INTO sizes VALUES ('team:big', 100000), ('team:s20k', 20000), ('team:s10k', 10000), ('team:s5k', 5000);
INSERT INTO sizes SELECT format('team:mid%s', s), 2000 FROM generate_series(1, 10) s;
INSERT INTO sizes SELECT format('team:small%s', lpad(s::text, 3, '0')), 50 FROM generate_series(1, 200) s;

INSERT INTO bench (scope, chunk_id, content, embedding)
SELECT z.scope, format('c-%s', lpad(g::text, 6, '0')), 'x', pt(1 + floor(random() * 200)::int, 0.4)
FROM sizes z, generate_series(1, z.n) g;

-- The store's own schema: the two scope indexes, then the shared HNSW index.
CREATE INDEX bench_scope_live_idx ON bench (scope) WHERE deleted_at IS NULL;
CREATE INDEX bench_scope_deleted_idx ON bench (scope, deleted_at);
CREATE INDEX bench_embedding_hnsw_idx ON bench USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);

CREATE TABLE qv AS SELECT g AS id, pt(1 + floor(random() * 200)::int, 0.4) AS v FROM generate_series(1, 50) g;
VACUUM ANALYZE bench;

-- ─── Harness ─────────────────────────────────────────────────────────
--
-- measure(table, scope, statement): the plan's scan and sort nodes, mean
-- and p95 latency, how many of the 50 pages came back shorter than the
-- exact one, and recall@k against the exact ranking.
--   two_key — the store's `Sql.search`
--   one_key — the store's `Sql.searchIndexOrdered`
--   exact   — the store's `Sql.searchExact` (the short-page fallback)

CREATE OR REPLACE FUNCTION measure(tbl text, sc text, knd text, k int DEFAULT 10)
RETURNS TABLE (o_scope text, o_kind text, plan text, avg_ms numeric, p95_ms numeric, short_pages int, recall numeric)
LANGUAGE plpgsql AS $$
DECLARE r record; t0 timestamptz; ms numeric[] := '{}'; got text[]; truth text[]; hits int := 0; short int := 0; total int := 0;
        sql text; exact_sql text; pl text := ''; line text; q1 vector;
BEGIN
  exact_sql := format('WITH s AS MATERIALIZED (SELECT chunk_id, embedding FROM %I WHERE scope = $1 AND deleted_at IS NULL) SELECT array_agg(chunk_id) FROM (SELECT chunk_id FROM s ORDER BY embedding <=> $2, chunk_id LIMIT $3) x', tbl);
  sql := CASE knd
    WHEN 'two_key' THEN format('SELECT array_agg(chunk_id) FROM (SELECT chunk_id FROM %I WHERE scope = $1 AND deleted_at IS NULL ORDER BY embedding <=> $2, chunk_id LIMIT $3) x', tbl)
    WHEN 'one_key' THEN format('SELECT array_agg(chunk_id) FROM (SELECT chunk_id FROM %I WHERE scope = $1 AND deleted_at IS NULL ORDER BY embedding <=> $2 LIMIT $3) x', tbl)
    WHEN 'exact'   THEN exact_sql END;
  SELECT v INTO q1 FROM qv WHERE id = 1;
  FOR line IN EXECUTE 'EXPLAIN (COSTS OFF) ' || replace(replace(replace(sql, '$1', quote_literal(sc)), '$2', quote_literal(q1::text) || '::vector'), '$3', k::text) LOOP
    IF line ~ '(Scan|Sort)' AND line !~ 'Key' THEN
      pl := pl || CASE WHEN pl = '' THEN '' ELSE ' / ' END || regexp_replace(trim(regexp_replace(line, '->', '')), ' on .*$', '');
    END IF;
  END LOOP;
  FOR r IN SELECT v FROM qv ORDER BY id LOOP
    EXECUTE exact_sql INTO truth USING sc, r.v, k;
    t0 := clock_timestamp();
    EXECUTE sql INTO got USING sc, r.v, k;
    ms := ms || (extract(epoch FROM clock_timestamp() - t0) * 1000)::numeric;
    IF coalesce(array_length(got, 1), 0) < coalesce(array_length(truth, 1), 0) THEN short := short + 1; END IF;
    hits := hits + (SELECT count(*) FROM unnest(coalesce(got, '{}')) g WHERE g = ANY (truth));
    total := total + coalesce(array_length(truth, 1), 0);
  END LOOP;
  RETURN QUERY SELECT sc, knd, pl,
    round((SELECT avg(x) FROM unnest(ms) x), 3),
    round((SELECT percentile_cont(0.95) WITHIN GROUP (ORDER BY x) FROM unnest(ms) x)::numeric, 3),
    short, round(hits::numeric / nullif(total, 0), 3);
END $$;

-- ─── Shared index ────────────────────────────────────────────────────

\echo ==== A. shared HNSW index, pgvector defaults (hnsw.ef_search = 40, no iterative scan)
SELECT m.* FROM unnest(ARRAY['team:big', 'team:s20k', 'team:s10k', 'team:s5k', 'team:mid1', 'team:small001']) s,
  unnest(ARRAY['two_key', 'one_key', 'exact']) kd, LATERAL measure('bench', s, kd) m;

\echo ==== B. shared HNSW index, PgvectorTuning.recommended (hnsw.ef_search = 100, iterative_scan = relaxed_order)
SET hnsw.ef_search = 100;
SET hnsw.iterative_scan = relaxed_order;
SELECT m.* FROM unnest(ARRAY['team:big', 'team:s20k', 'team:s10k', 'team:s5k', 'team:mid1', 'team:small001']) s,
  unnest(ARRAY['two_key', 'one_key']) kd, LATERAL measure('bench', s, kd) m;

\echo ==== C. shared HNSW index, raised width alone (hnsw.ef_search = 400, no iterative scan)
SET hnsw.ef_search = 400;
SET hnsw.iterative_scan = off;
SELECT m.* FROM unnest(ARRAY['team:big', 'team:s20k', 'team:s10k', 'team:s5k']) s,
  unnest(ARRAY['one_key']) kd, LATERAL measure('bench', s, kd) m;
RESET hnsw.ef_search;
RESET hnsw.iterative_scan;

-- ─── Alternatives ────────────────────────────────────────────────────

\echo ==== building (b) partial HNSW indexes for three scopes, and (c) the partitioned copy
\timing on
CREATE INDEX bench_hnsw_s20k ON bench USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64) WHERE scope = 'team:s20k' AND deleted_at IS NULL;
CREATE INDEX bench_hnsw_s5k ON bench USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64) WHERE scope = 'team:s5k' AND deleted_at IS NULL;
CREATE INDEX bench_hnsw_mid1 ON bench USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64) WHERE scope = 'team:mid1' AND deleted_at IS NULL;

CREATE TABLE benchp (LIKE bench INCLUDING DEFAULTS, CONSTRAINT benchp_pkey PRIMARY KEY (scope, chunk_id)) PARTITION BY LIST (scope);
DO $$ DECLARE s text; i int := 0; BEGIN
  FOR s IN SELECT DISTINCT scope FROM bench ORDER BY 1 LOOP
    i := i + 1;
    EXECUTE format('CREATE TABLE benchp_%s PARTITION OF benchp FOR VALUES IN (%L)', i, s);
  END LOOP; END $$;
INSERT INTO benchp SELECT * FROM bench;
CREATE INDEX benchp_scope_live_idx ON benchp (scope) WHERE deleted_at IS NULL;
CREATE INDEX benchp_embedding_hnsw_idx ON benchp USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);
\timing off
VACUUM ANALYZE bench;
VACUUM ANALYZE benchp;

\echo ==== (b) partial HNSW index per scope, pgvector defaults
SELECT m.* FROM unnest(ARRAY['team:s20k', 'team:s5k', 'team:mid1']) s,
  unnest(ARRAY['one_key', 'two_key']) kd, LATERAL measure('bench', s, kd) m;

\echo ==== (c) partitioned by scope (one HNSW index per partition), pgvector defaults
SELECT m.* FROM unnest(ARRAY['team:big', 'team:s20k', 'team:s10k', 'team:s5k', 'team:mid1', 'team:small001']) s,
  unnest(ARRAY['one_key', 'two_key', 'exact']) kd, LATERAL measure('benchp', s, kd) m;

-- ─── Generic plans ───────────────────────────────────────────────────
--
-- Npgsql sends unnamed statements, planned per execution with the actual
-- parameter values; a client that prepares statements (auto-prepare)
-- eventually runs a GENERIC plan, which cannot know the scope value.

SELECT v::text AS q FROM qv WHERE id = 1 \gset
SET plan_cache_mode = force_generic_plan;
PREPARE g_shared(text, vector, int) AS
  SELECT chunk_id FROM bench WHERE scope = $1 AND deleted_at IS NULL ORDER BY embedding <=> $2 LIMIT $3;
PREPARE g_part(text, vector, int) AS
  SELECT chunk_id FROM benchp WHERE scope = $1 AND deleted_at IS NULL ORDER BY embedding <=> $2 LIMIT $3;
\echo ==== (b) generic plan on the shared table: is the partial index usable?
EXPLAIN (COSTS OFF) EXECUTE g_shared('team:mid1', :'q', 10);
\echo ==== (c) generic plan on the partitioned table: runtime pruning, and its planning cost
EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF, SUMMARY ON) EXECUTE g_part('team:mid1', :'q', 10);
RESET plan_cache_mode;
