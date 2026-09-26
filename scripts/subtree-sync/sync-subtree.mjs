#!/usr/bin/env node
// Merges an upstream branch into a git subtree prefix while isolating conflicts to
// the upstream commits that cause them. Used by the sync-* targets in the root Makefile.
//
//   node scripts/subtree-sync/sync-subtree.mjs \
//     --name backend --prefix src/App/backend \
//     --repo https://github.com/Altinn/app-lib-dotnet.git --branch main
//
// Algorithm:
//   1. Fetch the upstream branch and list its first-parent commits that are not yet
//      reachable from HEAD. Earlier (non-squash) subtree merges make the merged upstream
//      commits ancestors of HEAD, so this is exactly what remains to be synced.
//   2. Probe the pending commits from newest to oldest and merge the newest one that
//      merges without conflicts, which brings in every commit up to it in one merge.
//      When the upstream tip merges cleanly this is the single merge it always was.
//   3. When even the oldest pending commit conflicts, merge it on its own and wait for
//      the conflicts to be resolved, so they are resolved for that one commit only.
//   4. Repeat from 2 until nothing is pending.
//
// Mergeability is probed with `git merge-tree`, which never touches the working tree or
// the index, so probing is cheap. The merges themselves use
// `git merge --no-ff -Xsubtree=<prefix>`, which is what `git subtree merge` runs.

import { spawnSync } from 'node:child_process';
import { parseArgs } from 'node:util';
import readline from 'node:readline/promises';
import { stdin, stdout } from 'node:process';

const { values: args } = parseArgs({
  options: {
    name: { type: 'string' },
    prefix: { type: 'string' },
    repo: { type: 'string' },
    branch: { type: 'string' },
  },
});

for (const option of ['name', 'prefix', 'repo', 'branch']) {
  if (!args[option]) {
    console.error(`ERROR: --${option} is required`);
    process.exit(2);
  }
}

const { name, prefix, repo, branch } = args;

// --- git helpers ---

/** Runs git and returns trimmed stdout. Exits the process if git fails. */
function git(...gitArgs) {
  const result = spawnSync('git', gitArgs, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  if (result.status !== 0) {
    console.error(`ERROR: git ${gitArgs.join(' ')} failed with status ${result.status}`);
    if (result.stderr) console.error(result.stderr.trimEnd());
    process.exit(1);
  }
  return result.stdout.trim();
}

/** True when git exits with status 0. Output is discarded. */
function gitSucceeds(...gitArgs) {
  return spawnSync('git', gitArgs, { stdio: 'ignore' }).status === 0;
}

/** Runs git with output going straight to the terminal and returns the exit status. */
function gitInteractive(...gitArgs) {
  return spawnSync('git', gitArgs, { stdio: 'inherit' }).status;
}

function short(commit) {
  return commit.slice(0, 10);
}

function subject(commit) {
  return git('log', '-1', '--format=%s', commit);
}

function describe(commit) {
  return `${short(commit)} ${subject(commit)}`;
}

function unresolvedFiles() {
  return git('diff', '--name-only', '--diff-filter=U').split('\n').filter(Boolean);
}

/** True when merging `commit` into HEAD under the subtree prefix produces no conflicts. */
function mergesCleanly(commit) {
  const result = spawnSync(
    'git',
    ['merge-tree', '--write-tree', `-Xsubtree=${prefix}`, 'HEAD', commit],
    { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 },
  );
  if (result.status === 0) return true;
  if (result.status === 1) return false;
  console.error(`ERROR: git merge-tree failed with status ${result.status}`);
  console.error(result.stderr.trimEnd());
  process.exit(1);
}

// --- merging ---

/**
 * Merges `commit` into the subtree. When the merge conflicts, waits for the conflicts to be
 * resolved in the working tree and commits the result.
 */
async function merge(commit) {
  const message = `Merge ${name} ${short(commit)}: ${subject(commit)}`;
  const body = `Subtree: ${prefix}\nUpstream: ${repo} (${branch})`;
  const status = gitInteractive(
    'merge',
    '--no-ff',
    `-Xsubtree=${prefix}`,
    '-m',
    message,
    '-m',
    body,
    commit,
  );
  if (status !== 0) {
    if (unresolvedFiles().length === 0) {
      console.error(
        `ERROR: merging ${short(commit)} failed without conflicts, see the git output above`,
      );
      process.exit(1);
    }
    await resolveConflicts(commit);
  }
  const isAncestor = gitSucceeds('merge-base', '--is-ancestor', commit, 'HEAD');
  if (!isAncestor || unresolvedFiles().length > 0) {
    console.error(`ERROR: ${short(commit)} is not merged into HEAD`);
    process.exit(1);
  }
}

function exitWithResumeInstructions() {
  console.error('Resolve the conflicts, run `git add -A && git commit --no-edit`, then rerun:');
  console.error(`  node ${process.argv[1]} ${process.argv.slice(2).join(' ')}`);
  process.exit(1);
}

async function resolveConflicts(commit) {
  console.log('');
  console.log('===============================================');
  console.log(`Merge conflicts in ${name} caused by upstream commit ${describe(commit)}`);
  console.log('===============================================');
  console.log('');
  if (!stdin.isTTY) {
    console.error('ERROR: conflicts must be resolved interactively, but stdin is not a terminal.');
    exitWithResumeInstructions();
  }
  const rl = readline.createInterface({ input: stdin, output: stdout });
  try {
    let unresolved = unresolvedFiles();
    while (unresolved.length > 0) {
      console.log('Unresolved files:');
      for (const file of unresolved) console.log(`  ${file}`);
      console.log('');
      try {
        await rl.question('Resolve the conflicts in your editor, then press Enter... ');
      } catch {
        // Ctrl+D or closed stdin: leave the merge in progress for manual completion.
        console.log('');
        exitWithResumeInstructions();
      }
      unresolved = unresolvedFiles();
    }
  } finally {
    rl.close();
  }
  console.log('Committing resolved conflicts...');
  if (gitInteractive('add', '-A') !== 0 || gitInteractive('commit', '--no-edit') !== 0) {
    console.error('ERROR: committing the resolved merge failed');
    process.exit(1);
  }
}

// --- search ---

/**
 * Returns the index of the newest commit in `commits` that merges into HEAD without
 * conflicts, or -1 when none does. Probes from newest to oldest, so a later commit that
 * undoes an earlier conflicting change is still found.
 */
function findNewestClean(commits) {
  for (let i = commits.length - 1; i >= 0; i -= 1) {
    const clean = mergesCleanly(commits[i]);
    console.log(`  ${describe(commits[i])} -> ${clean ? 'merges cleanly' : 'conflicts'}`);
    if (clean) return i;
  }
  return -1;
}

// --- main ---

async function main() {
  if (git('status', '--porcelain', '--untracked-files=no') !== '') {
    console.error('ERROR: the working tree has uncommitted changes, commit or stash them first');
    process.exit(1);
  }
  if (gitSucceeds('rev-parse', '--verify', '-q', 'MERGE_HEAD') || unresolvedFiles().length > 0) {
    console.error('ERROR: a merge is already in progress, finish or abort it first');
    process.exit(1);
  }

  console.log('');
  console.log(`Syncing ${name} (${prefix}) from ${repo} (${branch})...`);
  git('fetch', repo, branch);
  const tip = git('rev-parse', 'FETCH_HEAD');

  let merges = 0;
  for (;;) {
    const pending = git('rev-list', '--reverse', '--first-parent', `HEAD..${tip}`)
      .split('\n')
      .filter(Boolean);
    if (pending.length === 0) break;

    console.log('');
    console.log(
      `${pending.length} upstream commit(s) pending, looking for the newest one that merges cleanly...`,
    );
    const newestClean = findNewestClean(pending);
    if (newestClean >= 0) {
      console.log('');
      console.log(`Merging ${newestClean + 1} commit(s), up to ${describe(pending[newestClean])}`);
      await merge(pending[newestClean]);
      merges += 1;
      continue;
    }

    console.log('');
    console.log(`Merging the conflicting commit ${describe(pending[0])} on its own`);
    await merge(pending[0]);
    merges += 1;
  }

  console.log('');
  if (merges === 0) console.log(`OK: ${name} subtree is already up to date`);
  else console.log(`OK: ${name} subtree synced with ${merges} merge commit(s)`);
}

await main();
