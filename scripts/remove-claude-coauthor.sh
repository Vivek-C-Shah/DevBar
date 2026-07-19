#!/usr/bin/env bash
# Rewrites all commits on the current branch to strip any
# "Co-Authored-By: ... claude / anthropic ..." trailer line from the
# commit message. Leaves everything else about each commit untouched
# (author, date, diff) but rewrites commit hashes.
#
# Usage: run from the repo root:
#   bash scripts/remove-claude-coauthor.sh
#
# Safety:
#   - Refuses to run if the working tree is dirty.
#   - Creates a backup ref (refs/original/...) via filter-branch by default,
#     and also tags the pre-rewrite tip as 'pre-coauthor-cleanup' so you can
#     bail out with: git reset --hard pre-coauthor-cleanup
#   - If this history has already been pushed anywhere, rewriting it will
#     require a force-push and will break other clones. Do not force-push
#     shared branches without confirming with collaborators.

set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

if [[ -n "$(git status --porcelain)" ]]; then
  echo "Working tree is not clean. Commit or stash your changes first." >&2
  exit 1
fi

backup_tag="pre-coauthor-cleanup-$(date +%Y%m%d%H%M%S)"
git tag "$backup_tag"
echo "Backup tag created: $backup_tag (restore with: git reset --hard $backup_tag)"

# Strip any Co-Authored-By trailer whose email/name mentions claude or
# anthropic (case-insensitive), plus any now-blank trailing lines left behind.
FILTER_BRANCH_SQUELCH_WARNING=1 git filter-branch -f --msg-filter '
  sed -E "/^Co-Authored-By:.*([Cc]laude|[Aa]nthropic)/Id" | sed -e "s/[[:space:]]*$//" | awk "NF{blank=0} !NF{if(blank)next; blank=1} 1"
' -- --all

echo "Done. Verify with: git log --format=%H%n%B"
echo "If everything looks right, clean up filter-branch refs with:"
echo "  git for-each-ref --format='delete %(refname)' refs/original | git update-ref --stdin"
echo "  git reflog expire --expire=now --all && git gc --prune=now"
echo "Remove the backup tag once satisfied: git tag -d $backup_tag"
