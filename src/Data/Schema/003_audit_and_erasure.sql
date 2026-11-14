-- An audit log that is written by the database itself, in the same transaction as the
-- change it records, so no code path can make a change and forget to log it.
--
-- Each entry carries the hash of the one before it. Changing or removing an entry breaks
-- every hash after it, which audit_verify() finds.
--
-- Entries say who did what to which run or agreement. They hold no pay data and nothing
-- about an employee.
create table audit_log (
  seq       bigint      generated always as identity primary key,
  tenant_id text        not null,
  at        timestamptz not null,
  actor     text        not null,
  event     text        not null,
  subject   text        not null,
  detail    jsonb       not null,
  prev_hash text        not null,
  hash      text        not null
);

create function audit_hash(p_prev text, p_tenant text, p_at timestamptz, p_actor text, p_event text, p_subject text, p_detail jsonb)
  returns text language sql immutable
  as $$
    select encode(sha256(convert_to(
      p_prev || '|' || p_tenant || '|' || to_char(p_at at time zone 'utc', 'YYYY-MM-DD"T"HH24:MI:SS.US') || '|' ||
      p_actor || '|' || p_event || '|' || p_subject || '|' || p_detail::text, 'UTF8')), 'hex')
  $$;

create function audit_append(p_tenant text, p_actor text, p_event text, p_subject text, p_detail jsonb)
  returns bigint language plpgsql security definer set search_path = public
  as $$
declare
  previous text;
  now_     timestamptz := clock_timestamp();
  added    bigint;
begin
  -- One writer at a time, so the chain has one order.
  perform pg_advisory_xact_lock(7262027);
  select hash into previous from audit_log order by seq desc limit 1;
  previous := coalesce(previous, '');
  insert into audit_log (tenant_id, at, actor, event, subject, detail, prev_hash, hash)
    values (p_tenant, now_, p_actor, p_event, p_subject, p_detail, previous,
            audit_hash(previous, p_tenant, now_, p_actor, p_event, p_subject, p_detail))
    returning seq into added;
  return added;
end $$;

-- The first entry whose hash or link is wrong, or null when the whole chain holds.
create function audit_verify() returns bigint
  language plpgsql security definer set search_path = public
  as $$
declare
  entry    audit_log;
  previous text := '';
  expected bigint := null;
begin
  for entry in select * from audit_log order by seq loop
    if entry.prev_hash <> previous
       or entry.hash <> audit_hash(entry.prev_hash, entry.tenant_id, entry.at, entry.actor, entry.event, entry.subject, entry.detail)
       or (expected is not null and entry.seq <> expected) then
      return entry.seq;
    end if;
    previous := entry.hash;
    expected := entry.seq + 1;
  end loop;
  return null;
end $$;

-- The API records two things itself, which change no row: a pay check and a question.
-- It may only record them for the tenant it is acting for.
create function audit_note(p_event text, p_subject text, p_detail jsonb) returns bigint
  language plpgsql security definer set search_path = public
  as $$
begin
  if p_event not in ('pay_checked', 'question_asked') then
    raise exception 'event % cannot be noted', p_event;
  end if;
  if current_tenant() is null or nullif(current_setting('app.actor', true), '') is null then
    raise exception 'a note needs a tenant and an actor';
  end if;
  return audit_append(current_tenant(), current_setting('app.actor', true), p_event, p_subject, p_detail);
end $$;

revoke all on function audit_append(text, text, text, text, jsonb) from public;
revoke all on function audit_verify() from public;
revoke all on function audit_note(text, text, jsonb) from public;
grant execute on function audit_note(text, text, jsonb) to overens_api;

create trigger audit_log_is_immutable before update or delete on audit_log
  for each row execute function refuse_change();
create trigger audit_log_no_truncate before truncate on audit_log execute function refuse_change();

alter table audit_log enable row level security;
create policy tenant on audit_log using (tenant_id = current_tenant());
grant select on audit_log to overens_api;

create function audit_document() returns trigger language plpgsql security definer set search_path = public as $$
begin
  perform audit_append(new.tenant_id, new.uploaded_by, 'document_stored', new.sha256, jsonb_build_object('bytes', length(new.content)));
  return new;
end $$;

create function audit_run() returns trigger language plpgsql security definer set search_path = public as $$
begin
  if tg_op = 'INSERT' then
    perform audit_append(new.tenant_id, coalesce(nullif(current_setting('app.actor', true), ''), 'unknown'), 'run_started', new.id,
      jsonb_build_object('agreement', new.agreement_id, 'document', new.document_sha256));
  elsif new.status is distinct from old.status then
    perform audit_append(new.tenant_id, 'pipeline', 'run_' || new.status, new.id,
      jsonb_strip_nulls(jsonb_build_object('agreement', new.agreement_id, 'model', new.model, 'prompt', new.prompt_version, 'failure', new.failure)));
  end if;
  return new;
end $$;

create function audit_decision() returns trigger language plpgsql security definer set search_path = public as $$
begin
  if new.decision is distinct from old.decision then
    perform audit_append(new.tenant_id, new.decided_by, 'rule_' || new.decision, new.run_id, jsonb_build_object('rule', new.rule_id));
  end if;
  return new;
end $$;

create function audit_approval() returns trigger language plpgsql security definer set search_path = public as $$
begin
  perform audit_append(new.tenant_id, new.approved_by, 'run_approved', new.run_id, '{}'::jsonb);
  return new;
end $$;

create function audit_rule_set() returns trigger language plpgsql security definer set search_path = public as $$
begin
  perform audit_append(new.tenant_id, new.approved_by, 'rule_set_published', new.run_id,
    jsonb_build_object('agreement', new.agreement_id, 'version', new.version, 'rules', jsonb_array_length(new.rules)));
  return new;
end $$;

create trigger audit after insert on documents for each row execute function audit_document();
create trigger audit after insert or update on ingestion_runs for each row execute function audit_run();
create trigger audit after update on proposed_rules for each row execute function audit_decision();
create trigger audit after insert on approvals for each row execute function audit_approval();
create trigger audit after insert on rule_sets for each row execute function audit_rule_set();

-- Erasing a company. Rows that are never changed or removed can be removed in one case
-- only: by erase_tenant, for that tenant. Only the database's owner can run it; neither
-- run-time role can, and neither has the right to delete a row at all.
create or replace function refuse_change() returns trigger
  language plpgsql
  as $$
begin
  if tg_op = 'DELETE' and tg_table_name <> 'audit_log'
     and old.tenant_id = nullif(current_setting('app.erasing', true), '') then
    return old;
  end if;
  raise exception '% rows are never changed or removed', tg_table_name;
end $$;

-- This trigger function returned NEW for a delete, which is null, and a null from a row
-- trigger skips the row without a word. A delete must be let through or refused, never skipped.
create or replace function refuse_decision_after_approval() returns trigger
  language plpgsql
  as $$
begin
  if exists (select from approvals where run_id = old.run_id) then
    raise exception 'run % is approved; its decisions can no longer change', old.run_id;
  end if;
  if tg_op = 'DELETE' then
    return old;
  end if;
  return new;
end $$;

create function erase_tenant(p_tenant text) returns jsonb
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
  perform set_config('app.erasing', '', true);
  counts := jsonb_build_object('documents', n_documents, 'runs', n_runs, 'proposed_rules', n_rules, 'rule_sets', n_sets);
  -- The log keeps that the company was here and was erased. It never held its documents or its pay data.
  perform audit_append(p_tenant, 'owner', 'tenant_erased', p_tenant, counts);
  return counts;
end $$;

revoke all on function erase_tenant(text) from public;
