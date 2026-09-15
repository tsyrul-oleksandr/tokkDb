#!/bin/sh
# 9.7 on Mac Catalyst: part A ends in a crash with a question on screen; part B relaunches on the same database.
C="$HOME/Library/Containers/com.tokkdb.assistant/Data/Library"
APP="/Users/ts/Student/db/tokkDb/TokkDb.Assistant.App/bin/Debug/net10.0-maccatalyst/maccatalyst-arm64/Storage.app"
mkdir -p "$C"
for f in conferences-2025.csv conferences-bad.csv empty.csv trips-2025.csv; do cp "/Users/ts/Student/db/tokkDb/TokkDb.Assistant.App/SelfTest/$f" "$C/$f"; done
pkill -f "Storage.app/Contents/MacOS/TokkDb" 2>/dev/null; sleep 1
rm -f "$C/storage.db" "$C/storage.db.wal" "$C/storage.db.lock" "$C/selftest.log"; rm -f "$C"/selftest-0*.png 2>/dev/null
sed "s#\$C#$C#g" /Users/ts/Student/db/tokkDb/TokkDb.Assistant.App/SelfTest/selftest-97a.txt > "$C/selftest.txt"
open -n "$APP" || exit 1
i=0
until grep -q "crash:" "$C/selftest.log" 2>/dev/null || [ $i -ge 300 ]; do sleep 2; i=$((i+1)); done
sleep 3
echo "--- part A"; cat "$C/selftest.log"
cp "$C/selftest.log" "$C/selftest-97a.log"
mkdir -p "$C/shots-a"; mv "$C"/selftest-0*.png "$C/shots-a/" 2>/dev/null
sed "s#\$C#$C#g" /Users/ts/Student/db/tokkDb/TokkDb.Assistant.App/SelfTest/selftest-97b.txt > "$C/selftest.txt"
rm -f "$C/selftest.log"
open -n "$APP" || exit 1
i=0
until grep -q "self-test finished" "$C/selftest.log" 2>/dev/null || [ $i -ge 300 ]; do sleep 2; i=$((i+1)); done
echo "--- part B"; cat "$C/selftest.log"
cp "$C/selftest.log" "$C/selftest-97b.log"
echo "--- screenshots A: $(ls "$C/shots-a" | wc -l), B: $(ls "$C" | grep -c 'selftest-0')"
