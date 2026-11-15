-- 1. The audit log: one chain per company, and no tampering alarm from a gap in the numbers.
--
-- Entries were chained across all companies under one lock, so every audited write of
-- every company waited for every other. Each company now has its own chain and its own lock.
--
-- seq comes from a sequence, and a sequence gives a number to a transaction that later
-- rolls back. A gap in seq is therefore not a removed entry, and is no longer reported as
-- one. A removed or changed entry still breaks the hash of the entry after it.
create index audit_log_by_tenant on audit_log (tenant_id, seq desc);

create or replace function audit_append(p_tenant text, p_actor text, p_event text, p_subject text, p_detail jsonb)
  returns bigint language plpgsql security definer set search_path = public
  as $$
declare
  previous text;
  now_     timestamptz := clock_timestamp();
  added    bigint;
begin
  -- One writer at a time for each company, so its chain has one order.
  perform pg_advisory_xact_lock(7262027, hashtext(p_tenant));
  select hash into previous from audit_log where tenant_id = p_tenant order by seq desc limit 1;
  previous := coalesce(previous, '');
  insert into audit_log (tenant_id, at, actor, event, subject, detail, prev_hash, hash)
    values (p_tenant, now_, p_actor, p_event, p_subject, p_detail, previous,
            audit_hash(previous, p_tenant, now_, p_actor, p_event, p_subject, p_detail))
    returning seq into added;
  return added;
end $$;

-- The first entry whose hash or link is wrong, or null when every company's chain holds.
create or replace function audit_verify() returns bigint
  language plpgsql security definer set search_path = public
  as $$
declare
  entry    audit_log;
  previous text := '';
  tenant   text := null;
begin
  for entry in select * from audit_log order by tenant_id, seq loop
    if tenant is distinct from entry.tenant_id then
      tenant := entry.tenant_id;
      previous := '';
    end if;
    if entry.prev_hash <> previous
       or entry.hash <> audit_hash(entry.prev_hash, entry.tenant_id, entry.at, entry.actor, entry.event, entry.subject, entry.detail) then
      return entry.seq;
    end if;
    previous := entry.hash;
  end loop;
  return null;
end $$;

-- Entries written under the one chain are linked again, each company's to its own. This
-- is the only time hashes are rewritten, and it is done here, by the owner, in the
-- migration that changes what a link means.
alter table audit_log disable trigger audit_log_is_immutable;
do $$
declare
  entry    record;
  previous text := '';
  tenant   text := null;
begin
  for entry in select seq, tenant_id from audit_log order by tenant_id, seq loop
    if tenant is distinct from entry.tenant_id then
      tenant := entry.tenant_id;
      previous := '';
    end if;
    update audit_log
       set prev_hash = previous, hash = audit_hash(previous, tenant_id, at, actor, event, subject, detail)
     where seq = entry.seq
     returning hash into previous;
  end loop;
end $$;
alter table audit_log enable trigger audit_log_is_immutable;

-- 2. A rule, an approval and a rule set belong to a run of the same company. Row-level
-- security does not look at foreign keys, so the keys say it themselves.
alter table ingestion_runs add constraint ingestion_runs_tenant_id_id_key unique (tenant_id, id);
alter table proposed_rules drop constraint proposed_rules_run_id_fkey,
  add constraint proposed_rules_run_fkey foreign key (tenant_id, run_id) references ingestion_runs (tenant_id, id);
alter table approvals drop constraint approvals_run_id_fkey,
  add constraint approvals_run_fkey foreign key (tenant_id, run_id) references ingestion_runs (tenant_id, id);
alter table rule_sets drop constraint rule_sets_run_id_fkey,
  add constraint rule_sets_run_fkey foreign key (tenant_id, run_id) references ingestion_runs (tenant_id, id);

-- The function runs with its owner's rights, past row-level security, so it names the
-- company in every query of its own. Publishing also clears a note left by an earlier
-- attempt that was refused.
create or replace function publish_rule_set(p_run_id text, p_now timestamptz) returns integer
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
    return (select version from rule_sets where run_id = p_run_id and tenant_id = run.tenant_id);
  end if;
  if run.status <> 'awaiting_review' then
    raise exception 'run % is %, not awaiting review', p_run_id, run.status;
  end if;
  select approved_by into approver from approvals where run_id = p_run_id and tenant_id = run.tenant_id;
  if approver is null then
    raise exception 'run % has not been approved by a person', p_run_id;
  end if;
  if exists (select from proposed_rules where run_id = p_run_id and tenant_id = run.tenant_id and decision = 'pending') then
    raise exception 'run % has rules nobody has decided on', p_run_id;
  end if;
  select jsonb_agg(rule order by rule_id) into approved
    from proposed_rules where run_id = p_run_id and tenant_id = run.tenant_id and decision = 'approved';
  if approved is null then
    raise exception 'run % has no approved rules', p_run_id;
  end if;

  select coalesce(max(version), 0) + 1 into version_
    from rule_sets where tenant_id = run.tenant_id and agreement_id = run.agreement_id;
  insert into rule_sets (tenant_id, agreement_id, version, run_id, rules, approved_by, published_at)
    values (run.tenant_id, run.agreement_id, version_, p_run_id, approved, approver, p_now);
  update ingestion_runs set status = 'published', finished_at = p_now, failure = null
   where id = p_run_id and tenant_id = run.tenant_id;
  return version_;
end $$;

-- 3. Indexes for what is asked often: the worker's look for runs to start, a company's
-- runs newest first, and its rejected rules newest first.
create index ingestion_runs_to_start on ingestion_runs (created_at) where status = 'started' and dispatched_at is null;
create index ingestion_runs_by_tenant on ingestion_runs (tenant_id, created_at desc);
create index proposed_rules_rejected on proposed_rules (tenant_id, decided_at desc) where decision = 'rejected';

-- 4. What a company's questions and explanations have cost, by day. The API adds to it
-- after every answer and checks it before the next, so one company cannot spend what is
-- meant for all, and the count survives a restart. It holds sums only: no question, no pay.
create table model_spend (
  tenant_id   text    not null,
  day         date    not null,
  calls       integer not null default 0 check (calls >= 0),
  cost_micros bigint  not null default 0 check (cost_micros >= 0),
  primary key (tenant_id, day)
);
alter table model_spend enable row level security;
create policy tenant on model_spend using (tenant_id = current_tenant());
grant select on model_spend to overens_api;

-- The only way a row here changes: the API adds to its own company's day.
create function note_model_spend(p_day date, p_calls integer, p_micros bigint) returns bigint
  language plpgsql security definer set search_path = public
  as $$
declare
  total bigint;
begin
  if current_tenant() is null then
    raise exception 'spend needs a tenant';
  end if;
  if p_calls < 0 or p_micros < 0 then
    raise exception 'spend is only ever added';
  end if;
  insert into model_spend (tenant_id, day, calls, cost_micros)
    values (current_tenant(), p_day, p_calls, p_micros)
    on conflict (tenant_id, day) do update
      set calls = model_spend.calls + excluded.calls, cost_micros = model_spend.cost_micros + excluded.cost_micros
    returning cost_micros into total;
  return total;
end $$;

revoke all on function note_model_spend(date, integer, bigint) from public;
grant execute on function note_model_spend(date, integer, bigint) to overens_api;

-- Erasing a company removes its spend too.
create or replace function erase_tenant(p_tenant text) returns jsonb
  language plpgsql security definer set search_path = public
  as $$
declare
  counts jsonb;
  n_documents integer; n_runs integer; n_rules integer; n_sets integer;
begin
  if p_tenant is null or p_tenant = '' then
    raise exception 'which tenant?';
  end if;
  perform set_config('app.erasing', p_tenant, true);
  delete from rule_sets where tenant_id = p_tenant;
  get diagnostics n_sets = row_count;
  delete from approvals where tenant_id = p_tenant;
  delete from proposed_rules where tenant_id = p_tenant;
  get diagnostics n_rules = row_count;
  delete from ingestion_runs where tenant_id = p_tenant;
  get diagnostics n_runs = row_count;
  delete from documents where tenant_id = p_tenant;
  get diagnostics n_documents = row_count;
  delete from model_spend where tenant_id = p_tenant;
  perform set_config('app.erasing', '', true);
  counts := jsonb_build_object('documents', n_documents, 'runs', n_runs, 'proposed_rules', n_rules, 'rule_sets', n_sets);
  -- The log keeps that the company was here and was erased. It never held its documents or its pay data.
  perform audit_append(p_tenant, 'owner', 'tenant_erased', p_tenant, counts);
  return counts;
end $$;
