# ParkNest docs

Everything that isn't code: the spec, the decisions behind the code, what happened when, and
what's next.

## Contents

| Document | What it's for |
|---|---|
| [system-overview.md](system-overview.md) | **Start here.** Problem, solution, what's built so far, the database structure, and where load will hurt. |
| [PRD.md](PRD.md) | The product spec. Source of truth for scope; section numbers (§5, §9, §13…) are referenced from code comments. |
| [architecture.md](architecture.md) | How the current code is actually laid out, and where it diverges from the PRD's target architecture. |
| [ledger-model.md](ledger-model.md) | The double-entry account model, every transaction shape, and the invariants. Read this before touching money code. |
| [backlog.md](backlog.md) | Phased task list with current state. |
| [adr/](adr/) | Architecture decision records — one file per decision that would otherwise get re-litigated. |
| [worklog/](worklog/) | Dated session notes: what was built, what was decided, what broke. |

## Conventions

**Work log.** One file per working session, named `worklog/YYYY-MM-DD.md`. Record what changed and
why, decisions taken, and anything left dangling. It is a narrative, not a changelog — git already
has the changelog.

**ADRs.** Numbered, immutable once merged. If a decision is reversed, write a new ADR that
supersedes the old one rather than editing it; the reasoning that turned out wrong is the
useful part.

**PRD references.** Code comments cite PRD sections (`PRD §5.1.4`) rather than restating the rule.
If a section number moves, the citations need updating — keep section numbering stable.
