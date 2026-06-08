---
allowed-tools: Bash(git add:*), Bash(git status:*), Bash(git diff:*), Bash(git log:*), Bash(git commit:*), Bash(git push:*), Bash(git branch:*), Bash(git checkout:*), Bash(gh pr create:*), Bash(gh pr view:*)
description: Stage, commit, push, and open a pull request — commit message and PR body derived from the actual changes
---

## Context

- Current git status: !`git status`
- Staged and unstaged diff: !`git diff HEAD`
- Recent commits (for message style): !`git log --oneline -10`
- Current branch: !`git branch --show-current`
- Remote tracking info: !`git status -sb`

## Your task

Perform all four steps in a single message (one set of tool calls). Do not pause between steps.

### Step 1 — Analyze the changes

Read the diff above and determine:
- What changed: feature, bug fix, refactor, chore, docs, test, or build change.
- Which project areas are affected: Go backend (`apps/desktop/`), macOS app (`apps/macos/`), platform installers (`linux-ubuntu/`, `windows/`), or project-level config/docs.
- The *why*: what problem this solves or what capability it adds.

### Step 2 — Stage and commit

Write a commit message using these rules:
- Subject line (≤72 chars): imperative mood, no period. Use a type prefix: `feat:`, `fix:`, `refactor:`, `chore:`, `docs:`, `test:`, `build:`.
- Blank line, then optional body (wrap at 72 chars) explaining *why* the change was made.
- Trailer: `Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>`

Stage and commit:
```
git add <relevant files>
git commit -m "$(cat <<'EOF'
<subject line>

<body if needed>

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

### Step 3 — Push

If the current branch is `main` or `master`, create a new branch named `feat/<short-slug-from-subject>` first, then push with `-u`:
```
git checkout -b feat/<slug>   # only if currently on main/master
git push -u origin HEAD
```

Otherwise push the current branch:
```
git push -u origin HEAD
```

### Step 4 — Open a pull request

Create the PR with `gh pr create`. The title must match the commit subject line. The body must follow this template (use a heredoc):

```
gh pr create --title "<subject line without type prefix>" --body "$(cat <<'EOF'
## Summary

- <bullet: what changed>
- <bullet: which component(s) are affected>
- <bullet: why — the motivation or problem solved>

## Areas affected

<!-- list dirs/packages touched, e.g. apps/desktop, apps/macos, linux-ubuntu, windows -->

## Test plan

- [ ] Build succeeds on the affected platform(s)
- [ ] Manual smoke test of the changed functionality
- [ ] No regressions in adjacent features

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

After `gh pr create` returns the PR URL, output only that URL — nothing else.
