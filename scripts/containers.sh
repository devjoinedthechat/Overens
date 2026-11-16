#!/usr/bin/env bash
# Builds the three images the system is deployed as, with the .NET SDK and no Dockerfile.
#
#   scripts/containers.sh build              images for this machine, tagged latest
#   scripts/containers.sh build linux-x64    images for another platform, as a deployment needs
#   scripts/containers.sh up | down          start or stop them with stand-ins for what Azure provides
set -euo pipefail
cd "$(dirname "$0")/.."

compose() { docker compose -f infra/compose/compose.yml "$@"; }

case "${1:-}" in
  build)
    for project in src/Api src/Worker tools/Migrate; do
      dotnet publish "$project" -c Release -t:PublishContainer --nologo -v quiet ${2:+-p:ContainerRuntimeIdentifier=$2}
    done
    ;;
  up) compose up -d ;;
  down) compose down -v ;;
  *) echo "Usage: scripts/containers.sh build [platform] | up | down"; exit 2 ;;
esac
