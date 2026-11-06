# Handover: csc exits 139 in `docker build` on Ubuntu 26.04 runners

Date: 2026-11-06 (Europe/Oslo). Prepared by the Altinn Studio agent for whoever opens the pull request.

## Open the pull request

Branch `fix/ci-apparmor-docker-build` on the fork `ivarne-agent/altinn-studio` (2 commits on top of
`Altinn/altinn-studio` `main` at `dc1110b5ff`). The agent account has no push access to `Altinn/altinn-studio`.

- Compare / open in the browser:
  https://github.com/Altinn/altinn-studio/compare/main...ivarne-agent:altinn-studio:fix/ci-apparmor-docker-build?expand=1
- Or from a shell, which also uploads the evidence GIF (needs write access to `Altinn/altinn-studio`):

  ```sh
  cd /home/agent/code/.artifacts/fix-apparmor-docker-build
  gh pr create --repo Altinn/altinn-studio --base main \
    --head ivarne-agent:fix/ci-apparmor-docker-build \
    --title 'ci: let container processes signal each other on Ubuntu 26.04 runners' \
    --body-file pr-body.md --attach run1/terminal.gif
  ```

  Opening through the compare link instead: paste `pr-body.md`, then drag `run1/terminal.gif` into the Evidence
  section in place of the `![…](terminal.gif)` line (or delete that line).

**Title:** `ci: let container processes signal each other on Ubuntu 26.04 runners`

**Description:** `pr-body.md` in this directory, written for the repository's pull request template (Summary,
Evidence, Risk, Follow-ups). Label `skip-changelog`: CI only, nothing users notice.

## What the change is

One step inserted before the `docker build` in five workflows, with a comment pointing at moby/moby#54099:

```yaml
      - name: Let container processes signal each other (moby/moby#54099)
        run: sudo sysctl -w kernel.apparmor_restrict_unprivileged_unconfined=0
```

| Workflow | Image built | Why included |
|---|---|---|
| `deploy-runtime-localtest.yaml` | localtest (dotnet/sdk 10.0.402-alpine) | the failing one, blocks studioctl preview.31 |
| `deploy-runtime-kubernetes-wrapper.yaml` | kubernetes-wrapper (same SDK image) | identical build pattern, next push to main would fail |
| `deploy-runtime-gateway.yaml` | gateway (dotnet/sdk 10.0-noble) | .NET build on ubuntu-latest |
| `deploy-runtime-workflow-engine-app.yaml` | workflow-engine-app (dotnet/sdk 10.0) | .NET build on ubuntu-latest |
| `template-docker-push.yaml` | Designer, Designer migrations (and gitea runners, repositories, loadbalancer) | Designer compiles .NET; harmless for the rest |

`actionlint` passes on all five (only pre-existing shellcheck infos in untouched lines). Not run in CI on the fork: the
real workflows need the `dev` environment and registry logins. The fix itself was verified 4/4 with the identical
`docker build` command (round 7 below).

## The failure

- First seen: [Runtime - deploy localtest, run 42226254045](https://github.com/Altinn/altinn-studio/actions/runs/42226254045),
  push of #21743 to `main`, 2026-11-06 13:32 UTC; four attempts (one with debug logging), all
  `error MSB6006: "csc" exited with code 139` in the linux/amd64 `dotnet publish` stage, no C# diagnostics.
- Last pass of that workflow: 2026-10-30, on `ubuntu-24.04`. The failing runs are on `ubuntu-26.04`, because
  `ubuntu-latest` moved to 26.04 ([actions/runner-images#14748](https://github.com/actions/runner-images/issues/14748)).
- It is not #21743: four builds of the commit before it (`a453860cb0`) fail the same way.

## Root cause

Ubuntu 26.04 ships `kernel.apparmor_restrict_unprivileged_unconfined=1` and kernel 7.0 (runners: 7.0.0-1019-azure).
runc applies the container's AppArmor profile from an unprivileged unconfined context, and the kernel now converts
that into a **stacked** label: `docker-default//&unconfined`. Docker's `docker-default` profile allows
`signal (send,receive) peer=docker-default` only for the literal name, so a container's own processes and threads
can no longer signal each other. dockerd's `docker run` path applies the profile with privileges and gets the plain
label; BuildKit's executor does not, so only `docker build` `RUN` steps are affected.

.NET's GC sends `SIGRTMIN+3` to threads it needs to suspend. When the kernel denies it, the runtime calls `abort()`;
`SIGABRT` to itself is denied too; musl's `abort()` then executes its crash instruction, which is the SIGSEGV
(exit 139) MSBuild reports for `csc`. It is random (~80 % of builds) because the signal is only needed when a thread
cannot be suspended cooperatively. The runner's kernel log during a failing build:

```
apparmor="DENIED" operation="signal" profile="docker-default" comm=".NET Server GC" requested_mask="send" signal=rtmin+3 peer="docker-default//&unconfined"
apparmor="DENIED" operation="signal" profile="docker-default" comm=".NET Server GC" requested_mask="send" signal=abrt   peer="docker-default//&unconfined"
csc: .NET Server GC: potentially unexpected fatal signal 11.
```

Upstream:
- [moby/moby#54099](https://github.com/moby/moby/issues/54099) — open (2026-11-02), Postgres never starts (`gosu`),
  same denials; notes the stacking depends on the kernel build.
- [containerd#14485](https://github.com/containerd/containerd/issues/14485), fix PR
  [containerd#14487](https://github.com/containerd/containerd/pull/14487) adds `{{.Name}}//&*` to the signal and
  ptrace rules — open.
- [dotnet/runtime#136603](https://github.com/dotnet/runtime/issues/136603) — the same .NET symptom in Kubernetes on
  Ubuntu 26.04 hosts (crashes in `.NET BGC` / `.NET TP Worker`, exit 139).
- [actions/runner-images#15182](https://github.com/actions/runner-images/issues/15182) — same stacking seen through
  Podman on the 26.04 image.

## The research (experiment workflow on the fork, since deleted; runs remain)

All rounds build `src/Runtime/localtest/Dockerfile` from `Altinn/altinn-studio` with the workflow's own
`setup-docker-action` daemon (containerd snapshotter) unless stated. 48 jobs in total.

| Round | Question | Result | Run |
|---|---|---|---|
| 1 | runner (24.04/26.04) × Docker (latest/29.9.2) × source (before/after #21743) × registry cache | 26.04+after: 3/3 ❌; 24.04: 2/2 ✅; 26.04+before: 1/1 ✅ (later shown to be luck) | [42270307406](https://github.com/ivarne-agent/altinn-studio/actions/runs/42270307406) |
| 2 | rlimits inside the sandbox; `--ulimit stack=` 8 MiB / 256 MiB | identical limits pass/fail; ulimits don't help | [42270716604](https://github.com/ivarne-agent/altinn-studio/actions/runs/42270716604) |
| 3 | `docker run` instead of `docker build`; `DOTNET_EnableAVX512F=0`, `EnableHWIntrinsic=0`, `TieredCompilation=0`; glibc build on the host | all 7 ✅, including on AVX-512 CPUs — rules out CPU/JIT, shows `docker run` is fine | [42271245626](https://github.com/ivarne-agent/altinn-studio/actions/runs/42271245626) |
| 4 | build vs run; stock vs action daemon; `--platform` | build ❌❌✅✅, run ✅ — probabilistic within build | [42271547465](https://github.com/ivarne-agent/altinn-studio/actions/runs/42271547465) |
| 5 | repetitions; `--platform`; `DOTNET_EnableWriteXorExecute=0`; crash report | 26.04 build 7/9 ❌, 24.04 2/2 ✅; createdump blocked (`/proc/<pid>/mem` EACCES) | [42271960387](https://github.com/ivarne-agent/altinn-studio/actions/runs/42271960387) |
| 6 | 4× before #21743, 2× after, 3× docker-container builder; `print-fatal-signals` + `dmesg` | 9/9 ❌; kernel log shows the AppArmor signal denials | [42272559465](https://github.com/ivarne-agent/altinn-studio/actions/runs/42272559465) |
| 7 | sysctl workaround ×4; labels of `docker run` vs BuildKit `RUN` | 4/4 ✅; baseline ❌ with label `docker-default//&unconfined`, plain after sysctl | [42273209180](https://github.com/ivarne-agent/altinn-studio/actions/runs/42273209180) |

Dead ends worth knowing: local reproduction on the arm64 sandbox is impossible for this (x64 .NET under QEMU
user-mode crashes on its own), the Docker version (29.9.2 vs 29.9.3) and registry build cache are irrelevant, and
the "needs both the 26.04 host and #21743" and "AVX-512" hypotheses from rounds 1–3 were single-sample artefacts of
an ~80 % failure rate.

## Alternatives considered

- `runs-on: ubuntu-24.04` — works (9/9), no sysctl change, but only postpones the problem and the repo uses
  `ubuntu-latest` in ~150 places.
- Wait for a Docker release with the profile fix — no timeline yet.
- Build with `docker run` instead of `docker build`, or a custom AppArmor profile — larger changes for the same effect.

## After merging

1. Re-run [Runtime - deploy localtest](https://github.com/Altinn/altinn-studio/actions/workflows/deploy-runtime-localtest.yaml)
   on `main` (`workflow_dispatch`) so the preview.31 localtest image lands in GHCR.
2. Publish `studioctl v0.1.0-preview.31`; the release commit is already on `main` (`0841280035`, #21791).
3. Docs PR [altinn-studio-docs#3289](https://github.com/Altinn/altinn-studio-docs/pull/3289) says "studioctl
   v0.1.0-preview.31 or newer" on all three pages (pushed as `7fdf43dc2`) and can merge once that release exists.
4. Remove the sysctl step when moby/moby#54099 is fixed in the Docker that `setup-docker-action` installs.

## Files

- `pr-body.md` — the pull request description.
- `run1/terminal.gif` (+ `terminal.cast`, `demo.sh`) — evidence recording for the Evidence section.
- Local worktree: `/home/agent/code/.worktrees/fix-apparmor-docker-build`.
