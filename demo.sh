#!/usr/bin/env bash
# Demonstrates studioctl's env commands on the Runtime/Env interface, with localtest as the only registered runtime.
# Starting state: no environment running.
say() { printf '\n\033[1;36m$ %s\033[0m\n' "$*"; sleep 2; }
say 'studioctl env up --help      # shared options, then one section per runtime'
studioctl env up --help
sleep 3
say 'studioctl env up --with dialogporten     # a flag no registered runtime defines'
studioctl env up --with dialogporten
say 'studioctl env up extra-argument           # positional arguments are refused'
studioctl env up extra-argument
say 'studioctl env up -r nope                  # an unknown runtime'
studioctl env up -r nope
say 'studioctl env logs                        # nothing runs yet'
studioctl env logs
sleep 2
say 'export STUDIO_CA_BUNDLE=~/proxy-plus-system.pem; studioctl env up --json   # warnings in the JSON, through the shared stack code'
export STUDIO_CA_BUNDLE=/home/agent/rt-home/demo-bin/proxy-plus-system.pem
studioctl env up --json
sleep 3
say 'studioctl env status                      # no -r: the runtime whose containers run'
studioctl env status
sleep 3
say 'studioctl env status --json | head -c 400'
studioctl env status --json | head -c 400; echo
sleep 2
say "docker inspect localtest-pdf3 --format '{{range .Mounts}}{{.Source}} -> {{.Destination}}{{end}}'   # the CA bundle, now from stack/cabundle.go"
docker inspect localtest-pdf3 --format '{{range .Mounts}}{{.Source}} -> {{.Destination}}{{"\n"}}{{end}}'
sleep 2
say 'studioctl env down'
studioctl env down
sleep 4
