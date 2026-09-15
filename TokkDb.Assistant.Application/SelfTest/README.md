# The application's self-test

`SelfTest.cs` drives the window from a script file in the application's data directory: what to say,
files to attach, answers to cards, the browser's actions, an accessibility inspection, a cancellation and
a crash. The two shell scripts here copy the fixtures into the sandboxed container, write the script,
launch the built `.app`, wait for the log, and print it.

- `sh TokkDb.Assistant.App/SelfTest/selftest-run.sh <script> [fresh]` runs one script (`fresh` starts from
  an empty database).
- `sh TokkDb.Assistant.App/SelfTest/selftest-97.sh` runs step 9.7: part A ends in a crash with a card on
  screen, part B reopens on the same database.

Scripts: `selftest-s1-s6.txt` (steps 5.1 and 5.2), `selftest-browse.txt` (Phase 6), `selftest-diagram.txt`
(Phase 7), `selftest-97a.txt` and `selftest-97b.txt` (step 9.7). Fixtures: `conferences-2025.csv`,
`conferences-bad.csv` (100 rows, 3 unreadable), `empty.csv` (a heading line), `trips-2025.csv`. The logs of
the 9.7 run on 2026-09-15 are in `runs/`; the screenshots stay in the container.

Build first: `dotnet build TokkDb.Assistant.App/TokkDb.Assistant.App.csproj -f net10.0-maccatalyst`.
