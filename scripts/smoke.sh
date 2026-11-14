#!/usr/bin/env bash
# Starts the whole system from nothing and takes one agreement all the way through it, as a
# person would: sign in at Keycloak, upload the PDF, review the proposed rules, approve.
# It needs no model key: the worker replays the answers recorded for this agreement.
#
#   scripts/smoke.sh          start, check, stop
#   KEEP=1 scripts/smoke.sh   leave the system running afterwards
#   SKIP_BROWSER=1            leave out the part that needs Google Chrome
#   SHOTS=dir                 save screenshots of the console there
set -euo pipefail
cd "$(dirname "$0")/.."

api=http://localhost:5058
keycloak=http://localhost:8081/realms/overens
log=$(mktemp)
failed=0
ok() { echo "  ok    $1"; }
bad() { echo "  FAIL  $1"; failed=1; }
check() { if [ "$2" = "$3" ]; then ok "$1"; else bad "$1 (expected $3, got $2)"; fi; }
json() { python3 -c "import json,sys; d=json.load(sys.stdin); print($1)"; }

ANTHROPIC_API_KEY= dotnet run --project src/AppHost >"$log" 2>&1 &
host=$!
stop() {
  if [ -z "${KEEP:-}" ]; then
    kill "$host" 2>/dev/null || true
    wait "$host" 2>/dev/null || true
  else
    echo "Left running. Dashboard: http://localhost:15034  API: $api  Stop it with: kill $host"
  fi
  if [ "$failed" -ne 0 ]; then
    echo; echo "The host's log:"; tail -n 60 "$log"
  fi
  rm -f "$log"
}
trap stop EXIT

echo "Starting the system"
for _ in $(seq 1 150); do
  if curl -sf "$api/health" >/dev/null 2>&1 && curl -sf "$keycloak/.well-known/openid-configuration" >/dev/null 2>&1; then break; fi
  if ! kill -0 "$host" 2>/dev/null; then bad "the host stopped"; exit 1; fi
  sleep 2
done
check "the API is up" "$(curl -s -o /dev/null -w '%{http_code}' "$api/health")" 200

token() {
  curl -s "$keycloak/protocol/openid-connect/token" \
    -d client_id=overens-web -d grant_type=password -d "username=$1" -d password=local-demo-password | json "d['access_token']"
}
call() { curl -s -H "Authorization: Bearer $1" "${@:2}"; }
status() { curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $1" "${@:2}"; }

echo "Signing in"
anna=$(token anna)
bo=$(token bo)
erik=$(token erik)
check "no token is refused" "$(curl -s -o /dev/null -w '%{http_code}' "$api/agreements/lager/rule-sets")" 401
check "a person who is not a specialist is refused" "$(status "$erik" "$api/agreements/lager/rule-sets")" 403
check "a specialist is let in" "$(status "$anna" "$api/agreements/lager/rule-sets")" 200

echo "Uploading an agreement"
run=$(call "$anna" -X POST -H 'Content-Type: application/pdf' --data-binary @data/synthetic/hard-2026/agreement.pdf "$api/agreements/lager/runs" | json "d['id']")
for _ in $(seq 1 60); do
  state=$(call "$anna" "$api/runs/$run" | json "d['status']")
  [ "$state" = started ] || break
  sleep 1
done
check "the pipeline read it and proposed rules" "$state" awaiting_review
check "13 rules are proposed" "$(call "$anna" "$api/runs/$run" | json "len(d['rules'])")" 13
check "none has a problem" "$(call "$anna" "$api/runs/$run" | json "sum(len(r['problems']) for r in d['rules'])")" 0
check "another company cannot see the run" "$(status "$bo" "$api/runs/$run")" 404
check "nothing is published yet" "$(call "$anna" "$api/agreements/lager/rule-sets" | json "len(d)")" 0

echo "Reviewing and approving"
check "approval is refused while rules are undecided" "$(status "$anna" -X POST "$api/runs/$run/approval")" 409
for rule in $(call "$anna" "$api/runs/$run" | json "' '.join(r['id'] for r in d['rules'])"); do
  call "$anna" -X POST -H 'Content-Type: application/json' -d '{"decision":"approved"}' "$api/runs/$run/rules/$rule/decision" >/dev/null
done
check "the run is approved" "$(status "$anna" -X POST "$api/runs/$run/approval")" 202
for _ in $(seq 1 60); do
  state=$(call "$anna" "$api/runs/$run" | json "d['status']")
  [ "$state" = awaiting_review ] || break
  sleep 1
done
check "the rule set is published" "$state" published
check "as version 1, approved by anna" "$(call "$anna" "$api/agreements/lager/rule-sets" | json "f\"{d[0]['version']} {d[0]['approvedBy']} {len(d[0]['rules'])}\"")" "1 anna 13"
check "another company sees none of it" "$(call "$bo" "$api/agreements/lager/rule-sets" | json "len(d)")" 0

echo "Checking pay"
eva=$(token eva)
check "an employer cannot upload an agreement" "$(status "$eva" -X POST -H 'Content-Type: application/pdf' --data-binary @data/synthetic/hard-2026/agreement.pdf "$api/agreements/lager/runs")" 403
pay() { python3 -c "
import json
cases = json.load(open('data/synthetic/hard-2026/pay.json'))
answers = json.load(open('data/synthetic/hard-2026/pay-answers.json'))
wrong = {(a['employeeId'], a['periodStart']) for a in answers}
pick = [c for c in cases if ((c['timesheet']['employee']['id'], c['timesheet']['periodStart']) in wrong) == ($1 == 1)][0]
print(json.dumps(pick))"; }
check "a correct payslip has no findings" "$(pay 0 | call "$eva" -X POST -H 'Content-Type: application/json' --data-binary @- "$api/agreements/lager/pay-checks" | json "len(d['findings'])")" 0
found=$(pay 1 | call "$eva" -X POST -H 'Content-Type: application/json' --data-binary @- "$api/agreements/lager/pay-checks")
check "a wrong payslip has findings" "$(echo "$found" | json "len(d['findings']) > 0")" True
check "each finding is explained in words" "$(echo "$found" | json "all('kr.' in f['explanation'] for f in d['findings'])")" True

if [ -z "${SKIP_BROWSER:-}" ]; then
  echo "The console, in a real browser"
  # shellcheck disable=SC2086
  if ! dotnet run --project tools/UiCheck -- ${SHOTS:-}; then failed=1; fi
fi

echo
if [ "$failed" -eq 0 ]; then echo "All checks passed."; else echo "Some checks failed."; exit 1; fi
