# What was left out, and why

Things a production system of this kind would have, that this one does not. Each is a decision, not an oversight.

## Not built

| Left out | Why | What it would take |
| --- | --- | --- |
| A real collective agreement | A published agreement is its parties' copyrighted text. None is downloaded, read or sent to a model | A customer's own agreement, with their permission |
| Editing a proposed rule | A reviewer approves or rejects. An edited rule would need its evidence checked again, and a record of who changed what | An edit endpoint that reruns the checks and logs the change |
| More than six kinds of rule | Enough to show the method. Real agreements also set monthly salaries, public-holiday pay, shift work, on-call pay and rates for the young | A rule record, an engine branch and an extraction schema for each |
| Tax and statutory deductions | They come from law, not from the agreement | A different product |
| Scanned agreements | The local reader needs text in the PDF and ruled tables | Azure Document Intelligence behind the same interface |
| Stored pay data and findings | Personal data not held cannot leak | A retention policy first |
| A second model provider | One prompt and one eval to keep honest. Failover is between one provider's models | Evals passed on the second provider's models |
| Vector search | An agreement is a few dozen clauses; keyword ranking finds them. Anthropic has no embedding model | A measured gap in retrieval |
| Prompt caching | Tried and removed. A kept prompt is read only when the answer schema is the same, and each kind of rule has its own | Schemas small enough to share, or one call for all kinds measured against six |
| Four-eyes approval | One specialist approves. Many payroll teams would want a second | A second approval row and a rule that it is another person |

## Built, with limits worth knowing

| Limit | Detail |
| --- | --- |
| Both models are at the ceiling | The synthetic documents cannot tell them apart, and say little about real agreements |
| A few passes per model | Extraction repeated three times for Sonnet, five for Haiku, through Claude Code and not the API. Only Haiku varied |
| Three ways of stating a figure have no answer | A step added per seniority band, a share of a total, a change in percentage points. Sonnet calculates and is flagged; Haiku drops the rule or cuts it short |
| Dates and lengths in words | "den første januar 2026" and "første og anden time" are right and flagged |
| The prompt met four of the documents | The extraction prompt was written while looking at the first four agreements. Six more were added after it was fixed |
| The reader knows two numbering conventions | Sections with "Stk." clauses, and bold numbered headings. A third would read as no clauses and fail the run |
| A split word's hyphen is guessed | A line ending in a hyphen is joined by a rule of thumb. It can be wrong |
| Questions use the newest version's text | The rules are joined across versions; the clauses searched are the newest upload's |
| The rule checks are about where a figure is printed | Figures exchanged within one sentence, and a part of a rule left out, pass. Measured: 264 of 2,363 planted mistakes |
| "One" is barely checked | A count of one is accepted as "en", "et" or "første", and those words are everywhere |
| The answer check is about provenance | It shows where each figure came from, not whether the answer addresses the question |
| Failover does not cover a provider outage | Both models are the same provider's |
| The audit log holds staff user names | And is kept when a company is erased |
| The console is in English | Agreements, findings and answers are in Danish |
