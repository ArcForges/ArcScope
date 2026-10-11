-- SPDX-License-Identifier: AGPL-3.0-only
-- ArcScope storage schema 1 (APP.02). Canonical annotation content, its content origin, journal and command receipts are
-- committed through the shared store mechanism; this step adds only ArcScope-owned tables: the session-target catalog, the
-- derived annotation session index and its catch-up marker. Unsigned 64-bit values are fixed-width 20-digit decimal text,
-- so text order is numeric order; identities are canonical lowercase UUID text. Each statement starts at a "-- @statement"
-- line.
-- @statement
CREATE TABLE arcscope_session_target(
    session_id TEXT NOT NULL PRIMARY KEY
        CHECK(length(session_id) = 36 AND session_id NOT GLOB '*[^0-9a-f-]*'),
    sample_count TEXT NOT NULL
        CHECK(length(sample_count) = 20 AND sample_count NOT GLOB '*[^0-9]*')
) STRICT, WITHOUT ROWID;
-- @statement
CREATE TRIGGER arcscope_session_target_only_grows BEFORE UPDATE ON arcscope_session_target
WHEN NEW.session_id <> OLD.session_id OR NEW.sample_count < OLD.sample_count
BEGIN
    SELECT RAISE(ABORT, 'arcscope_session_target rows only grow');
END;
-- @statement
CREATE TRIGGER arcscope_session_target_is_never_deleted BEFORE DELETE ON arcscope_session_target
BEGIN
    SELECT RAISE(ABORT, 'arcscope_session_target rows are never deleted');
END;
-- @statement
CREATE TABLE arcscope_annotation_index(
    annotation_id TEXT NOT NULL PRIMARY KEY
        CHECK(length(annotation_id) = 36 AND annotation_id NOT GLOB '*[^0-9a-f-]*'),
    session_id TEXT NOT NULL
        CHECK(length(session_id) = 36 AND session_id NOT GLOB '*[^0-9a-f-]*'),
    range_from TEXT NOT NULL
        CHECK(length(range_from) = 20 AND range_from NOT GLOB '*[^0-9]*'),
    range_count TEXT NOT NULL
        CHECK(length(range_count) = 20 AND range_count NOT GLOB '*[^0-9]*' AND range_count <> '00000000000000000000')
) STRICT, WITHOUT ROWID;
-- @statement
CREATE INDEX arcscope_annotation_index_by_session
    ON arcscope_annotation_index(session_id, range_from, range_count, annotation_id);
-- @statement
CREATE TABLE arcscope_store_state(
    key TEXT NOT NULL PRIMARY KEY
        CHECK(key IN ('index_catch_up_after')),
    value TEXT NOT NULL
        CHECK(length(value) = 20 AND value NOT GLOB '*[^0-9]*')
) STRICT, WITHOUT ROWID;
