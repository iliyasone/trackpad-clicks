---
name: babysit-pr
description: Drive a branch or PR to a clean review — a fresh sub-agent reviews the diff, you fix what it finds, push, and send it back for another review, round after round until it finds nothing left. Use when asked to babysit a PR, "review and fix until clean", or run a review-fix loop on the current branch.
---

# Babysit PR

You are the author; a sub-agent is the reviewer. Each round a **new** reviewer
reads the whole diff cold, you triage and fix its findings, push, and start the
next round. Stop when a round produces nothing you accept as a real problem.

## 0. Pin the target

- Target = the PR number the user gave, or the PR of the current branch
  (`gh pr view --json number,title,body,baseRefName,headRefName,url`); its
  `baseRefName` is the base. With no PR, review the current branch against the
  base branch the repo's AGENTS.md / CLAUDE.md names.
- Check out the head branch, make sure the working tree is clean and up to date
  with the remote (`git pull --ff-only`).
- Find how the repo checks itself (lint, types, tests) in AGENTS.md / CLAUDE.md /
  CI config. You run these after every round of fixes.

## 1. Review round

Spawn a fresh reviewer every round — never reuse the previous one: a reviewer
that has seen your earlier fixes stops looking at them with fresh eyes.

- Claude Code: the Agent tool (`general-purpose`).
- No sub-agent tool (e.g. Codex): a separate process, `codex exec -s read-only "<prompt>"`
  or `claude -p "<prompt>"`.

The reviewer prompt must be self-contained:

- The diff: `git diff origin/<base>...HEAD` and `git log origin/<base>..HEAD --oneline`.
- The spec: the PR title and body, plus any issue they link (fetch it with `gh`).
- The standards: the repo's AGENTS.md / CLAUDE.md / CONTRIBUTING.md paths.
- If a `code-review` skill is available, tell the reviewer to follow it for the
  Standards and Spec axes.
- Always ask for **correctness bugs** too, as the first priority: wrong
  behaviour, races, data loss, money/security impact, missing error handling at
  real failure points. For each, a concrete failure scenario (inputs/state →
  wrong outcome).
- From round 2 on: the list of findings you dismissed earlier, each with your
  reason. The reviewer re-raises one only with a new argument against that reason.
- Output format: a numbered list, each item with `file:line`, severity
  (`bug` / `standards` / `spec` / `nit`), the problem, the failure scenario, and a
  suggested fix. "No findings" is a valid answer — say so to the reviewer, so it
  doesn't invent problems to fill the list.
- Read-only: the reviewer does not edit files, commit, or comment on the PR.

## 2. Triage

Verify every finding against the code yourself before acting — reviewers are
wrong often enough. Put each into exactly one bucket:

- **Fix** — real and inside the PR's scope.
- **Dismiss** — wrong, already handled, or a matter of taste the repo's standards
  don't settle. Write down a one-line reason; it goes into the next round's prompt
  and into the final report.
- **Ask the user** — real, but fixing it changes product behaviour, widens the
  PR's scope, or touches code outside the diff. Collect these; ask once, at the
  end of the round, with the AskUserQuestion tool when available. Don't block the
  other fixes on the answer.

## 3. Fix

- Fix the accepted findings, add or adjust tests that would have caught the bugs.
- Run the repo's checks; a check your changes turned red is fixed before
  pushing, not left for the next round. A check that is red on the base branch
  too is not yours — mention it in the report.
- Commit (one commit per round is fine, message says which findings it fixes),
  following the repo's commit conventions, and push to the PR's branch.
- Do not touch the PR title. Update the PR description only if the fixes change
  what it claims.

## 4. Loop or stop

- Round had accepted findings → back to step 1 with a new reviewer.
- Round had none (empty list, everything dismissed, or only items waiting on
  the user) → done. A re-raised dismissal with no new argument stays dismissed.
- After 5 rounds, stop anyway and report what is still open: a loop that long
  means the reviewer and you disagree on something only the user can settle.

## 5. Report

Tell the user, per round: what the reviewer found, what you fixed (with commit
SHAs), what you dismissed and why, and what waits on their decision. End with
the state of the checks and the PR URL.
