---
allowed-tools: Bash(git add:*), Bash(git status:*), Bash(git diff:*), Bash(git log:*), Bash(git commit:*)
description: Stage all changes and commit with a message derived from the actual code changes
---

## Context

- Current git status: !`git status`
- Staged and unstaged diff: !`git diff HEAD`
- Recent commits (for message style): !`git log --oneline -10`
- Current branch: !`git branch --show-current`

## Your task

Review the diff above and write a commit message that accurately reflects what changed and why — not just what files changed. Follow these rules:

1. **Analyze the diff** to understand: what feature, fix, or refactor is being committed; which packages or layers are affected (e.g. sync engine, macOS app, Go backend, installer scripts, docs); and whether this is a new capability, a bug fix, a refactor, or a config/tooling change.

2. **Write the commit message** using this structure:
   - Subject line (≤72 chars): imperative mood, no period, describes the change concisely. Use a type prefix when appropriate: `feat:`, `fix:`, `refactor:`, `chore:`, `docs:`, `test:`, `build:`.
   - One blank line.
   - Body (optional, wrap at 72 chars): explain *why* the change was made if it is not obvious from the subject. Mention affected components (e.g. `apps/desktop`, `apps/macos`, `linux-ubuntu`, `windows`) when useful context.
   - Trailer: `Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>`

3. **Stage and commit** in a single message using one `git add` + one `git commit` call. Pass the message via a heredoc so formatting is preserved:
   ```
   git commit -m "$(cat <<'EOF'
   <subject line>

   <body if needed>

   Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
   EOF
   )"
   ```

Do not run any tools beyond `git add`, `git status`, and `git commit`. Do not write any text outside of the tool calls.
