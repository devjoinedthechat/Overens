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
| Prompt caching | The agreement is sent six times per extraction. Caching would cut most of that cost | A cache breakpoint after the agreement, and a re-recording of the evals |
| Four-eyes approval | One specialist approves. Many payroll teams would want a second | A second approval row and a rule that it is another person |

## Built, with limits worth knowing

| Limit | Detail |
| --- | --- |
| Both models are at the ceiling | The synthetic documents cannot tell them apart, and say little about real agreements |
| Each model answered once per prompt | No measure of run-to-run variance. One rerun changed Haiku's citations enough to trip a check |
| The prompt met four of the documents | The extraction prompt was written while looking at the first four agreements. Six more were added after it was fixed |
| The reader knows two numbering conventions | Sections with "Stk." clauses, and bold numbered headings. A third would read as no clauses and fail the run |
| A split word's hyphen is guessed | A line ending in a hyphen is joined by a rule of thumb. It can be wrong |
| Questions use the newest version's text | The rules are joined across versions; the clauses searched are the newest upload's |
| The answer check is about provenance | It shows where each figure came from, not whether the answer addresses the question |
| Failover does not cover a provider outage | Both models are the same provider's |
| The audit log holds staff user names | And is kept when a company is erased |
| The console is in English | Agreements, findings and answers are in Danish |
