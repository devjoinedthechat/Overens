#!/usr/bin/env bash
# Everything CI checks, in the same order. Run it before pushing.
set -euo pipefail
cd "$(dirname "$0")/.."

scripts/check-secrets.sh --all
dotnet build --nologo -v quiet
dotnet format --verify-no-changes --no-restore
# With COVERAGE=1, as on CI, the tests also report which lines they ran, and a floor is held.
if [ "${COVERAGE:-}" = 1 ]; then
  scripts/coverage.sh
else
  dotnet test --nologo --no-build
fi
