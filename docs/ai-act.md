# The EU AI Act and this system

A note on whether a tool that checks payslips against collective agreements is "high-risk" under the EU AI Act, and what follows from the design either way.

This is an engineer's reading of secondary sources, not legal advice. The Commission's guidelines on high-risk classification were still in draft when it was written.

## The question

Annex III of the Act lists areas where an AI system is high-risk. Point 4 is employment, workers' management and access to self-employment. Commentary on the Act and on the Commission's draft guidelines describes it as covering two things: recruitment and selection; and decisions on promotion and termination, allocation of tasks, and monitoring and evaluating performance.

A system in a listed area is not automatically high-risk. Article 6(3) exempts one that, among other things, performs a narrow procedural task or a task preparatory to an assessment, provided it does not profile natural persons.

## Where this system stands

| Question | Here |
| --- | --- |
| Does it decide anything about a worker? | No. It compares what an employer paid with what an agreement requires |
| Does it evaluate a worker's behaviour or performance? | No. Its inputs are hours and pay lines |
| Does it profile a person? | No. An employee is an opaque id; nothing about a person is kept |
| Who acts on its output? | The employer or payroll provider, to correct pay |
| Where is the AI? | Reading the agreement into rules a person approves, wording findings, and answering questions |
| Where is it not? | The pay decision, which is arithmetic with no model in it |

On that reading it falls outside point 4: it is a compliance check on the employer, not a decision about the worker. If a regulator took the wider view that anything touching terms of employment is in scope, the reading of the agreement would be the part to assess, and it looks like a preparatory task with a person's approval after it.

That is an argument, not a ruling. A provider relying on the Article 6(3) exemption has to document the assessment before putting the system into service.

## What the design already has

The obligations for a high-risk system are, in outline, risk management, data governance, technical documentation, record-keeping, transparency, human oversight, and accuracy and robustness. Without claiming compliance:

| Obligation | What exists |
| --- | --- |
| Human oversight | A person approves every rule before it is used; the database refuses to publish without it |
| Record-keeping | An audit log written by the database, hash-chained; each run records its model and prompt version |
| Accuracy and robustness | Evals with known answers, a release gate, hostile documents, failure analysis in the [eval report](eval-report/README.md) |
| Transparency | Every rule shown beside the clause it quotes; every answer cites its clause; "not in the agreement" said as such |
| Risk management | A [threat model](threat-model.md) with a test for each control |
| Data governance | Pay data not stored; CPR numbers removed before any model call |

## Timing

The high-risk obligations for Annex III systems were due to apply from 2 August 2026. According to commentary on the Digital Omnibus on AI, approved by the Council in June 2026, that date has moved to 2 December 2027. Check the Official Journal before relying on either date.

## Sources

- [CDT: EU AI Act brief, AI at work](https://cdt.org/insights/eu-ai-act-brief-pt-4-ai-at-work/)
- [European Commission AI Act service desk: employment](https://ai-act-service-desk.ec.europa.eu/en/employment-0)
- [DLA Piper: Commission draft guidelines on high-risk AI in employment](https://knowledge.dlapiper.com/dlapiperknowledge/globalemploymentlatestdevelopments/2026/eu-commission-publishes-draft-guidelines-on-high-risk-ai-in-employment)
- [McCann FitzGerald: draft guidelines on high-risk AI classification](https://www.mccannfitzgerald.com/knowledge/data-privacy-and-cyber-risk/employment-spotlight-eu-ai-act-draft-guidelines-on-high-risk-ai-classification)
- [Cloud Security Alliance: high-risk deadline moved to December 2027](https://labs.cloudsecurityalliance.org/research/csa-research-note-eu-ai-act-omnibus-vii-deadline-delay-20260/)
- [Leaseurope: application dates for high-risk AI system rules](https://www.leaseurope.org/new-application-dates-high-risk-ai-system-rules)
