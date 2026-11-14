#!/usr/bin/env bash
# Everything CI checks, in the same order. Run it before pushing.
set -euo pipefail
cd "$(dirname "$0")/.."

scripts/check-secrets.sh --all
dotnet build --nologo -v quiet
dotnet format --verify-no-changes --no-restore
dotnet test --nologo --no-build
