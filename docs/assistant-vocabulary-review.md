# Vocabulary review (UI-2, BR-6, step 9.3)

Every user-visible string of the assistant belongs to one of three tiers, decided by **where** it
appears (UI-2). The review is enforced by `VocabularyTests` in `TokkDb.Assistant.Tests`, which reads the
first tier's sources and fails on any string literal that says *collection, column, index, schema,
query, constraint, nullable, database, SQL, primary key* or *foreign key*. Names of things and fields
come from the person's own words and are shown with underscores turned into spaces.

## First tier — plain words, no exceptions

The conversation and the browser's primary surfaces, including the names a screen reader says there.

| source | what it holds |
|---|---|
| `Replies.cs` | every sentence the assistant says |
| `ConfirmationCard.cs` | the question, the loss in counts and examples, the undo note |
| `StructuralAction.cs` | how each change is described ("drop the notes from conferences") |
| `ChangeClassifier.cs` | the evidence sentences ("3 of your 6 conferences have a notes, and those values would go") |
| `WhatItKeeps.cs`, `ThingsOverview.cs`, `RecordDetail.cs`, `TableFilter.cs` | the browser's words: kinds of value, notes, relation headings, filters |
| `BrowseSurface.cs`, `BrowseViewModel.cs` | the browser's screens and their accessibility names |
| `Suggestions.cs` | what the person could say next (UI-9): the starters C# writes, and the words a model's suggestion may not contain |
| `ConversationView.cs`, `ChatViewModel.cs`, `MainPage.cs`, `ConversationList.cs` | the conversation surface, the composer, the surface switch |

Words used instead: *thing* (collection), *field* (column), *kind of value* (type), *what it keeps*
(schema), *always needed* (required, not null), *no two the same* (unique), *set once and kept*
(read-only), *narrow it down* (filter), *sort* (order by), *find* (query), *put back* (undo), *changed
underneath* (stale cursor).

## Second tier — technical on purpose

The diagram's detail panel (`App/Diagram/DetailViews.cs`) and the diagnostics line under the table
(`BrowseSurface.cs`, the accessibility hint that carries the planner's description). Inspection is their
purpose, so they say "full scan of conferences (no index on date)", "prompt hash", "peak context",
"index walk". `StepsPane.cs` shows a step's raw input and output for the same reason.

## Third tier — technical with a plain gloss

"What this keeps" (BR-6) is the worked example: structure shown in plain words, each field as "name
keeps a kind; notes". The changed-underneath banner says "these have changed since you opened them, so
this list may be out of date" rather than "the cursor is stale". A record's field waiting to be made sense
of says so instead of "conversion failed".

## Reviewed 2026-09-15

The test passes over the sources above. Two things were corrected during the review: change descriptions
and evidence sentences printed stored names with underscores ("conferences_2025"), and the browser
opened with "That is no longer kept" for a name that never existed; both now use the person's words.
