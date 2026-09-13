# Fixtures

## pre-versioning.db

A database written by the engine **before any versioning code existed**, for step 0.2 of
`docs/versioning-requirements-and-plan.md`. It was written on 2026-09-13 by
`PreVersioningFixture.Generate` (in this directory), run once against the engine at commit
`4c08108fc4e878891f6b6392d0a5ccb3ae61456d` — the last commit before Phase 0 of that plan. Its root
page carries format version 2.

**This file must never be regenerated.** Its only value is that a pre-versioning engine wrote it:
NF-7, G-9 and S-4 of the versioning plan open it to show that a later engine reads it unchanged and
that switching versioning on changes nothing already stored. A file written again by a later engine
would prove nothing. If the engine ever stops opening it, that is a finding about the engine, not
about the file.

Tests never open it in place. `PreVersioningFixture.Copy()` copies it to a temporary file and the
test opens the copy; the build copies it beside the test assembly for that purpose
(`TokkDb.Tests.csproj`). `.gitattributes` marks it binary.

What it holds, in the order it was written:

1. `Conference`: 50 records, `Id` 1 to 50, with `Id` declared unique — the unique index.
2. `Expense`, and the relation `ExpenseConference` from `Expense.ConferenceId` to `Conference.Id`.
3. 45 expenses, `Id` 1 to 45, under schema version 1, whose text column was called `Note`.
4. Expense 7 updated three times, each update its own transaction: copy-on-write, so its first
   three images were retired.
5. `Note` renamed `Comment` through `SetColumns`, without `Rewrite`: the collection is at schema
   version 2 with the rename in its migration log, and the 45 records are still at version 1.
6. Five more expenses, `Id` 46 to 50, under schema version 2.
7. Expense 13 deleted.

So 50 conferences and 49 expenses are live. `PreVersioningFixture` states every value in code, and
`PreVersioningFixtureTests` reads every record of the file against it.
