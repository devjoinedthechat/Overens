-- Three roles, each with only what its job needs.
--
--   overens_api     serves people: uploads, review decisions, approvals
--   overens_worker  runs the pipeline: reads documents, stores what the model proposes,
--                   and publishes a rule set once a person has approved it
--   (the owner)     applies this file; nothing connects as the owner at run time
--
-- Every table carries tenant_id and row-level security, so a query can only see the
-- tenant named in the transaction's app.tenant_id setting.

do $$
begin
  if not exists (select from pg_roles where rolname = 'overens_api') then
    create role overens_api login;
  end if;
  if not exists (select from pg_roles where rolname = 'overens_worker') then
    create role overens_worker login;
  end if;
end $$;

create function current_tenant() returns text
  language sql stable
  as $$ select nullif(current_setting('app.tenant_id', true), '') $$;

-- A source document. Stored once under the hash of its bytes and never changed, so the
-- words a rule quotes can always be produced.
create table documents (
  tenant_id   text        not null,
  sha256      text        not null check (sha256 ~ '^[0-9a-f]{64}$'),
  file_name   text        not null,
  content     bytea       not null,
  uploaded_by text        not null,
  uploaded_at timestamptz not null,
  primary key (tenant_id, sha256)
);

-- One attempt to turn a document into a rule set.
create table ingestion_runs (
  id              text        primary key,
  tenant_id       text        not null,
  agreement_id    text        not null,
  document_sha256 text        not null,
  status          text        not null check (status in
                    ('started', 'awaiting_review', 'published', 'rejected', 'failed')),
  model           text,
  prompt_version  text,
  title           text,
  clauses         jsonb,
  failure         text,
  created_at      timestamptz not null,
  finished_at     timestamptz,
  foreign key (tenant_id, document_sha256) references documents (tenant_id, sha256)
);

-- A rule the model proposed, the problems the checks found with it, and what the reviewer decided.
create table proposed_rules (
  run_id      text        not null references ingestion_runs (id),
  rule_id     text        not null,
  tenant_id   text        not null,
  rule        jsonb       not null,
  also_from   jsonb       not null,
  problems    jsonb       not null,
  decision    text        not null default 'pending' check (decision in ('pending', 'approved', 'rejected')),
  decided_by  text,
  decided_at  timestamptz,
  primary key (run_id, rule_id),
  -- A rule the checks found a problem with cannot be approved as it stands.
  check (decision <> 'approved' or problems = '[]'::jsonb),
  check ((decision = 'pending') = (decided_by is null))
);

-- A person's approval of a run's rules as a whole. Only the API can write one, and only
-- on behalf of a signed-in person. Without it nothing is published.
create table approvals (
  run_id      text        primary key references ingestion_runs (id),
  tenant_id   text        not null,
  approved_by text        not null check (approved_by <> ''),
  approved_at timestamptz not null
);

-- A published rule set. Never changed and never removed; a new version replaces it.
create table rule_sets (
  tenant_id    text        not null,
  agreement_id text        not null,
  version      integer     not null check (version > 0),
  run_id       text        not null unique references ingestion_runs (id),
  rules        jsonb       not null,
  approved_by  text        not null,
  published_at timestamptz not null,
  primary key (tenant_id, agreement_id, version)
);

create function refuse_change() returns trigger
  language plpgsql
  as $$ begin raise exception '% rows are never changed or removed', tg_table_name; end $$;

create trigger documents_are_immutable before update or delete on documents
  for each row execute function refuse_change();
create trigger rule_sets_are_immutable before update or delete on rule_sets
  for each row execute function refuse_change();
create trigger approvals_are_immutable before update or delete on approvals
  for each row execute function refuse_change();
-- TRUNCATE is not a row change; refuse it too.
create trigger documents_no_truncate before truncate on documents execute function refuse_change();
create trigger rule_sets_no_truncate before truncate on rule_sets execute function refuse_change();
create trigger approvals_no_truncate before truncate on approvals execute function refuse_change();

-- Publishes a run's approved rules as the agreement's next version. This is the only way
-- a row reaches rule_sets: neither role may insert there. It refuses unless a person's
-- approval is on record, every rule has been decided, and at least one was approved.
create function publish_rule_set(p_run_id text, p_now timestamptz) returns integer
  language plpgsql security definer set search_path = public
  as $$
declare
  run      ingestion_runs;
  approver text;
  version_ integer;
  approved jsonb;
begin
  select * into run from ingestion_runs where id = p_run_id and tenant_id = current_tenant() for update;
  if not found then
    raise exception 'no such run';
  end if;
  -- Asked twice, it answers the same and publishes once.
  if run.status = 'published' then
    return (select version from rule_sets where run_id = p_run_id);
  end if;
  if run.status <> 'awaiting_review' then
    raise exception 'run % is %, not awaiting review', p_run_id, run.status;
  end if;
  select approved_by into approver from approvals where run_id = p_run_id;
  if approver is null then
    raise exception 'run % has not been approved by a person', p_run_id;
  end if;
  if exists (select from proposed_rules where run_id = p_run_id and decision = 'pending') then
    raise exception 'run % has rules nobody has decided on', p_run_id;
  end if;
  select jsonb_agg(rule order by rule_id) into approved
    from proposed_rules where run_id = p_run_id and decision = 'approved';
  if approved is null then
    raise exception 'run % has no approved rules', p_run_id;
  end if;

  select coalesce(max(version), 0) + 1 into version_
    from rule_sets where tenant_id = run.tenant_id and agreement_id = run.agreement_id;
  insert into rule_sets (tenant_id, agreement_id, version, run_id, rules, approved_by, published_at)
    values (run.tenant_id, run.agreement_id, version_, p_run_id, approved, approver, p_now);
  update ingestion_runs set status = 'published', finished_at = p_now where id = p_run_id;
  return version_;
end $$;

revoke all on function publish_rule_set(text, timestamptz) from public;
grant execute on function publish_rule_set(text, timestamptz) to overens_worker;

alter table documents      enable row level security;
alter table ingestion_runs enable row level security;
alter table proposed_rules enable row level security;
alter table approvals      enable row level security;
alter table rule_sets      enable row level security;

create policy tenant on documents      using (tenant_id = current_tenant()) with check (tenant_id = current_tenant());
create policy tenant on ingestion_runs using (tenant_id = current_tenant()) with check (tenant_id = current_tenant());
create policy tenant on proposed_rules using (tenant_id = current_tenant()) with check (tenant_id = current_tenant());
create policy tenant on approvals      using (tenant_id = current_tenant()) with check (tenant_id = current_tenant());
create policy tenant on rule_sets      using (tenant_id = current_tenant()) with check (tenant_id = current_tenant());

-- The API: takes uploads, starts runs, records decisions and approvals, reads everything.
grant select, insert on documents to overens_api;
grant select, insert on ingestion_runs to overens_api;
grant select on proposed_rules to overens_api;
grant update (decision, decided_by, decided_at) on proposed_rules to overens_api;
grant select, insert on approvals to overens_api;
grant select on rule_sets to overens_api;

-- The worker: reads documents, records what it read and what the model proposed, and
-- publishes through the function above. It cannot decide on a rule or approve a run.
grant select on documents to overens_worker;
grant select on ingestion_runs to overens_worker;
grant update (status, model, prompt_version, title, clauses, failure, finished_at) on ingestion_runs to overens_worker;
grant select, insert on proposed_rules to overens_worker;
grant select on approvals to overens_worker;
grant select on rule_sets to overens_worker;
