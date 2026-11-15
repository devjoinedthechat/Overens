# Threat model

What could go wrong, who could make it go wrong, and what stands in the way. Each control names the test that tries to get past it.

## What is being protected

| Asset | Why it matters |
| --- | --- |
| Published rule sets | They decide whether a payslip is right. A wrong one misleads every check made with it |
| Pay data | Salaries and hours of people who are not users of the system |
| A company's agreements and rules | One company must not see another's |
| The audit log | The record of who approved what |
| The model budget | Someone else's money |

## Who is trusted with what

| Party | Trusted to | Not trusted to |
| --- | --- | --- |
| The model | Read a clause and propose what it says; word a finding or an answer | Calculate, decide, publish, or be right |
| An agreement's text | Be the thing that is read | Give instructions |
| A specialist | Approve or reject rules for their own company | See another company's data |
| An employer | Check pay and ask questions for their own company | Upload, review or approve |
| The worker | Store what was read and proposed; publish what a person approved | Approve, decide, or write a rule set directly |
| The API | Record people's decisions and approvals | Publish |
| The database's owner | Migrate; erase a company | Nothing is hidden from the owner; tampering is detectable, not prevented |

## Threats and controls

### The model is wrong

| Threat | Control | Tried by |
| --- | --- | --- |
| Misreads or invents a value | Clause exists, quote word for word, numbers and dates printed where cited | `ExtractionTests` |
| Leaves a rule out | Gaps reported per kind of rule and period | `A_rule_the_model_left_out_is_reported_as_a_gap` |
| Chooses between contradicting clauses | Told not to; both proposed; conflict reported; approving both refused | `GateTests`, `Two_rules_that_conflict_cannot_both_be_approved...` |
| Does arithmetic | It has no way to return a sum that is accepted: rates as printed, increases as printed, amounts from the calculator | `A_rate_given_as_an_increase...`, `An_amount_must_come_from_the_calculator...` |
| States a number of its own in an answer or an explanation | Answer check and explanation guard; fixed wording otherwise | `QuestionAgentTests`, `FindingExplainerTests` |
| Answers what the agreement does not say | Fixed "not in the agreement" wording; measured on unanswerable questions | `GateTests` |

**Residual:** a value printed in the cited clause that belongs to something else passes every check. The reviewer, shown the rule beside its clause, is the only guard.

### The document is hostile

| Threat | Control | Tried by |
| --- | --- | --- |
| Instructions planted in a clause, a table or a fake protocol | The agreement is passed as data and declared so; both models ignored five planted passages | `Instructions_planted_in_an_agreement_change_nothing...` |
| A planted figure in the clause a rule quotes | None mechanical. Shown to the reviewer beside the rule | Stated in the eval report |
| A damaged, encrypted or enormous file | Fails the run at once with a reason; never reaches the model; 20 MB and 500 pages | `A_damaged_pdf_fails_the_run_at_once...` |
| Markup in what is displayed | Everything rendered as text; a content policy that allows only the console's own scripts | `UiCheck`, `The_console_is_served_with_a_policy...` |

### A person or a service oversteps

| Threat | Control | Tried by |
| --- | --- | --- |
| Publishing without approval | Only one database function publishes, and it requires an approval row | `Nothing_is_published_without_a_persons_approval` |
| The pipeline approving its own work | The worker's role cannot write approvals or decisions | Same test |
| Swapping a decision after approval | Decisions frozen by trigger once approved | `A_decision_cannot_change_once_the_run_is_approved` |
| Changing a published rule set | No grant; triggers stop the owner too | `A_published_rule_set...cannot_be_changed_or_removed` |
| Reading another company's data | Row-level security on every table; no query without a tenant | `One_tenant_cannot_see_or_touch_anothers_rows`, `One_company_cannot_see_or_decide_on_anothers_run` |
| An employer doing a specialist's work | Role checked on every route | `Pay_is_checked_only_for_ones_own_company...` |
| A forged, expired or misdirected token | Signature, issuer, audience and lifetime checked | `Nobody_gets_in_without_a_valid_token...` |
| Editing the audit log | No grant; triggers; hash chain | `The_log_cannot_be_written_edited_or_emptied...` |

### Personal data leaks

| Threat | Control | Tried by |
| --- | --- | --- |
| A CPR number sent to the model | Removed from every model call next to the provider | `Nothing_cpr_shaped_reaches_the_model...` |
| A CPR number as an employee id | Refused | `Pay_is_checked...never_under_a_cpr_number` |
| Pay data kept | Not stored; the log holds counts only | `An_employer_checks_payslips...` |
| Content in telemetry | Attributes from a fixed list; non-id text withheld | `A_runs_spans_are_one_trace_and_say_nothing...` |
| A company's data outliving the company | `erase_tenant`, owner only, scoped to that tenant | `ErasureTests` |

### Cost and availability

| Threat | Control | Tried by |
| --- | --- | --- |
| One company exhausting the budget | Daily budget per company, checked before a model call; uploads and questions rate-limited | `What_a_run_cost_is_recorded...`, `One_company_cannot_use_up_the_pipeline...` |
| Runaway spend overall | A cap on each process's spend; unpriced models refused | `Each_call_is_priced...` |
| A model that cannot be reached | The provider's other model answers; the run records which | `When_the_model_asked_for_is_down...` |
| A worker dying mid-run | The orchestration resumes; steps are safe to repeat | `A_run_survives_the_worker_dying...` |
| An upload stored and never started | The worker finds and starts it | `A_run_that_was_stored_and_never_started...` |
| An agent that never finishes | Ten turns; one resend of a refused answer | `A_model_that_never_answers_is_stopped...` |

## Not defended against

- **The database's owner.** They can read everything and erase a company. An edit to the audit log is detectable only if the chain is verified by someone else.
- **The model provider.** Agreement text and findings are sent to it. A provider outage stops extraction and questions.
- **A reviewer who approves without reading.** The system puts the evidence beside the rule. It cannot make anyone look.
- **Development settings in the local stack.** Plain HTTP, fixed demo passwords and a default Keycloak admin. They are for one machine.
