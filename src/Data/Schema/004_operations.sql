-- Starting a run is two steps: the row is stored, then the pipeline is told. If the second
-- fails, the row says so (dispatched_at is null) and the worker starts the run itself.
alter table ingestion_runs add column dispatched_at timestamptz;
grant update (dispatched_at) on ingestion_runs to overens_api;

-- The worker serves every company, so it finds undispatched runs through a function that
-- returns only what it needs to start them: which run, for which tenant.
create function runs_to_start(p_older_than timestamptz) returns table (tenant_id text, id text)
  language sql security definer set search_path = public
  as $$
    select tenant_id, id from ingestion_runs
     where status = 'started' and dispatched_at is null and created_at < p_older_than
     order by created_at limit 20
  $$;

create function mark_dispatched(p_run_id text, p_now timestamptz) returns void
  language sql security definer set search_path = public
  as $$ update ingestion_runs set dispatched_at = p_now where id = p_run_id and dispatched_at is null $$;

revoke all on function runs_to_start(timestamptz) from public;
revoke all on function mark_dispatched(text, timestamptz) from public;
grant execute on function runs_to_start(timestamptz) to overens_worker;
grant execute on function mark_dispatched(text, timestamptz) to overens_worker;

-- What reading an agreement cost, and which model in fact answered.
alter table ingestion_runs add column model_calls integer;
alter table ingestion_runs add column input_tokens bigint;
alter table ingestion_runs add column output_tokens bigint;
alter table ingestion_runs add column cost_micros bigint;
grant update (model_calls, input_tokens, output_tokens, cost_micros) on ingestion_runs to overens_worker;

-- What a company has spent on reading agreements since a moment, in millionths of a dollar.
-- The worker checks this against the company's daily budget before it calls a model.
create function tenant_spend_since(p_since timestamptz) returns bigint
  language sql stable
  as $$ select coalesce(sum(cost_micros), 0)::bigint from ingestion_runs where tenant_id = current_tenant() and created_at >= p_since $$;

-- Why a reviewer rejected a rule. A rejected rule with its reason is a case for the evals.
alter table proposed_rules add column reason text check (reason is null or length(reason) <= 500);
grant update (reason) on proposed_rules to overens_api;
