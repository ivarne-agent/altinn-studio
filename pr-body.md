> **Stacked on #21843**, which passes the host's `STUDIO_CA_BUNDLE` to pdf3 and was split out of this pull request. Until #21843 merges, the diff here includes its commits. To see only this pull request's change: [compare](https://github.com/ivarne-agent/altinn-studio/compare/fix/pdf3-ca-bundle...refactor/studioctl-env-shared-runtime).
>
> The description was rewritten after the split: the CA bundle sections and their evidence moved to #21843. The branch is one commit, `9600a68774`, on top of #21843's.

## Summary

`studioctl env` starts a local Altinn Platform for app development, and has one kind of environment, `localtest`. This pull request makes room for more than one, starting with localtest-v2 (#21394).

- Each runtime is a package behind the `Runtime` and `Env` interfaces, so `env.go` names no runtime, and a new one is a package and a line in the registry.
- What every runtime does with its containers lives in `internal/cmd/env/stack`, the host's CA bundle from #21843 included. Runtimes do not import each other.
- `Env.Active` reads which runtime is up from the running containers, not from a state file.

```
src/cli/internal/cmd/
├── env.go               parses -r and the runtime's flags, calls the interface
└── env/
    ├── types.go         Runtime, Env, and the optional Resetter and Installer
    ├── active.go        which runtime is up, from its containers
    ├── registry/        the runtimes, the default first
    ├── stack/           apply, destroy, status, logs, container user, CA bundle
    └── localtest/       the localtest runtime
```

<details>
<summary>The change in detail</summary>

### Shared code: `internal/cmd/env/stack`

Everything a runtime does with its devenv resource graph:

- `Stack.Apply` and `Stack.Destroy`, with the progress display and a warning when a registry can't be reached.
- `Stack.Observe`, the status snapshot, and `Stack.HasManagedResources`.
- `Stack.ContainerImages`, which reports the image each container runs.
- `ManagedStatus`, `StatusString` and `LogStreamer`.
- `RuntimeUser`: the container user, the user-namespace mode and SELinux relabelling.
- `CheckForLegacyLocaltest`. A `localtest` container counts as studioctl's when its devenv graph ID starts with `studioctl-`, which covers every runtime's graph. `app env` uses it from here too.
- `LoadCABundle` and `CABundle.Mount`, moved unchanged from localtest's `components` package, where #21843 adds them, since they know nothing about localtest.

The `env status` payload (`Status`, `ContainerStatus`, `ContainerImage`) is in `internal/cmd/env`.

### Which runtime is up

- `Env.Active` lists the running containers that carry the runtime's devenv graph label, so it is right after a reboot, a failed `env up`, or a container removed by hand.
- `ActiveRuntimes`, `EnsureNoOtherActive` and `ResolveRuntime` build on it.
- `env up` refuses to start one runtime while another runs, since they share the public host names and port.
- Without `-r`, a command uses the runtime that runs, or the default (localtest) when none does. When more than one runs, it refuses and names them.

### The interface

- **`Runtime`** is one kind of environment: `Name()`, its own `env up` flags and their help (`UpUsage`, `BindUpFlags`), and `Env(client, out)`, the environment built with the parsed flags.
- **`Env`** is what the environment does: `Name`, `Active`, `Up`, `Down`, `Status` and `Logs`. `Up` includes the runtime's preflight and already-running checks. `UpOptions` holds only the shared options, since each runtime's own options are its bound flags. `Resetter` and `Installer` are optional.
- **`localtest/runtime.go`** implements `Runtime` for localtest, with `--monitoring`, `--pgadmin` and the hidden `--dev-workflow-engine`. The localtest package holds only what is its own: its components, its status rules, reset and hosts.
- **`registry`** lists the runtimes. `EnvCommand` takes the list and a container-client factory as fields, so tests can add a runtime and never reach the developer's Podman or Docker.

### `internal/cmd/env.go`

- Each command connects to the container runtime once, and uses that client to resolve the runtime and to act on it.
- `env up` reads `-r` first, then accepts that runtime's flags next to the shared ones (`-r`, `-d`, `--open`, `--json`):
  - A flag of another runtime is refused with a message that names the runtime, such as `--with is not supported by the localtest runtime (it is localtest-v2's); pick the runtime with -r`.
  - Two runtimes may each have a flag of the same name, with their own meanings.
  - `-r` given twice with different runtimes is refused, and so is a positional argument.
- `env up --json` keeps the `warnings` field from #21843 for every runtime.
- `env hosts` does not ask the container runtime, since every runtime shares the hostnames and `-r` only labels the output. It is the one place `env.go` uses the localtest package.
- `env reset` refuses, before it prompts: `--json` without `--yes`, a named runtime without reset before it connects, then a resolved runtime without reset, then a missing container runtime.
- The help shows one "Options for the <name> runtime:" section per runtime.

### Docs

- `src/cli/README.md` states the rules under "Development": one package per runtime behind `Runtime` and `Env`, the shared work in `stack` with the CA bundle, no imports between runtime packages, `env hosts` as the exception, and `Env.Active` instead of a state file.

### Behaviour changes for studioctl users, against `main`

- `env up --help` and `env --help` show the shared options, then a section per runtime.
- `env up` with an undefined flag prints only the error (`parsing flags: flag provided but not defined: -bogus`), without the help.
- `env up` refuses positional arguments.
- `env logs` with nothing running says `environment not running`.
- Commands without `-r` refuse when more than one runtime runs.

### Stack

This pull request stacks on #21843, the CA bundle for pdf3. #21394, the localtest-v2 runtime, builds on this pull request. Its runtime is its own package, `internal/cmd/env/localtestv2`, plus one line in the registry, and its pdf3 takes the CA bundle through the same `LoadCABundle` and `Mount`. It also changes shared code: the `env status` payload gains localtest-v2's origin, mode and emulated services, `stack.Active` uses devenv's `GraphRunning`, and it changes `app run`, `app env`, `envtopology`, studioctl-server's app discovery and devenv's resources for its front door.

</details>

## Evidence

Runs on this branch at `f322761d27`, built with `make dev-install`, whose tree the squashed head `9600a68774` keeps apart from the changelog's PR number, on Podman behind a TLS-intercepting proxy, with localtest as the only registered runtime. The recording, 60 s, is on the fork branch `studioctl-env-shared-runtime` with its [transcript](https://raw.githubusercontent.com/ivarne-agent/altinn-studio/evidence/studioctl-env-shared-runtime/transcript.txt), the [demo script](https://raw.githubusercontent.com/ivarne-agent/altinn-studio/evidence/studioctl-env-shared-runtime/demo.sh) and the [test run](https://raw.githubusercontent.com/ivarne-agent/altinn-studio/evidence/studioctl-env-shared-runtime/fake-runtime-tests.txt):

![env up --help, the refusals, env up --json with a warning, env status without -r, the pdf3 mount, env down](https://raw.githubusercontent.com/ivarne-agent/altinn-studio/evidence/studioctl-env-shared-runtime/terminal.gif)

- `env up --help` prints the shared options, then "Options for the localtest runtime:" with `--monitoring` and `--pgadmin`.
- `env up --with dialogporten` is refused with `parsing flags: flag provided but not defined: -with` and no help text. When a second runtime defines the flag, the message names it (`TestEnvUpNamesTheRuntimeAForeignFlagBelongsTo`).
- `env up extra-argument`: `invalid flag value: env up takes no arguments, got "extra-argument"`.
- `env up -r nope`: `unsupported runtime: nope (one of localtest)`.
- `env logs` with nothing running: `environment not running: no containers are running`.
- `env up --json` with a bundle that holds one unusable certificate: the warning is on stderr and in `warnings`, through `stack.LoadCABundle`.
- `env status` without `-r` picks localtest from its running containers (`Env.Active`), and `env status --json` carries the shared payload with each container's image and ID.
- pdf3 mounts `ca-bundle-8a7b85cf4234bc39.pem` at `/tmp/studio-ca-bundle.pem`, as it does with #21843 alone: moving the code into `stack` changed nothing for localtest.
- `env down` without `-r` stops it.

`env_runtimes_internal_test.go` adds a fake second runtime, with a flag of the same name as localtest's and one of its own, and shows that the two work side by side: flags, help, status, lookup and the refusals. With `--json`, a warning the fake runtime prints while it starts is in `warnings`. `go test -run 'Runtime|Active|Warning|Resolve|Flag' ./internal/cmd/ ./internal/cmd/env/` at the head: 36 tests pass, among them `TestEnvUpRefusesTwoDifferentRuntimes`, `TestEnvStatusAndResetReachEveryRuntime`, `TestResolveRuntimeRefusesToPickOneOfSeveralRunning` and `TestEnsureNoOtherActiveNamesTheRunningRuntime`.

After the split, the tree of this branch is the tree of its earlier head, 35c9f5dc83, merged with the current `main`, with two differences: `app env`'s legacy-localtest check from #21731 imports `CheckForLegacyLocaltest` from `stack`, and the CA bundle's changelog entry and `ui.Output.ForJSON` test come from #21843.

## Risk

- Every `env` command goes through the new interface and runtime resolution, so a mistake there breaks `env up`, `down`, `status`, `logs`, `reset` or `hosts` for localtest users. The fake-runtime tests and the existing localtest tests cover the paths, and the earlier manual runs used the default runtime.
- A command without `-r` now asks the container runtime which runtime runs. A container runtime that cannot answer falls back to the default runtime for most commands.
- `env up` with an undefined flag no longer prints the help, and `env up` refuses positional arguments, which a script could depend on.
- Nothing outlasts a revert.

## Follow-ups

- Docker Desktop, SELinux hosts and Windows are not tested.
