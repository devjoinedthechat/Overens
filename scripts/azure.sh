#!/usr/bin/env bash
# Deploys Overens to Azure, and removes it again.
#
#   scripts/azure.sh check       compile and lint the infrastructure file. Needs no account.
#   scripts/azure.sh deploy      create everything in one resource group, push the images, apply the schema
#   scripts/azure.sh smoke       ask the deployed API whether it is alive and ready
#   scripts/azure.sh teardown    delete the resource group and everything in it
#
# deploy, smoke and teardown need the Azure CLI, signed in (az login), and these settings:
#
#   AZURE_RESOURCE_GROUP   the group to create, for example overens-demo
#   AZURE_LOCATION         where, for example swedencentral
#   AUTH_AUTHORITY         https://login.microsoftonline.com/organizations/v2.0
#   AUTH_AUDIENCE          the API's application ID URI or client id
#   AUTH_WEB_CLIENT        the console's client id
#   AUTH_SCOPE             what the console asks for, for example api://overens-api/access
#   AUTH_ALLOWED_TENANTS   optional: the directories that may sign in, separated by commas
#   ANTHROPIC_API_KEY      optional: without it no agreement can be read
#
# The three database passwords are made here, handed to the deployment and kept only in the
# Key Vault it creates. They are never written to a file or printed.
set -euo pipefail
cd "$(dirname "$0")/.."

template=infra/azure/main.bicep
need() { for name in "$@"; do [ -n "${!name:-}" ] || { echo "The setting $name is missing. See the top of this script."; exit 2; }; done; }
secret() { openssl rand -base64 36 | tr -dc 'A-Za-z0-9' | cut -c1-32; }

case "${1:-}" in
  check)
    # "az bicep build" fails on an error and prints warnings. A warning is treated as a failure here.
    out=$(az bicep build --file "$template" --stdout 2>&1 >/dev/null) || { echo "$out"; exit 1; }
    if [ -n "$out" ]; then echo "$out"; echo "The infrastructure file has warnings."; exit 1; fi
    echo "The infrastructure file compiles with no warnings."
    ;;

  deploy)
    need AZURE_RESOURCE_GROUP AZURE_LOCATION AUTH_AUTHORITY AUTH_AUDIENCE AUTH_WEB_CLIENT AUTH_SCOPE
    tenants=$(python3 -c "import json,os; print(json.dumps([t for t in os.environ.get('AUTH_ALLOWED_TENANTS','').split(',') if t]))")
    owner=$(secret); api=$(secret); worker=$(secret)
    az group create --name "$AZURE_RESOURCE_GROUP" --location "$AZURE_LOCATION" --output none
    pass() {
      az deployment group create --resource-group "$AZURE_RESOURCE_GROUP" --name "overens-$1" --template-file "$template" --output none \
        --parameters deployApps="$1" ownerPassword="$owner" apiPassword="$api" workerPassword="$worker" modelKey="${ANTHROPIC_API_KEY:-}" \
          authAuthority="$AUTH_AUTHORITY" authAudience="$AUTH_AUDIENCE" authWebClient="$AUTH_WEB_CLIENT" authScope="$AUTH_SCOPE" authAllowedTenants="$tenants"
    }
    value() { az deployment group show --resource-group "$AZURE_RESOURCE_GROUP" --name "overens-$1" --query "properties.outputs.$2.value" --output tsv; }

    echo "Creating the network, database, vault, registry and scheduler"
    pass false
    registry=$(value false registry)

    echo "Building and pushing the images to $registry"
    az acr login --name "${registry%%.*}"
    for project in src/Api src/Worker tools/Migrate; do
      dotnet publish "$project" -c Release -t:PublishContainer --nologo -v quiet -p:ContainerRuntimeIdentifier=linux-x64 -p:ContainerRegistry="$registry"
    done

    echo "Creating the apps"
    pass true

    echo "Applying the database schema"
    job=$(value true migrateJob)
    run=$(az containerapp job start --resource-group "$AZURE_RESOURCE_GROUP" --name "$job" --query name --output tsv)
    for _ in $(seq 1 60); do
      state=$(az containerapp job execution show --resource-group "$AZURE_RESOURCE_GROUP" --name "$job" --job-execution-name "$run" --query properties.status --output tsv)
      [ "$state" = Running ] || break
      sleep 5
    done
    [ "$state" = Succeeded ] || { echo "The schema job ended as $state."; exit 1; }

    echo "Deployed: $(value true address)/app/"
    ;;

  smoke)
    need AZURE_RESOURCE_GROUP
    address=$(az deployment group show --resource-group "$AZURE_RESOURCE_GROUP" --name overens-true --query properties.outputs.address.value --output tsv)
    for path in /alive /health /openapi/v1.json /app/; do
      echo "$(curl -s -o /dev/null -w '%{http_code}' "$address$path")  $path"
    done
    echo "401 is the right answer here: $(curl -s -o /dev/null -w '%{http_code}' "$address/runs")  /runs without a token"
    ;;

  teardown)
    need AZURE_RESOURCE_GROUP
    az group delete --name "$AZURE_RESOURCE_GROUP" --yes
    echo "Deleted. The Key Vault is kept in a deleted state for seven days; purge it to free its name."
    ;;

  *)
    sed -n '2,7p' "$0" | sed 's/^# \{0,1\}//'
    exit 2
    ;;
esac
