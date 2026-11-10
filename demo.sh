#!/usr/bin/env bash
# Demonstrates: studioctl env up passes the host's STUDIO_CA_BUNDLE to pdf3, so a PDF renders behind a TLS-intercepting proxy.
# Starting state: localtest is down. The bundle is the system CA bundle, which holds the proxy's CA, with one unusable certificate prepended.
say() { printf '\n\033[1;36m$ %s\033[0m\n' "$*"; sleep 2; }
say 'export STUDIO_CA_BUNDLE=~/proxy-plus-system.pem   # system CAs + the proxy CA, with one unusable certificate first'
export STUDIO_CA_BUNDLE=/home/agent/cab-home/demo-bin/proxy-plus-system.pem
say 'studioctl env up'
studioctl env up
sleep 3
say "docker inspect localtest-pdf3 --format '{{range .Mounts}}{{.Source}} -> {{.Destination}} ro={{not .RW}}{{end}}'"
docker inspect localtest-pdf3 --format '{{range .Mounts}}{{.Source}} -> {{.Destination}} ro={{not .RW}}{{"\n"}}{{end}}'
sleep 3
say 'cd apps/ttd-testapp-pdf && studioctl run --detach   # the app creates a PDF on process end'
cd /home/agent/code/apps/ttd-testapp-pdf && studioctl run --detach 2>&1 | tail -3
sleep 2
say 'localtest-pdf-check.sh out/   # test-user token, new instance, PUT process/next, fetch the PDF'
/home/agent/code/.scripts/localtest-pdf-check.sh /home/agent/code/.artifacts/pdf3-ca-bundle/demo/out
sleep 6
