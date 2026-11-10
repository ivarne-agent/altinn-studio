> **Stacked on #21483**, which moves what the env runtimes share into `internal/cmd/env/stack` and decides which runtime is up from the running containers, and #21483 is stacked on #PRNUMBER, which passes the host's CA bundle to pdf3. Until both merge, the diff here includes them. To see only this pull request's change: [compare](https://github.com/ivarne-agent/altinn-studio/compare/refactor/studioctl-env-shared-runtime...feat/studioctl-localtest-v2-stack).
>
> **Draft for a shape review.** It cannot be merged as is (see Follow-ups). I'd like feedback on the overall shape: a second `env` runtime driven by an embedded manifest, the localtest container as the front door, and studioctl-server registering the apps it discovers with that front door.

## Summary

This adds a second environment runtime to studioctl, `studioctl env up -r localtest-v2`. It runs the Altinn Platform's own services as containers instead of localtest's emulation of them: Register, Profile, Authentication, the PDP, Access Management, Storage, Events, the Resource Registry and Notifications. Beside them run the localtest app as the front door, a shared Postgres, and the same pdf3 and workflow engine as the `localtest` runtime. Apps run against it unchanged, as processes or containers.

Every gap between localtest's emulation and production is a bug that local testing cannot find, and some app-frontend specs failed for that reason. Running the platform's own services removes that class of difference, and brings services localtest never had, such as the Resource Registry, Notifications, Dialogporten, Correspondence, the receipt and Arbeidsflate.

```
src/cli/
├── internal/localtestv2/manifest/   manifest.json: the containers, groups and public origin
├── internal/cmd/env/localtestv2/    the runtime: env up, down, reset, status, logs
├── internal/cmd/app/                the app environment and the registration wait
├── studioctl-server/Discovery/      registers discovered apps with the front door
└── docs/localtest-v2.md             how to use it, and how it is built
```

<details>
<summary>The change in detail</summary>

- Optional groups add Dialogporten, the receipt, Correspondence, Arbeidsflate and a monitoring stack (an OpenTelemetry collector, VictoriaMetrics, VictoriaTraces, VictoriaLogs and Grafana).
- A minimal mode runs a small stack where the front door stands in for most services, and writes the telemetry to JSON Lines files.
- The public origin can move off `http://local.altinn.cloud:8000`.
- pdf3 takes the host's `STUDIO_CA_BUNDLE` through #21483's shared code, as in the `localtest` runtime.

The `localtest` runtime stays the default. Against `main`, `env logs -c` shows a stopped container's log, and a removed container takes its anonymous volumes with it.

### The manifest (`internal/localtestv2/manifest/`)

`manifest.json` describes the stack, and the `README.md` beside it is the format.

- `public`: the origin's default host, port and subdomains (`pdf.`, `workflow-engine.`, `app-frontend.`, `af.`), which the containers' `envs` use through placeholders such as `{{origin}}` and `{{host:<sub>}}`.
- `groups`: `default` (always on), `minimal` (replaces it in minimal mode), and the optional `dialogporten`, `receipt`, `correspondence`, `arbeidsflate` and `monitoring`, each naming its root containers. `everything` is every optional group.
- `containers`: 28 containers, each with its image, `envs`, `dependsOn` and named `volumes`, and optionally `port`, `health`, `files` (files shipped beside the manifest, mounted read-only), `otlpReceiver` (the stack's OTLP receiver) and `caBundle` (takes the host's CA bundle).

The reader refuses unknown keys, unresolved references, cycles, containers no group reaches, two OTLP receivers in one selection, and out-of-range values. `Select` returns a set of groups' containers in start order. Adding a container needs no Go change.

### The runtime (`internal/cmd/env/localtestv2/`)

- **Containers:** one per selected entry, in dependency order, aliased by name, on `localtest-v2_network`. Each gets the public host names and `host.docker.internal` on the host gateway, the shared test certificate at `/certs`, its `envs`, its `files`, and a `services__<name>__http__0` variable for every running HTTP container. Only the localtest container publishes a port, 8000 or `--port`.
- **Health:** a short-lived probe container on the stack's network checks each container, by HTTP path or TCP, through devenv's new readiness probe, before its dependents start. Then the runtime polls the front door's `/health` through the public port.
- **Flags:** `--with` and `--without <group>`, `--latest`, `--minimal`, `--host`, `--port` (`0` picks a free one), and `--dev-<container>[=<path>]`, which builds a container from a Dockerfile. `env up` refuses another runtime's flags.
- **State and convergence:** a state file records how the stack was started: groups, origin, mode, `--latest`, dev builds and the CA bundle's digest. `env up` on a running stack converges it when any of them differs, and says "already running" otherwise. The state file never decides whether the stack runs. `localtestv2.Current` checks the containers with devenv's `GraphRunning` first, so a file left after a reboot or `docker rm` counts as no localtest-v2.
- **Monitoring:** while the collector runs, every other container starts after it and gets `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_SERVICE_NAME` and a 5 s metric interval, unless its `envs` set them. The workflow engine gets its dashboard's Grafana links. Grafana is at `{{origin}}/grafana/`.
- **Minimal mode:** Postgres, Storage, the workflow engine, the localtest container and `otel-files`, with tmpfs volumes. The front door emulates the other services. `otel-files` writes `<data>/infra/localtest-v2/telemetry/<service>/{traces,logs,metrics}.jsonl`, size-rotated and owned by the developer. `env up` empties the folder, and `env down` deletes it.
- **CA bundle:** pdf3, the one container with `caBundle`, takes the host's bundle with #21483's `stack.LoadCABundle` and `CABundle.Mount`: a copy of its usable certificates under `<data>/infra/localtest-v2/ca-bundle/`, read-only at `/tmp/studio-ca-bundle.pem`, relabelled for SELinux like the runtime's other bind mounts. A changed bundle changes the copy's name, so `env up` recreates pdf3.
- **Failed starts** are cleaned up (containers, network, state file), except when a container never became healthy. The stack then stays up so its logs can be read.
- **`env reset`** deletes the manifest's `localtest-v2-*` volumes.
- **Shared code:** apply, destroy, status, logs, the container user, the legacy-localtest check and the CA bundle come from `internal/cmd/env/stack`. The package imports nothing from the localtest runtime's package.
- **Snapshot:** `testdata/environment.golden.json` pins what the runtime builds for the default stack, `--with everything` (with a CA bundle), `--minimal` and a custom origin.

### Running apps against it

- **App environment** (`internal/cmd/app/`): while localtest-v2 runs, the app gets the test certificate's settings, the Arbeidsflate links and the platform's logout as its logout link, and listens on `0.0.0.0`, because the front door reaches it as `host.docker.internal:<port>`. A `localtest` run keeps localtest's start page as its inbox and logout links. `studioctl app env` prints the environment, and `app-environment.golden.json` pins it.
- **`studioctl run`** waits until the front door lists the app with a provisioning that succeeded since the run started. When the containers run but the front door does not answer, it stops before the build with the command to fix it. `--dev-frontend` routes `app-frontend.<host>` to the dev server on port 8080 while the run lasts.
- **Container mode:** `run --mode container` puts the app container on the stack's network under its alias, with the same settings as a process run. `env down` removes app containers that still run. It is refused at a custom origin.
- **studioctl-server** (`Discovery/FrontDoorRegistration.cs`): while localtest-v2 runs, it registers each app discovery finds, from `studioctl run` or an IDE, with `PUT localtest/api/apps/{org}/{app}`. The front door routes `/{org}/{app}/` to it and provisions it from the app's own API. A failed registration is retried on the next refresh. An app that restarted, or that the front door no longer lists, is registered again, and one that goes gets a `DELETE`. The calls run beside discovery, at most one per app, so a slow front door delays only that app. `env up` restarts the server once the containers are healthy.

### devenv

- `ContainerLifecycleOptions.ReadinessProbe`: a check the executor runs once a container is ready and before its dependents start, for readiness the runtime cannot observe.
- `VolumeMount.Options` and a tmpfs mount type. A removed container takes its anonymous volumes with it.
- `GraphRunning`: whether a container with a graph's label runs.

### Commits

1. `5d921267d2` feat(devenv): the readiness probe.
2. `aa000cce42` feat(studioctl): the manifest and its reader.
3. `f42345e16e` feat(studioctl): the runtime for `env up`, `down`, `reset`, `status` and `logs`.
4. `68097e38f4` feat(studioctl): running apps against it: the app environment, the registration wait and the dev-server route.
5. `da549587d7` feat(studioctl-server): registering discovered apps with the front door.
6. `6226e3aaff` docs(studioctl): the changelog and the bundled skill.
7. `c84edbc883` feat(devenv): tmpfs mounts, and anonymous volumes removed with their container.
8. `68dff9ec1d` feat(studioctl-server): an app container registered at its address on the stack's network.
9. `fe933a6ba4` feat(studioctl): minimal mode, the configurable origin, container mode and cleanup after a failed start.
10. `e3cd7a05ea` docs(studioctl): the changelog, manifest README and skill for 9.
11. `4f69df6da9` fix(studioctl-server): registration beside discovery, not inside its refresh.
12. `26271005cb` fix(studioctl): localtest-v2 runs when its containers run, not when its state file exists.
13. `038eff492e` docs(studioctl): no references to the research repository's tasks.
14. `7ecd965d12` feat(studioctl): the monitoring group and the manifest's `files`.
15. `19926f3202` feat(studioctl): minimal mode's telemetry files.
16. `e7a96aac14` feat(studioctl): pdf3 takes the host's CA bundle through the shared code.
17. `15795e6b6e` docs(studioctl): changelog entries and the manifest README formatted.
18. `f05805430c` test(studioctl): a `localtest` run's app gets localtest's start page links, which localtest-v2 runs leave alone.
19. `d797f44ac7` docs(studioctl): `docs/localtest-v2.md` with the usage and design docs, and one changelog entry for localtest-v2.
20. `d9163a934c` style(studioctl): the localtest-v2 comments in sentences.

### Docs

- `src/cli/docs/localtest-v2.md`: how to use localtest-v2, why apps share the stack's network, and how the runtime is built. `src/cli/README.md` links it.
- The bundled app-development skill in `agents/_skills/app-development/SKILL.md`, including how to read the telemetry files with `jq`.
- `internal/localtestv2/manifest/README.md`: the format, and the conventions the code depends on, such as tags, ports and health, the certificate, volumes and what the runner adds.
- `src/cli/CHANGELOG.md`: one Added entry for localtest-v2, with a sub-bullet per flag, and a Fixed entry for the anonymous volumes.

</details>

## Evidence

A run on this branch at `8245c6d714`, built with `make dev-install`, on Podman behind a TLS-intercepting proxy, with the pinned images (`sha-ddef433`, pdf3 `tt02`), `STUDIO_CA_BUNDLE=/etc/ssl/certs/ca-certificates.crt`, and a `ttd/testapp` copy that creates a PDF on process end. The recording, 73 s, is on the fork branch `studioctl-localtest-v2-stack` with its [transcript](https://raw.githubusercontent.com/ivarne-agent/altinn-studio/evidence/studioctl-localtest-v2-stack/transcript.txt) and [demo script](https://raw.githubusercontent.com/ivarne-agent/altinn-studio/evidence/studioctl-localtest-v2-stack/demo.sh):

![env up --help with both runtimes, env up -r localtest-v2, the refusal to start localtest beside it, env status, the pdf3 mount, studioctl run, a PDF through process/next, env down](https://raw.githubusercontent.com/ivarne-agent/altinn-studio/evidence/studioctl-localtest-v2-stack/terminal.gif)

- `env up --help` prints the shared options, then one section each for localtest and localtest-v2 (`--latest`, `--minimal`, `--host`, `--port`, `--dev-NAME[=PATH]`, `--with`, `--without`).
- `env up -r localtest-v2` started the 13 containers of the default groups and waited for the front door: "Environment healthy", then the `env logs -r localtest-v2` and `env down -r localtest-v2` hints.
- `env up -r localtest` while it runs: `preflight check: another environment runtime is running: localtest-v2; stop it first with 'env down -r localtest-v2'`.
- `env status` without `-r` picks localtest-v2 from its containers and prints `Origin: http://local.altinn.cloud:8000` and the 13 containers with their images and IDs.
- pdf3 mounts the host bundle's copy, `data/infra/localtest-v2/ca-bundle/ca-bundle-8a7b85cf4234bc39.pem -> /tmp/studio-ca-bundle.pem`, through the shared `stack` code of #21483.
- `studioctl run --detach` waited until the front door had registered the app, then printed `App ready: http://local.altinn.cloud:8000/ttd/testapp/`.
- `PUT process/next` answered 200, and the `ref-data-as-pdf` element is a 23 362-byte PDF that embeds Altinn-DIN, Altinn-DIN-Bold and Altinn-DIN-Italic from the CDN:

![The generated PDF's first page](https://raw.githubusercontent.com/ivarne-agent/altinn-studio/evidence/studioctl-localtest-v2-stack/pdf-page-1.png)

- `env down` without `-r` stopped localtest-v2.

Not repeated on this head: `--with monitoring` on the running stack, `--minimal`, and the CA bundle variants (an unusable certificate, a bundle without one). Those ran on the earlier build 7f548efa73 of the same change; the CA bundle variants are shown on #PRNUMBER, whose code this runtime calls.

studioctl-server's tests pass, apart from the 13 `V8Tov9UpgradeTargetFrameworkTests`, which need the .NET 11 SDK and fail the same way without this pull request on a machine with only .NET 10.

## Risk

- localtest-v2 is opt-in with `-r localtest-v2`, so a mistake there reaches only those who choose it.
- The shared parts reach `localtest` users too:
  - devenv's readiness probe, tmpfs mounts and anonymous-volume removal
  - `stack.Active` through `GraphRunning`, and `env logs -c` for a stopped container
  - the `env status` payload, which gains the origin, mode and emulated services
  - studioctl-server's discovery, which registers apps with the front door only while localtest-v2 runs
- `env reset -r localtest-v2` deletes the `localtest-v2-*` volumes and nothing else.
- The images come from the research repository's registry, so the stack depends on it until the images move.

## Follow-ups

Before this can merge:

- **Images and registry.** The manifest's `registry` values are `ghcr.io/ivarne-agent/localtest-research/localtest-<name>` at `sha-ddef433`, built by the research repository's image workflow. The components need a home and an image workflow in Altinn's registry. The manifest README's `bump-tag.sh` and workflows are the research repository's.
- **The localtest image lives outside this pull request.** The front door, its routes, the 503 pages, minimal mode's emulation, provisioning and the registration API are the research repository's localtest app (`localtest-localtest` image), not `src/Runtime/localtest`. Moving it into the monorepo, or merging it with the existing localtest, is open.
- **The Aspire package lives outside this pull request.** `localtest/aspire/Altinn.App.Aspire` in the research repository reads the same manifest and compares itself with the golden files and the test CA bundle. Comments in the golden tests and the manifest README name it.
- **References to the research repository** remain, by absolute URL or image reference, in `src/cli/docs/localtest-v2.md`, `localtestv2/doc.go`, `certs.go` (the test key pair), the manifest README, the golden file and a few tests. The research repository's [`localtest/studioctl/README.md`](https://github.com/ivarne-agent/localtest-research/blob/main/localtest/studioctl/README.md#not-mergeable-yet) lists each one and what it waits for.
- **docs.altinn.studio** does not describe localtest-v2 yet.

Not tested: Docker Desktop, SELinux hosts and Windows, the optional groups other than `monitoring`, and the smoke suites of the research repository's components.

To try it:

```sh
cd src/cli && make dev-install && export STUDIOCTL_HOME=$PWD/build/dev-home   # a short path: the socket must stay under 107 characters
build/dev-home/bin/studioctl env hosts add                                   # af.local.altinn.cloud for Arbeidsflate
build/dev-home/bin/studioctl env up -r localtest-v2 --with monitoring        # several GB of images on the first pull
cd <an app> && <...>/studioctl run                                           # waits until the front door has provisioned the app
curl http://local.altinn.cloud:8000/localtest/api/apps                       # the registrations
<...>/studioctl env down                                                     # env reset also deletes the localtest-v2-* volumes
```
