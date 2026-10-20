-- Run tracking and raw-file provenance. Provider-specific tables are added by
-- their own migrations in their own branches; this migration stays generic.

create table if not exists ingestion_runs (
    id                   bigint generated always as identity primary key,
    provider_name        text        not null,
    asset_class          text        not null,
    source_id            text        not null,
    sha256               text,
    subject_id           text,
    reporting_period     date,
    status               smallint    not null,
    started_at           timestamptz not null,
    completed_at         timestamptz,
    records_parsed       bigint      not null default 0,
    records_loaded       bigint      not null default 0,
    validation_errors    bigint      not null default 0,
    validation_warnings  bigint      not null default 0,
    error_message        text,

    constraint ingestion_runs_status_known check (status between 0 and 3),
    constraint ingestion_runs_completed_after_started
        check (completed_at is null or completed_at >= started_at)
);

comment on table ingestion_runs is
    'One row per attempt at one source. The report cites these ids so every published number is traceable.';
comment on column ingestion_runs.status is
    '0=Running, 1=Succeeded, 2=Failed, 3=Skipped (DataAcquisition.Core.Model.IngestionRunStatus).';
comment on column ingestion_runs.sha256 is
    'SHA-256 of the raw file. Null until the source has been fetched.';

-- Serves the already-ingested check: provider + source + content hash, successes only.
-- Deliberately not unique: --force re-ingests a source on purpose and must be able to
-- write a second succeeded row.
create index if not exists ix_ingestion_runs_identity
    on ingestion_runs (provider_name, source_id, sha256)
    where status = 1;

-- Serves "latest run per subject and period" for report provenance.
create index if not exists ix_ingestion_runs_subject_period
    on ingestion_runs (provider_name, subject_id, reporting_period desc, id desc);

create table if not exists source_files (
    id                bigint generated always as identity primary key,
    provider_name     text        not null,
    source_id         text        not null,
    sha256            text        not null,
    uri               text        not null,
    size_bytes        bigint      not null,
    subject_id        text,
    reporting_period  date,
    first_seen_at     timestamptz not null default now(),
    last_fetched_at   timestamptz not null default now(),

    constraint source_files_identity unique (provider_name, source_id, sha256),
    constraint source_files_size_non_negative check (size_bytes >= 0)
);

comment on table source_files is
    'Raw files seen upstream, keyed by content hash so a re-filed exhibit under an existing accession number is a distinct row.';
