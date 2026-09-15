# The whole thing: S-1 to S-9 and N-1 to N-12 against a real local model (steps 9.7 and 10.1)

Run on 2026-09-15 on Mac Catalyst (macOS 26.5, Apple silicon) against `qwen3.5:4b` served by Ollama on
this machine, through the application's own window: the self-test driver (`TokkDb.Assistant.Application/SelfTest/selftest-97.sh`,
scripts `selftest-97a.txt` and `selftest-97b.txt` beside it) types what the person would type, attaches the
files, answers the cards, crashes the process where the scenario says so, and writes what came back and the
steps the trace shows. **Windows was not available on this machine**; the Windows run is a known gap, not
a known failure, and everything below the platform line is the same code.

The scenarios that cannot be forced on a real model - a wrong placement, a model failure, a schema change
in the window between a proposal and its write, a substituted proposal - are asserted by the scripted
tests named below, where the fake model does exactly what the scenario needs.

## What worked, what did not, and what the trace shows

Both parts ran to the end; the logs are `TokkDb.Assistant.Application/SelfTest/runs/selftest-97a.log` and `selftest-97b.log`, with nine
screenshots each in the application's data directory. Model calls are the steps marked with an asterisk
in the trace line the self-test writes.

| | what the person did | what they were told | what the trace shows | |
|---|---|---|---|---|
| S-1 | typed the two conferences | "Started keeping conferences and kept 2 of them." | the person → the assistant → what was meant\* → what is in the text\* → the placement → the new thing → the write → the answer (2 model calls) | worked |
| S-2 | attached `conferences-2025.csv`, "Here are more of them" | "Kept 4 more of conferences, and added 2 fields to it (name, notes)." | where it belongs\* → the placement → the new field ×2 → the write (1 model call) | worked |
| S-3 | "How much did I spend on conferences in 2025?" | "42440 in all, over 6 conferences." with the six shown | what was meant\* → what to look for\* → looking → the answer (2 model calls) | worked |
| S-4 | "Which was the most expensive?" | "Of the 6 conferences you were shown, the highest amount is Lviv at 12000." | answering from what was shown (no model call) | worked |
| S-5 | "The Lviv one was actually 13 000." | "Changed amount of Lviv from 12000 to 13000." | what to correct\* → the change, with the old value as the step's input and the new as its output | worked |
| S-6 | "Drop the notes I was keeping on these." | the card: "3 of your 6 conferences have a notes, and those values would go. This can be undone for 90 days…" | what would change\* → what it would do → the question | worked |
| N-8 | "no" | "Left as it is. Nothing was changed." | the question → the answer; the refusal is in the trace | worked |
| N-9 | asked again, then the process was killed with the card on screen; the application was reopened | the same question came back, "yes" gave "Dropped the notes from conferences. 3 values went with it." | the request had been held as waiting; startup reconciliation reopened its conversation and the answer executed what was shown, with no model call | worked |
| S-7 | after the crash and reopening | the browser shows "Conferences (6 of them)", and Lviv reads 13000 | nothing was asked; the file is the same | worked |
| S-8 | Browse → the overview → conferences → sorted by amount twice → the Lviv one → "what it keeps" | the table sorted, the record with every field, "location keeps some words. date keeps a day. amount keeps an amount. name keeps some words." | no request, no model call; then "Ask about this" put "About Lviv: " in the composer and "Which was the most expensive?" was answered from that record alone | worked |
| S-9 | clicked Suggest four times: with nothing stored, after keeping `conferences-2025.csv`, after the year's total was shown, and with "Show my" typed | with nothing stored: "Keep this: the conference in Lviv on 14 March 2025, 12 000 hryvnia", "Keep these as trips: …", "What can you keep for me?"; after the file: "Show my conferences from this year", "EuroPython should have a different cost: it was actually $600", "Remove EuroPython"; after the total: the same three about the conferences shown; with "Show my" typed: four continuations ("Show my conferences from 2025", "… in Europe", "… with cost over 5000") | what to say next\* as a request of its own (one model call; no turn added, the conversation unchanged); with nothing stored, no call at all | worked, see the note |
| IN-10, UI-3a | attached `133804_custom_campaigns_2.csv`, said "Keep these as 9lives", then "Keep these" with nothing attached again | "That could not be done: '9lives' cannot be a name for a thing: a name starts with a letter, then letters, digits or spaces. Say it again with a name like that, or leave the name to me."; then "Started keeping custom campaigns and kept 3 of them." - the attachment had stayed, and the model named the thing | where it belongs (refused, nothing stored); then what is in the file → what to call it\* → the placement → the new thing → the write | worked |
| N-1 | with conferences and trips both kept (see below), attached a third file of the same shape | the card: "Add these to conferences? It looks most like conferences: name as name, city as location… It could also be trips, which has name, city, date, cost, notes in common." | where it belongs\* → the placement → the question | worked |
| N-3 | attached `conferences-2025.csv` a second time | "Kept 0 more of conferences; skipped 4 already there, matched on everything in the row." | the write, with every row's disposition | worked |
| N-4 | attached `conferences-bad.csv` (100 rows, 3 unreadable) | "Kept 97 more of conferences. 3 could not be kept: line 12: "the 14th of never" in date is not date; line 42: "twelve thousand" in cost is not number; line 79: "n/a" in cost is not number." | one write, 97 dispositions kept and 3 rejected with their lines | worked |
| N-5 | attached `empty.csv` (a heading line and no rows) | "There was nothing in it to keep: headings but no rows. Nothing was stored." | what is in it → the answer, no write | worked |
| N-7 | asked the year's total and cancelled while the model was answering | "Stopped. Nothing was changed."; the request's state is Cancelled; the six conferences are as they were | the model step ends as cancelled and the request reaches Cancelled | worked |
| N-11 | "undo" after the 97-row import | "Put 97 of conferences back as they were." | what would be taken back → putting things back → the answer; the six original records stayed | worked, see the note |
| "Keep these as trips" | attached `trips-2025.csv`, which has conferences' shape | "Started keeping trips and kept 2 of them." | where it belongs (decided from the person's words, no model call) → the new thing | worked |

**S-9, a note (step 10.1, run later the same day with `selftest-run.sh selftest-suggest.txt fresh`; the log is
`runs/selftest-suggest.log`).** Three things the real model did before the operation took its final shape, each
now handled in C# as well as in the instructions: with nothing stored it wrote the assistant's own lines
("What do you want to store?") - so with nothing stored, nothing said and nothing typed the starters are shown
without a call; with no names to use it invented places and records ("Keep the conference in Tokyo on March
15th", "Remove the entry for the New York conference"), and copied the names from the instructions' examples
when those had any - so the content lists a few real titles (those last shown, or of the thing lately kept),
the examples are name-free shapes, and an option with a placeholder left in is dropped; and given the titles
it proposed keeping them again with made-up amounts ("Keep this: EuroPython, 2025, $150") - so a "Keep" option
that names a listed title is dropped. Each click took one to three seconds and about 460 prompt tokens against
the 1,800 allowed (`docs/assistant-token-budget.md`, S-9: 4 of 4 real runs, one call each).

**IN-10 and UI-3a, a note (step 10.2, `selftest-run.sh selftest-naming.txt fresh`; the log is `runs/selftest-naming.log`).**
The file's name starts with digits, which the storage's rule for names refuses; before this step the import
failed at the write with that rule's message, and the attachment went with the failed message, so a bare
"store" that followed had nothing to keep and was read as a search. Now the model names the thing from the
fields and examples ("custom campaigns" here), C# checks the name before use, and an attachment comes back
to the composer when its turn fails.

**N-11, a note.** In the first run of the day, before the application's error log existed, the same
"undo" ended with "Something went wrong: Index was outside the bounds of the array" after the step
"putting things back", and the import stayed. It did not recur in two later runs of the same script, and
`UndoOverTheFileTests` replays the whole sequence over the engine-backed storage without it. The
application now writes every unhandled failure with its stack to `errors.log` in its data directory, so
the next occurrence will say where it was.

**Asserted by the scripted tests rather than run here**, because a real model cannot be made to fail
on cue:

- **N-2** (a valid but wrong placement): `PlacementTests` and `AssistantScenarioTests` assert that the
  decision is in the reply and the diagram and that a correction in one turn moves the records.
- **N-6** (the model fails, malformed past the bound and a timeout): `OperationCatalogTests` and
  `ScriptedModelTests`, and the harness's `Failures_are_counted_by_mode…` - each ends with a message
  naming what could not be done and no partial write.
- **N-10** (a schema change underneath): `AssistantScenarioTests.N10…` re-plans against the changed
  shape and asks again.
- **N-12** (a substituted proposal): `AssistantScenarioTests.N12…` refuses on the hash.

**The accessibility inspection** the self-test made on each surface: 39 interactive elements on the
conversation, 72 on the overview, 76 on a record - every one named, with the focus order starting at
the surface switch and running through the composer, the replies' "what happened" and "open in the
browser" buttons, then the browser's rail, headings, rows and the record's actions.


## Recorded as known limitations, with the reason

- **Windows (UI-7, 9.7):** no Windows machine was available; the run and the accessibility inspection
  are Mac Catalyst only.
- **A descending sort is a sort (BR-3, R-1):** the engine walks an index forward only, so "largest first"
  examines every record for each page - exact and quick at ten thousand, but its cost grows with the thing.
- **"Last year" on a 4B model:** with the calendar reading and a worked example in the query operation's
  instructions the model reads it right in about seven runs of eight; the eighth takes the twelve-month
  reading and finds nothing. The reply says so ("Nothing among conferences matches that") rather than
  guessing.
- **The model's names for fields:** S-1's prose is stored under the names the model chose (this run:
  location, date, amount; an earlier run: city, date, cost). S-2's spreadsheet then maps its headings onto
  them. The person sees consistent names; the run's scripts had to address columns by number.
- **Keyboard-only completion by a person** was not exercised; every control is reachable by Tab and named,
  which the `a11y` inspection shows, but nobody sat at the keyboard.
