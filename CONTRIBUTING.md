# Contributing

## Before you push

Run the same checks CI runs:

```sh
scripts/check.sh    # secrets scan, build, format, tests
scripts/smoke.sh    # the whole system, from nothing
```

Both need Docker. The smoke test also needs Google Chrome, or `SKIP_BROWSER=1`.

Turn on the commit hooks once per clone, so the secrets scan runs before every commit:

```sh
git config core.hooksPath .githooks
```

## What a change needs

- **A test that fails without it.** A fix with no test is a fix until the next change.
- **No added trust in a model.** What a model says is checked by code or approved by a person before it is used. A change that lets a model's answer through unchecked will not be merged.
- **Synthetic data only.** Test agreements come from the generator in `src/Synthetic`. Do not add real agreements, payslips or names.
- **A migration, not an edit.** The schema changes by adding a numbered file in `src/Data/Schema`. Files already there are not changed.

## Prompts and evals

A change to a prompt gets its own prompt version, recordings and results file:

```sh
dotnet run --project evals/Runner -- --record --save --cap 2
```

Recording needs `ANTHROPIC_API_KEY` and spends up to the cap. Everything else replays the recordings and costs nothing.
