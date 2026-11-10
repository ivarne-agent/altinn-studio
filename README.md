# Evidence for fix/pdf3-ca-bundle (not for merging)

Manual verification of the `fix/pdf3-ca-bundle` branch at `02d46a4c5c` on 2026-11-10, on a Linux sandbox behind a
TLS-intercepting proxy, with Podman. `pr-body.md` is the pull request description these files back.

| File | What |
| --- | --- |
| `terminal.gif`, `terminal.cast`, `demo.sh` | `env up` with a bundle holding one unusable certificate, the pdf3 mount, `studioctl run`, and a PDF through `process/next` |
| `pdf-page-1.png` | The first page of the PDF from the system-bundle run |
| `localtest-pdf-check.sh` | The driver: test-user token, new instance, `PUT process/next`, the `ref-data-as-pdf` element and its fonts |
| `run1-no-bundle/` | No `STUDIO_CA_BUNDLE`: 500 and pdf3's `ERR_CERT_AUTHORITY_INVALID` |
| `run2-system-bundle/` | The system bundle: the mount, the NSS store, `pdffonts`, 200 |
| `run3-skipped-cert/` | A negative-serial CA before the system bundle: the warning, the copy, 200; and the entrypoint inside the `tt02` image |
| `run4-unusable/` | A malformed-only bundle and a missing file: the warnings, no mount |
