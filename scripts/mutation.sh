#!/usr/bin/env bash
# Mutation testing of the rule engine: the code that does the arithmetic is changed one small
# way at a time (a < for a <=, a + for a -), and the tests are run against each change. A change
# no test notices is a gap in the tests. Fails below the score in tests/Rules.Tests/stryker-config.json.
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet tool restore >/dev/null
cd tests/Rules.Tests
dotnet stryker --output "$PWD/../../TestResults/mutation"
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  cat ../../TestResults/mutation/reports/mutation-report.md >> "$GITHUB_STEP_SUMMARY"
fi
