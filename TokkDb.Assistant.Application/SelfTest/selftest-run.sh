#!/bin/sh
# Drives the app's self-test: the script to run is $1 (lines as SelfTest.cs reads them, with
# $C standing for the container's Library directory); waits for the log to say it finished.
C="$HOME/Library/Containers/com.tokkdb.assistant/Data/Library"
APP="/Users/ts/Student/db/tokkDb/TokkDb.Assistant.App/bin/Debug/net10.0-maccatalyst/maccatalyst-arm64/Storage.app"
pkill -f "Storage.app/Contents/MacOS/TokkDb" 2>/dev/null; sleep 1
mkdir -p "$C"
cp /Users/ts/Student/db/tokkDb/TokkDb.Assistant.App/SelfTest/conferences-2025.csv "$C/conferences-2025.csv"
if [ "$2" = "fresh" ]; then rm -f "$C/storage.db" "$C/storage.db.wal" "$C/storage.db.lock"; fi
rm -f "$C/selftest.log"; rm -f "$C"/selftest-0*.png 2>/dev/null
sed "s#\$C#$C#g" "$1" > "$C/selftest.txt"
open -n "$APP" || exit 1
i=0
until grep -q "self-test finished" "$C/selftest.log" 2>/dev/null || [ $i -ge 450 ]; do sleep 2; i=$((i+1)); done
echo "--- self-test log"; cat "$C/selftest.log" 2>/dev/null
echo "--- screenshots: $(ls "$C" 2>/dev/null | grep -c 'selftest-0')"
