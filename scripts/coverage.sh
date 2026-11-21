#!/usr/bin/env bash
# Runs every test, adds up which lines of the system they ran, and fails below the floor.
# The build must be there already: scripts/check.sh calls this in place of a plain test run.
set -euo pipefail
cd "$(dirname "$0")/.."

# Line coverage of the system's own code, in percent. Raise it when the figure rises; never lower it to pass.
FLOOR=${COVERAGE_FLOOR:-94}
OUT=TestResults/coverage

rm -rf "$OUT"
dotnet tool restore >/dev/null
# The eval tests replay thousands of recorded answers through extraction. Counting lines makes
# that many times slower and adds nothing: the same code is run by the tests of each project.
# They are run plainly, every other test project is run with lines counted, and all at once.
runs=()
dotnet test tests/Evals.Tests --nologo --no-build &
runs+=($!)
for project in tests/*.Tests; do
  if [ "$project" != tests/Evals.Tests ]; then
    dotnet test "$project" --nologo --no-build --collect "XPlat Code Coverage" --results-directory "$OUT/raw" &
    runs+=($!)
  fi
done

failed=0
for run in "${runs[@]}"; do
  wait "$run" || failed=1
done
if [ "$failed" -ne 0 ]; then
  echo "A test failed. Coverage is not reported." >&2
  exit 1
fi

# What is measured is what runs in production. The generator of test documents, the eval
# runner, the tools and the local host are not, and neither is generated code.
dotnet reportgenerator \
  "-reports:$OUT/raw/**/coverage.cobertura.xml" \
  "-targetdir:$OUT" \
  "-reporttypes:TextSummary;MarkdownSummaryGithub;JsonSummary" \
  "-assemblyfilters:+Overens.Rules;+Overens.Documents;+Overens.Ingestion;+Overens.Agent;+Overens.Gateway;+Overens.Data;+Overens.Worker;+Overens.Api" \
  "-filefilters:-*.g.cs;-*/obj/*" >/dev/null

sed -n '1,/^$/p;/^Overens\./p' "$OUT/Summary.txt"
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  cat "$OUT/SummaryGithub.md" >> "$GITHUB_STEP_SUMMARY"
fi

covered=$(sed -n 's/.*"linecoverage": \([0-9.]*\).*/\1/p' "$OUT/Summary.json" | head -1)
if awk -v covered="$covered" -v floor="$FLOOR" 'BEGIN { exit !(covered < floor) }'; then
  echo "Line coverage is $covered pct., below the floor of $FLOOR." >&2
  exit 1
fi
echo "Line coverage $covered pct. (floor $FLOOR)."
