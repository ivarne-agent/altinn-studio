#!/usr/bin/env bash
# Demonstrates the localtest-v2 runtime next to localtest: help, env up -r localtest-v2 on the pinned images,
# the refusal to start localtest beside it, status, an app run with a PDF through pdf3, and env down without -r.
# Starting state: no environment running. STUDIO_CA_BUNDLE is the system bundle (the host is behind a TLS-intercepting proxy).
say() { printf '\n\033[1;36m$ %s\033[0m\n' "$*"; sleep 2; }
say 'studioctl env up --help | tail -n +8        # one options section per runtime'
studioctl env up --help | tail -n +8
sleep 4
say 'echo $STUDIO_CA_BUNDLE'
echo "$STUDIO_CA_BUNDLE"
say 'studioctl env up -r localtest-v2'
studioctl env up -r localtest-v2
sleep 3
say 'studioctl env up -r localtest                # refused while localtest-v2 runs'
studioctl env up -r localtest
sleep 2
say 'studioctl env status                         # no -r: the runtime that runs'
studioctl env status
sleep 4
say "docker inspect localtest-v2-pdf3 --format '{{range .Mounts}}{{.Source}} -> {{.Destination}}{{end}}'   # the CA bundle through the shared stack code"
docker inspect localtest-v2-pdf3 --format '{{range .Mounts}}{{.Source}} -> {{.Destination}}{{"\n"}}{{end}}' 2>&1 | grep -i 'ca-bundle' || docker ps --format '{{.Names}}' | grep pdf3
sleep 2
say 'cd apps/ttd-testapp-pdf && studioctl run --detach    # waits until the front door has registered the app'
cd /home/agent/code/apps/ttd-testapp-pdf && studioctl run --detach 2>&1 | tail -4
sleep 2
say 'localtest-pdf-check.sh out/   # test-user token, new instance, PUT process/next, fetch the PDF'
/home/agent/code/.scripts/localtest-pdf-check.sh /home/agent/code/.artifacts/localtest-v2-stack/demo/out
sleep 4
say 'studioctl app stop && studioctl env down     # no -r: the runtime that runs'
studioctl app stop >/dev/null 2>&1; studioctl env down
sleep 4
