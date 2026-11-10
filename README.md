# Evidence for feat/studioctl-localtest-v2-stack, altinn-studio#21394 (not for merging)

Manual verification of the branch at `8245c6d714` on 2026-11-10, on a Linux sandbox behind a TLS-intercepting proxy,
with Podman, the pinned images (`sha-ddef433`, pdf3 `tt02`) and `STUDIO_CA_BUNDLE` set to the system bundle.
`pr-body.md` is the pull request description these files back.

| File | What |
| --- | --- |
| `terminal.gif`, `terminal.cast`, `transcript.txt`, `demo.sh` | `env up --help` with both runtimes, `env up -r localtest-v2`, `env up -r localtest` refused, `env status`, the pdf3 CA bundle mount, `studioctl run`, a PDF through `process/next`, `env down` |
| `pdf-page-1.png`, `instance.pdf` | The PDF the run produced and its first page |
| `localtest-pdf-check.sh` | The driver: test-user token, new instance, `PUT process/next`, the `ref-data-as-pdf` element and its fonts |
