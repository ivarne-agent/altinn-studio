## Summary

Since `ubuntu-latest` moved to Ubuntu 26.04 (here on 2026-11-06), `docker build` of our .NET images dies in most runs with `error MSB6006: "csc" exited with code 139` and no diagnostics. First hit: [Runtime - deploy localtest, run 42226254045](https://github.com/Altinn/altinn-studio/actions/runs/42226254045), four attempts, which blocks the studioctl v0.1.0-preview.31 localtest image.

Cause: on these runners BuildKit `RUN` steps get the stacked AppArmor label `docker-default//&unconfined` (Ubuntu's `kernel.apparmor_restrict_unprivileged_unconfined=1` with kernel 7.0), and Docker's `docker-default` profile only allows `signal … peer=docker-default`, so a container's processes cannot signal each other ([moby/moby#54099](https://github.com/moby/moby/issues/54099), [containerd#14485](https://github.com/containerd/containerd/issues/14485)). .NET's GC sends `SIGRTMIN+3` to suspend threads; when that is denied the runtime calls `abort()`, whose `SIGABRT` is denied too, and musl's `abort()` ends in SIGSEGV. It is random because the signal is only needed when a thread cannot be suspended cooperatively.

The fix is one step before each `docker build` of a .NET image, in `deploy-runtime-localtest`, `-kubernetes-wrapper`, `-gateway`, `-workflow-engine-app` and `template-docker-push` (Designer and its migrations image):

```yaml
      - name: Let container processes signal each other (moby/moby#54099)
        run: sudo sysctl -w kernel.apparmor_restrict_unprivileged_unconfined=0
```

`RUN` steps then get the plain `docker-default` label again. `docker run` was never affected: dockerd applies the profile with privileges, BuildKit's executor does not.

## Evidence

Seven matrix rounds on a fork, 48 jobs, building `src/Runtime/localtest/Dockerfile` the way the workflow does:

| Variable | Result | Run |
|---|---|---|
| Source before vs after #21743, ubuntu-26.04 | 4/4 ❌ vs 9/9 ❌, so not the source | [R6](https://github.com/ivarne-agent/altinn-studio/actions/runs/42272559465) |
| ubuntu-24.04, any configuration | 9/9 ✅ | [R1](https://github.com/ivarne-agent/altinn-studio/actions/runs/42270307406), [R5](https://github.com/ivarne-agent/altinn-studio/actions/runs/42271960387) |
| `docker run` of the same publish, ubuntu-26.04 | 10/10 ✅ | [R3](https://github.com/ivarne-agent/altinn-studio/actions/runs/42271245626), [R4](https://github.com/ivarne-agent/altinn-studio/actions/runs/42271547465) |
| Docker version, registry cache, `--platform`, W^X, stack ulimit, CPU (AVX-512), docker-container builder | no effect | [R1](https://github.com/ivarne-agent/altinn-studio/actions/runs/42270307406), [R2](https://github.com/ivarne-agent/altinn-studio/actions/runs/42270716604), [R4](https://github.com/ivarne-agent/altinn-studio/actions/runs/42271547465), [R5](https://github.com/ivarne-agent/altinn-studio/actions/runs/42271960387) |
| **sysctl step before the build, ubuntu-26.04** | **4/4 ✅**, `RUN` label back to `docker-default` | [R7](https://github.com/ivarne-agent/altinn-studio/actions/runs/42273209180) |

The runner's kernel log during a failing build (`print-fatal-signals=1`; BuildKit's sandbox blocks `createdump`, so this is the only stack-level signal available):

```
apparmor="DENIED" operation="signal" profile="docker-default" comm=".NET Server GC" requested_mask="send" signal=rtmin+3 peer="docker-default//&unconfined"
apparmor="DENIED" operation="signal" profile="docker-default" comm=".NET Server GC" requested_mask="send" signal=abrt   peer="docker-default//&unconfined"
csc: .NET Server GC: potentially unexpected fatal signal 11.
```

Labels seen from inside the container on ubuntu-26.04 (R7): `docker run` → `docker-default (enforce)`; BuildKit `RUN` → `docker-default//&unconfined (enforce)`; BuildKit `RUN` after the sysctl → `docker-default (enforce)`.

![Round 7 on the fork: baseline fails, four builds with the sysctl pass, and the kernel log of the failing one](https://raw.githubusercontent.com/ivarne-agent/altinn-studio/evidence/fix-apparmor-docker-build/terminal.gif)

## Risk

- Relaxes a host hardening default on the ephemeral GitHub-hosted runner for the rest of the job. The `docker-default` profile still confines the build; only the label stacking goes. Nothing outlasts the job.
- Only the workflows that build .NET images get the step. Other `docker build`s on `ubuntu-latest` whose build stage needs inter-process signals (anything using `gosu`, `rsync`, …) would hit the same denial and are not changed here.
- Self-hosted runners are not on Ubuntu 26.04 and are unaffected.

## Follow-ups

- Remove the step when Docker ships the profile fix (follow [moby/moby#54099](https://github.com/moby/moby/issues/54099); containerd's equivalent is [containerd#14487](https://github.com/containerd/containerd/pull/14487)). Pinning `runs-on: ubuntu-24.04` is the alternative if relaxing the sysctl is unwanted.
- Re-run [Runtime - deploy localtest](https://github.com/Altinn/altinn-studio/actions/workflows/deploy-runtime-localtest.yaml) on `main` after merging, so the preview.31 localtest image exists; [altinn-studio-docs#3289](https://github.com/Altinn/altinn-studio-docs/pull/3289) points at that release.
