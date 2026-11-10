## Summary

pdf3 renders an app's pages in Chromium, which has its own certificate store. Behind a TLS-intercepting proxy, such as a corporate network or a sandbox, Chromium does not trust the proxy's CA. It then cannot load the app frontend from `altinncdn.no` (`net::ERR_CERT_AUTHORITY_INVALID`), and the app's `process/next` answers 500 "Pdf generation failed".

`STUDIO_CA_BUNDLE` is the variable devenv uses for this: a PEM bundle with the system's CAs and the proxy's CA. pdf3's entrypoint imports the file it names into Chromium's store. This pull request gets the host's bundle there, in three layers:

```
host STUDIO_CA_BUNDLE
  └─ devenv cabundle.FromEnv         keeps the certificates Go accepts, names the others
       └─ studioctl LoadCABundle      <data>/infra/ca-bundle-<digest>.pem, 0644
            └─ pdf3 only, read-only   /tmp/studio-ca-bundle.pem, STUDIO_CA_BUNDLE set to it
                 └─ docker-entrypoint.sh   certutil imports what it can, starts anyway
```

Split out of #21483, which keeps the env runtime interface and stacks on this one.

<details>
<summary>The change in detail</summary>

### devenv: `pkg/cabundle`

- `pkg/cabundle` uses only the standard library, so studioctl can import it, and a test keeps it that way. Its Kubernetes artifact code moves to `pkg/cabundle/kubeartifact`, which the pdf3, operator and gateway testers and the kind runtime's Grafana use.
- `FromEnv` keeps each `CERTIFICATE` block that Go's X.509 parser accepts, in `Bundle.Data`, and lists the others in `Bundle.Skipped` with their line and reason, such as `x509: negative serial number`. Keys and the text between blocks are left out without a reason.
- `FromEnv` returns `cabundle.ErrNoCertificate` only when no certificate is usable, and its message says whether the file has no `CERTIFICATE` block or none that is usable.
- kind nodes, Flux, Grafana and the testers get the usable certificates. A bundle without one fails the cluster's CA step, as a missing file does.

### studioctl: `internal/cmd/env/localtest/components/cabundle.go`

- `LoadCABundle(out, dir)` writes the usable certificates to a copy in `dir`, named by their digest (`ca-bundle-<16 hex digits>.pem`, mode 0644), and removes the copies of other bundles.
- The copy is what gets mounted, so the container's user can read it whatever the original's mode, Docker Desktop shares it, and SELinux relabelling never touches a system file. The name follows the content, so a changed bundle changes the mount, and a copy is never rewritten under a running container.
- Skipped certificates give one warning that names the first three by line and counts the rest: `STUDIO_CA_BUNDLE (/path/bundle.pem): skipping 1 unusable certificate, the environment trusts the rest: line 1: x509: negative serial number`.
- `Mount(env, volumes)` mounts the copy read-only at `/tmp/studio-ca-bundle.pem`, as in devenv's clusters, and sets `STUDIO_CA_BUNDLE` to it. A nil bundle adds nothing.
- Any other problem, such as a missing file, a directory or a file without a usable certificate, is one warning, and the environment starts without the bundle.
- localtest loads the bundle when it starts, with the copy under `<data>/infra/`, and only its pdf3 mounts it, since no other image reads the variable.
- `env up --json` dropped every warning the environment printed. It now reports them in a `warnings` field and prints them on stderr, as `env hosts` does, through `ui.Output.ForJSON`.

The code sits in localtest's `components` package, since that is where pdf3's container is declared and `main` has no package shared between env runtimes. #21483 moves it to `internal/cmd/env/stack`.

### pdf3: `docker-entrypoint.sh`

- It reports a bundle without a certificate and starts without one, instead of exiting.
- It skips a certificate `certutil` cannot import, so one bad block does not keep the worker from starting.

### Docs

- `src/cli/CHANGELOG.md` has a Fixed entry for PDFs behind a TLS-intercepting proxy.

</details>

## Evidence

Runs on this branch at `02d46a4c5c`, built with `make dev-install`, on a Linux sandbox whose network goes through a TLS-intercepting proxy (Podman behind the `docker` command). The app is a copy of the `ttd/testapp` fixture on app-lib 8.12.15 with `enablePdfCreation` on its data type, so `process/next` out of `Task_1` renders a PDF through pdf3. A script fetches a test-user token, creates an instance, calls `PUT process/next` and downloads the `ref-data-as-pdf` element.

**Without a bundle** (`STUDIO_CA_BUNDLE` unset), pdf3 has no mount, and `process/next` fails as before:

```
PUT process/next -> HTTP 500
{"title":"Process next failed.","status":500,"detail":"Pdf generation failed"}
```

pdf3's log: `Failed to load resource: net::ERR_CERT_AUTHORITY_INVALID`, then `PDF generation failed ... error.type=element_not_ready`.

**With the system bundle** (`STUDIO_CA_BUNDLE=/etc/ssl/certs/ca-certificates.crt`, 121 certificates, the proxy's CA among them), `env up` prints no warning, and pdf3 gets the copy:

```
STUDIO_CA_BUNDLE=/tmp/studio-ca-bundle.pem
/home/agent/cab-home/data/infra/ca-bundle-8a7b85cf4234bc39.pem -> /tmp/studio-ca-bundle.pem ro=true
```

Inside the container, `certutil -L` lists 121 `studio-ca-bundle-*` entries. Then:

```
PUT process/next -> HTTP 200
PDF data element 525eefb7-bcc1-4e55-8aca-4fecc9cda3f4: 23396 bytes, header %PDF-1.4
AAAAAA+Altinn-DIN          CID TrueType  emb=yes
BAAAAA+Altinn-DIN-Bold     CID TrueType  emb=yes
CAAAAA+Altinn-DIN-Italic   CID TrueType  emb=yes
```

The fonts come from `altinncdn.no`, through the proxy. The first page:

![The generated PDF's first page](https://raw.githubusercontent.com/ivarne-agent/altinn-studio/evidence/pdf3-ca-bundle/pdf-page-1.png)

**A certificate Go rejects in the bundle** (a CA with a negative serial number, made with `openssl req -x509 -set_serial -1`, followed by the system bundle): `env up --json` reports it once and starts, and the PDF is the same. The recording shows `env up`, the mount, `studioctl run` and the check:

![env up with a bundle holding one unusable certificate, then a PDF](https://raw.githubusercontent.com/ivarne-agent/altinn-studio/evidence/pdf3-ca-bundle/terminal.gif)

```
{"runtime":"localtest","warnings":["STUDIO_CA_BUNDLE (/tmp/.../negative-plus-system.pem): skipping 1 unusable certificate, the environment trusts the rest: line 1: x509: negative serial number"],"running":true,"started":true,"alreadyRunning":false}
```

The same line is on stderr. The copy keeps its name, `ca-bundle-8a7b85cf4234bc39.pem`, since the usable certificates are the same 121 as before.

**A bundle with no usable certificate** (one malformed `CERTIFICATE` block) and **a missing file**: one warning each, the environment starts, pdf3 runs with no mount and no `STUDIO_CA_BUNDLE`, and no copy is left under `<data>/infra/`:

```
{"runtime":"localtest","warnings":["STUDIO_CA_BUNDLE holds no usable certificate: /tmp/.../malformed-only.pem: 1 certificate, none usable; line 1: x509: malformed certificate; the environment starts without the CA bundle"],"running":true,"started":true,"alreadyRunning":false}
{"runtime":"localtest","warnings":["stat STUDIO_CA_BUNDLE: stat /nonexistent.pem: no such file or directory; the environment starts without the CA bundle"],"running":true,"started":true,"alreadyRunning":false}
```

**The entrypoint**, mounted into `ghcr.io/altinn/altinn-studio/runtime-pdf3-worker:tt02` with the command replaced by an echo:

| Bundle | This branch | `main` |
| --- | --- | --- |
| one malformed block | `certutil: could not decode certificate: SEC_ERROR_BAD_DER`, `pdf3 worker: skipped certificate 1 of STUDIO_CA_BUNDLE`, `installed 0 of 1 certificates`, starts (exit 0) | exit 255 before the worker starts |
| negative serial + system bundle | imports all 122 and starts | imports all 122 and starts |

`certutil` accepts a negative serial number that Go rejects, see Risk.

Unit tests at the head: `go test ./pkg/cabundle/...` in devenv, and `go test ./internal/cmd/env/... ./internal/ui/... ./internal/cmd/` in studioctl, pass. `TestStartGivesPDF3TheHostsCABundle` builds localtest's manifest with `STUDIO_CA_BUNDLE` set and fails if `env up` stops loading the bundle or pdf3 stops mounting it.

## Risk

- The devenv CA bundle filter changes what kind nodes, Flux, Grafana and the pdf3, operator and gateway testers trust: the certificates Go accepts, instead of the whole file.
- Go's parser is stricter than NSS. A proxy CA with a negative serial number is left out of pdf3's copy, with the warning, although `certutil` would have imported it, so on such a proxy pdf3 fails as it does today. Go rejects these since 1.23; `GODEBUG=x509negativeserial=1` in studioctl, or a filter that only drops what `pem.Decode` cannot read, would let them through. Left as is here, since the filter also decides what the kind clusters trust.
- `env up` leaves a running localtest as it is, so a new or changed bundle takes effect only after `studioctl env down` and `studioctl env up`, as the changelog entry says.
- `env up --json` now prints the environment's warnings on stderr, where before it printed nothing there. stdout is still only the JSON.
- pdf3's new entrypoint reaches localtest and the clusters only with the next pdf3 image. Until then, localtest's copy holds only certificates Go accepts, which the old entrypoint imports. Nothing outlasts a revert.

## Follow-ups

- Docker Desktop, SELinux hosts and Windows are not tested.
- A pdf3 image built from this branch is not tested. The entrypoint was tested mounted into the `tt02` image.
- The pdf3, operator and gateway testers were built, not run against kind clusters.
