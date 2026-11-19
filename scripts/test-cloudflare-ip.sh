#!/usr/bin/env bash
# Checks how the API picks the client address behind Cloudflare, using the sign-in
# rate limit (5 attempts a minute per address; the 6th answers 429). Requests carry the
# headers the Container Apps ingress forwards: X-Forwarded-For ending in a Cloudflare
# edge address, and CF-Connecting-IP.
#
# Run the API locally with the settings the deployment sets behind Cloudflare:
#   cd server
#   App__TrustForwardedHeaders=true \
#   App__CloudflareIpRanges="$(jq -r 'join(",")' ../infra/cloudflare-ips.json)" \
#   BrowserApp__Enabled=false dotnet run --project src/Escribot.AppHost
# then, from the repository root:
#   BASE=http://localhost:5271 scripts/test-cloudflare-ip.sh
set -u
BASE=${BASE:-http://localhost:5271}
EDGE=172.64.10.20      # in Cloudflare's 172.64.0.0/13
failures=0

try() { # xff cf-connecting-ip
  curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/api/auth/sign-in/email" \
    -H 'Content-Type: application/json' -H "X-Forwarded-For: $1" -H "CF-Connecting-IP: $2" \
    -d '{"email":"nobody@example.test","password":"wrong-password"}'
}

check() { # label expected actual
  if [ "$2" = "$3" ]; then echo "ok   $1: $3"; else echo "FAIL $1: expected '$2', got '$3'"; failures=$((failures + 1)); fi
}

# Fresh addresses on every run, so counters left by an earlier run don't interfere.
R=$((RANDOM % 200 + 20))
limited="401 401 401 401 401 429"

codes=""; for _ in 1 2 3 4 5 6; do codes+="$(try "203.0.$R.1, $EDGE" "203.0.$R.1") "; done
check "one visitor through Cloudflare is limited" "$limited" "${codes% }"

check "another visitor on the same Cloudflare edge is not" "401" "$(try "203.0.$R.2, $EDGE" "203.0.$R.2")"

codes=""; for i in 1 2 3 4 5 6; do codes+="$(try "198.51.$R.9" "10.0.$R.$i") "; done
check "CF-Connecting-IP from a caller that isn't Cloudflare is ignored" "$limited" "${codes% }"

codes=""; for _ in 1 2 3 4 5 6; do codes+="$(try "198.18.$R.1, 172.65.$R.1" "not-an-ip") "; done
check "a malformed CF-Connecting-IP falls back to the edge address" "$limited" "${codes% }"

[ "$failures" -eq 0 ] && echo "All checks passed." || { echo "$failures check(s) failed."; exit 1; }
