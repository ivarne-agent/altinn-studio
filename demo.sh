#!/bin/bash
# Evidence for the Ubuntu 26.04 csc crash: experiment runs on the fork ivarne-agent/altinn-studio.
R=ivarne-agent/altinn-studio
say() { printf '\n\033[1;36m$ %s\033[0m\n' "$*"; sleep 2; }
say "# Round 6: docker build of the localtest image on ubuntu-26.04, before and after #21743"
say gh run view 42272559465 -R $R
gh run view 42272559465 -R $R | sed -n '/^JOBS/,/^$/p' | grep -E '^[X✓]' | sed -E 's/ \(ID [0-9]+\)//'
sleep 5
say "# Round 7: the same build with 'sysctl -w kernel.apparmor_restrict_unprivileged_unconfined=0' first"
say gh run view 42273209180 -R $R
gh run view 42273209180 -R $R | sed -n '/^JOBS/,/^$/p' | grep -E '^[X✓]' | sed -E 's/ \(ID [0-9]+\)//'
sleep 5
say "# Kernel log of the failing baseline job: AppArmor denies the GC thread's signals"
say "gh run view -R $R --job 128082818351 --log | grep -E 'DENIED|fatal signal'"
gh run view -R $R --job 128082818351 --log 2>/dev/null | grep -E 'apparmor="DENIED"|fatal signal' | grep -v -E '##\[|grep ' \
  | sed -E 's/^.*[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+Z //; s/^\[ *[0-9.]+\] //; s/audit: type=1400 audit\([0-9.:]+\): //; s/ class="signal"//; s/ pid=[0-9]+//; s/ comm=[0-9A-F]+//; s/ denied_mask="send"//' | awk '!seen[$0]++' | head -3
sleep 6
say "# AppArmor label of a BuildKit RUN step on ubuntu-26.04, before and after the sysctl"
strip() { grep -v -E '##\[|echo |printf|docker build' | sed -E 's/^.*[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+Z //'; }
gh run view -R $R --job 128082818351 --log 2>/dev/null | grep -E 'buildkit RUN:|docker run:' | strip | sed 's/^/baseline     /'
gh run view -R $R --job 128082818045 --log 2>/dev/null | grep -E 'buildkit RUN:' | strip | sed 's/^/with sysctl  /'
sleep 7
