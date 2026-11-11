-- SourceFlow.Net v3 PostgreSQL schema, version 3: in-batch assertions for the one-round-trip commit (doc 05 §3.2).
-- A conditional statement (append compare-and-set, process-state version, fenced checkpoint) calls sf_assert with
-- its outcome. A failed check raises SQLSTATE SF001 with the check's code, so PostgreSQL skips the rest of the batch,
-- including the trailing COMMIT, and the adapter maps the code to ConcurrencyException or LeaseLostException.

CREATE FUNCTION {schema}.sf_assert(ok boolean, code text) RETURNS boolean
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NOT ok THEN
        RAISE EXCEPTION USING ERRCODE = 'SF001', MESSAGE = code;
    END IF;
    RETURN true;
END
$$;
