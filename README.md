# Evidence for refactor/studioctl-env-shared-runtime, altinn-studio#21483 (not for merging)

Manual verification of the branch at `f322761d27` on 2026-11-10, on a Linux sandbox behind a TLS-intercepting proxy,
with Podman and localtest as the only registered runtime. `pr-body.md` is the pull request description these files back.

| File | What |
| --- | --- |
| `terminal.gif`, `terminal.cast`, `transcript.txt`, `demo.sh` | `env up --help`, the refusals (foreign flag, positional argument, unknown runtime), `env logs` with nothing running, `env up --json` with a warning, `env status` without `-r`, the pdf3 CA bundle mount, `env down` |
| `fake-runtime-tests.txt` | `go test -run 'Runtime|Active|Warning|Resolve|Flag' ./internal/cmd/ ./internal/cmd/env/` at the head |
