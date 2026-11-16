-- A run is open while it is being read or is waiting for review. Its pipeline can end
-- without the run being told: the step that ends a run failed every time it was tried, or
-- the scheduler lost the pipeline. Nothing then moves the run, and a reviewer's decision
-- goes nowhere. The worker looks at open runs in turn and ends those whose pipeline is gone.
alter table ingestion_runs add column checked_at timestamptz;

create index ingestion_runs_open on ingestion_runs (checked_at nulls first)
  where status in ('started', 'awaiting_review') and dispatched_at is not null;

-- Up to p_limit open runs that were handed to the pipeline before p_dispatched_before, the
-- ones looked at longest ago first. Each is stamped as looked at, so every open run gets
-- its turn however many there are. Like runs_to_start, it gives the worker only what it
-- needs: which run, for which tenant.
create function runs_to_check(p_dispatched_before timestamptz, p_now timestamptz, p_limit integer)
  returns table (tenant_id text, id text)
  language sql security definer set search_path = public
  as $$
    update ingestion_runs run set checked_at = p_now
     where run.id in (
       select open.id from ingestion_runs open
        where open.status in ('started', 'awaiting_review')
          and open.dispatched_at is not null and open.dispatched_at < p_dispatched_before
        order by open.checked_at nulls first, open.created_at
        limit p_limit
        for update skip locked)
    returning run.tenant_id, run.id
  $$;

revoke all on function runs_to_check(timestamptz, timestamptz, integer) from public;
grant execute on function runs_to_check(timestamptz, timestamptz, integer) to overens_worker;
