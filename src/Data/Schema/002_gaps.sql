-- What an extraction left uncovered: a kind of rule with no rule at all, or with days no
-- rule applies on. A missing rule is not a wrong rule, so nothing else would show it.
alter table ingestion_runs add column gaps jsonb;
grant update (gaps) on ingestion_runs to overens_worker;
