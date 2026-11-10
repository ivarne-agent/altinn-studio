#!/usr/bin/env bash
# End-to-end PDF check against classic localtest: token for test user ${USER_ID:-20001}, a new ttd/testapp instance,
# process/next (which renders the PDF through pdf3), then the PDF data element.
# Usage: localtest-pdf-check.sh <out-dir>   (BASE defaults to http://local.altinn.cloud:8000)
set -u
BASE=${BASE:-http://local.altinn.cloud:8000}
APP=$BASE/ttd/testapp
OUT=${1:?out dir}; mkdir -p "$OUT"
TOKEN=$(curl -s "$BASE/Home/GetTestUserToken?userId=${USER_ID:-20001}" | tr -d '"')
echo "token for user ${USER_ID:-20001}: ${#TOKEN} chars"
INST=$(curl -s -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -X POST "$APP/instances" -d "{\"instanceOwner\":{\"partyId\":\"${PARTY_ID:-520001}\"}}")
ID=$(printf '%s' "$INST" | python3 -c 'import sys,json; print(json.load(sys.stdin)["id"])')
echo "instance: $ID"
CODE=$(curl -s -o "$OUT/process-next.body" -w '%{http_code}' -H "Authorization: Bearer $TOKEN" -X PUT "$APP/instances/$ID/process/next")
echo "PUT process/next -> HTTP $CODE"
[ "$CODE" = 200 ] || { echo "body: $(head -c 400 "$OUT/process-next.body")"; exit 1; }
PDF=$(curl -s -H "Authorization: Bearer $TOKEN" "$APP/instances/$ID" | python3 -c 'import sys,json; d=[e for e in json.load(sys.stdin)["data"] if e["dataType"]=="ref-data-as-pdf"]; print(d[0]["id"] if d else "")')
[ -n "$PDF" ] || { echo "no ref-data-as-pdf data element"; exit 1; }
curl -s -H "Authorization: Bearer $TOKEN" -o "$OUT/instance.pdf" "$APP/instances/$ID/data/$PDF"
echo "PDF data element $PDF: $(wc -c < "$OUT/instance.pdf") bytes, header $(head -c 8 "$OUT/instance.pdf")"
echo "fonts: $(strings "$OUT/instance.pdf" | grep -o '/BaseFont */[A-Za-z0-9+-]*' | sort -u | tr '\n' ' ')"
