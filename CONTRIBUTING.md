# Contributing

Thanks for taking a look. This is a small tool with a deliberately narrow scope, so most
contributions land quickly when they respect the constraints below.

## Before you start

Open an issue for anything that touches the **scanner**, the **database schema** or the
**report structure**. Several current choices look arbitrary from the source but are not:
the target is a single `net45` build that still runs on Server 2012, the process is x64 only,
and no API newer than Windows 8 may be used. A patch that "modernises" any of those will be
rejected, however clean it looks.

Documentation fixes, clearer error messages, additional knowledge-base entries and
reproduction cases are welcome without an issue.

## Build

Requires the .NET SDK (any version that can target `net45`) on Windows.

```console
dotnet build -c Release
# output: bin\Release\net45\DiskGrowthMonitor.exe
```

Or open `DiskGrowthMonitor.csproj` in Visual Studio 2022 and build `Release|x64`. The project
targets `net45` alone; there is no multi-target build, on purpose.

### Publishing a runnable folder

```console
dotnet publish -c Release -o <output-dir>
```

After publishing, confirm that **`x64\SQLite.Interop.dll` is present** in the output. It is a
native library loaded at runtime; if it is missing, the program starts and crashes
immediately. The build declares this explicitly because the default SQLite package targets
did not copy it reliably.

## Verify a change

Do not test against your own `disk_growth.db` — use a throwaway database and a small tree:

```console
DiskGrowthMonitor.exe -r D:\some\small\tree --data-dir D:\tmp\dgtest --open
DiskGrowthMonitor.exe -r D:\some\small\tree --data-dir D:\tmp\dgtest --open   # second run
```

A change is ready when:

1. `--show-env` still reports a strategy and the chosen engine.
2. Two consecutive runs produce a report whose second run shows a real comparison (not
   "first run"), and whose numbers reconcile with the tree you scanned.
3. Deleting `D:\tmp\dgtest\disk_growth.db` and re-running still works (clean-install path).
4. For enumeration changes: `--enum-engine win32` and `--enum-engine auto` produce
   **identical** totals on the same tree. They are supposed to differ only in speed.

The demo data used for the screenshots in `docs/` comes from a synthetic tree; nothing in
the repository contains a real machine's snapshot.

## Constraints

| Constraint | Why |
|---|---|
| Single `net45` target | Windows 7 SP1 / Server 2012 and later ship .NET Framework 4.5+. Multi-targeting would force a second artifact and a second set of bugs |
| Do not reference `netstandard2.1` / `net5+` assemblies | `Compat/NetFxPolyfill.cs` re-declares BCL types that only exist there; both being visible produces `CS0101` |
| `PlatformTarget = x64`, `Prefer32Bit = false` | A 32-bit process runs out of address space on a million-directory scan |
| No API newer than NT 6.2 | The capability floor is Server 2012. `NtQueryDirectoryFileEx` (RS3), `ON_DISK_ENTRIES_ONLY` (1709) and `Span<T>` are all out |
| Report stays a single HTML file | Inlined CSS/JS, no external requests. It has to open offline and survive being forwarded by email |
| Knowledge base is append-only | Existing `dir_knowledge` rows are user data. Add entries; do not renumber, rewrite or delete them |
| Schema changes need a discussion | The database is the user's baseline. A breaking change means everyone loses their comparison history |

## Pull requests

- One logical change per pull request; keep unrelated formatting out of the diff.
- If the change is user-visible, update **both** `README.md` and `README_zh.md` — they are
  maintained as parallel documents, not as a translation of convenience.
- State how you verified it: the command line, the tree you scanned, the before/after
  numbers. "It builds" is not verification for anything that touches scanning or SQL.
- Do not bump the version or edit `CHANGELOG.md`; that happens at release time.

## Reporting bugs

Use the issue form. The three things that make a report actionable:

1. The exact command line.
2. The full output of `--show-env` (it records OS, media type, engine and parallelism).
3. If a number looks wrong: the "volume reconciliation" block and the skip list from the
   report. Never attach `disk_growth.db` itself — it contains a full path listing of your
   machine.
