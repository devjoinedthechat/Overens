## What changes, and why

## How it is tested

- [ ] A test that fails without the change
- [ ] `scripts/check.sh` passes, and `COVERAGE=1 scripts/check.sh` if tests or `src` changed
- [ ] `scripts/smoke.sh` passes if the API, the worker, the schema or the console changed
- [ ] `scripts/mutation.sh` passes if `src/Rules` changed

## If a model's answers are involved

- [ ] Nothing a model says is used without a check by code or a person's approval
- [ ] A changed prompt has a version of its own, with recordings and a results file
- [ ] The eval report and the README give the results after the change, including what got worse
