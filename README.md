# DiskGrowthMonitor · Disk Space Growth Monitor

**English** | [简体中文](README_zh.md)

Recursively scans per-directory disk usage → stores it in a local SQLite database → on the next run, compares it **directory by directory** against the previous snapshot → reports the fastest-growing and most-shrinking directories and generates a self-contained HTML report. Known directories (browser caches, system temp files, logs, crash dumps, application data, …) are annotated with their **purpose and whether they are safe to delete**.

> It answers exactly three questions: **what is eating the disk, how much did it grow, and can it be deleted?**

A single Windows executable with zero prerequisites — copy the folder and run. Suitable for tracking disk usage over time from Task Scheduler, or for one-off hunts for "why is C: full again".

---

## Features

| Feature | Details |
|---|---|
| **Zero prerequisites** | Targets .NET Framework 4.5 — included with Windows 7 SP1+ and Windows Server 2012+. The native SQLite library ships with the app and depends on neither the VC runtime nor UCRT |
| **Growth comparison** | Every run is stored as a snapshot and diffed against the previous one per directory: growth, shrink, new and removed, with red-up / green-down colouring |
| **Physical hard-link deduplication** | Deduplicates by `(volume serial, file ID)`, so hard-link-heavy trees such as `WinSxS` do not inflate. Measured: **15.89 GB / 71k files** avoided from double counting |
| **Directory knowledge base** | **66** built-in entries (system temp/cache/logs/dumps, Chrome / Edge / Firefox, WeChat / DingTalk / Teams, VS Code / NuGet / pip, …). Report rows are annotated in place with purpose and one of three cleanability levels. Insert-only, and you can add your own |
| **Knowledge-base exemption** | Directories matching the knowledge base (or having a matching ancestor) are re-attempted every run, so a stale "access denied" record or a default exclusion can never turn them into a **permanent blind spot** |
| **Incremental reuse** | Uses the NTFS USN change journal to detect whether a scan root actually changed; unchanged roots **reuse the previous snapshot wholesale**, finishing in seconds |
| **Adaptive strategy** | Probes OS, storage media and file system at startup, then picks the enumeration engine and parallelism **from per-volume runtime measurements** instead of hard-coded parameters |
| **Bilingual** | Console messages and the HTML report are bilingual (Chinese / English); `--lang=auto\|zh\|en`, defaulting to automatic detection from UI language + console code page |
| **Self-contained report** | Single HTML file with inlined CSS/JS — no external dependencies, opens offline, safe to forward. Supports path search, column sorting and status filtering |
| **Crash-safe** | Two-phase commit: the current snapshot is never touched during a scan, so a crash or power loss still leaves the previous baseline intact |
| **Automatic elevation** | Prompts for UAC and restarts as administrator when a scan needs it, **attaching to the current terminal** so the progress bar and output look identical. Declining UAC degrades gracefully and keeps running |

---

## Performance

Measured on Windows 11 (Build 26200), AMD Ryzen 7 7840H (8C/16T), NVMe SSD, C: holding roughly **191k directories and 700k files**. Timings are wall-clock as reported by the program, with runs interleaved.

| Scenario | Time | Notes |
|---|---|---|
| Full C:, first run (create DB + full scan + commit + report) | **≈ 20 s** | end to end |
| Full C:, subsequent run (nothing changed; whole root reused) | **≈ 1.6 s** | end to end |
| Full C:, subsequent run with changes (incremental rescan) | seconds — tens of seconds | depends on change volume |
| Subtree `C:\Windows\System32` (~1.6k dirs / 20k files) | ≈ 0.2 s | |
| Subtree `C:\Windows\WinSxS` (121k dirs / 116k files) | ≈ 11.7 s | the most hard-link-dense area on the volume |
| Scanner core only (`--no-knowledge-rescan`, excluding the `C:\Windows` tree) | 25.2 s first / 19.9 s second | 49k dirs / 278k files / 90.2 GB |

**Enumeration engine comparison** (same tree, same time window, interleaved runs; all engines produce identical results, only speed differs):

| Engine | Full C: | Mechanism |
|---|---|---|
| `auto` (default; resolves to `extd` or `both` by measurement) | **14.4 s** | The directory entry already carries the file ID, so deduplication needs no per-file handle |
| `win32` | 46.9 s | Compatibility fallback: `FindFirstFileExW` plus a `CreateFileW` per file to fetch the ID |

### Accounting for the numbers

The program reports the **sum of logical file sizes after deduplication**. Explorer's "used space" counts **allocated clusters**, so a 2–4% gap is inherent (MFT reserved area, metadata, compression, cluster rounding) — **not a missed scan**:

| Measure | This machine, C: |
|---|---|
| This scan (elevated, full) | 285.48 GB / 191,902 dirs / 704,963 files |
| Volume used space | 293.68 GB (gap **+2.77%**) |

Permissions are the largest source of deviation: running without elevation under-reports by roughly 4% (about 12.5 GB here). The report and console **state the gap and its three structural causes explicitly**, and never emit an estimated breakdown of it.

### Footprint and load

| Item | Notes |
|---|---|
| Database size | ~15–25 MB per 100k directories. Only the current and previous snapshots are kept, so it **does not grow over time** |
| Disk load | Single-threaded sequential I/O; it will not saturate the disk queue, though mechanical drives still feel sluggish |
| Antivirus | Real-time scanning slows it down noticeably; consider excluding the program directory |
| Long-term use | Schedule it with Task Scheduler, e.g. once a week |

---

## Quick start

```bash
# 1) Build (single target: net45)
dotnet build -c Release
#    Output: bin\Release\net45\DiskGrowthMonitor.exe

# 2) First run — establish the baseline (no comparison yet)
DiskGrowthMonitor.exe -d ALL

# 3) Run again later — get the growth report
DiskGrowthMonitor.exe -d ALL --open
```

> **You need two runs before any growth data appears.** The first run only establishes a baseline; the second is what shows "what grew".
>
> **Note**: the database is fixed at `<exe directory>\disk_growth.db`, so Debug / Release and every copied folder each get their own database. Stick to one copy to avoid comparing two different datasets.

### Deploying to a target machine

Copy the files from `bin\Release\net45\` together (`publish\` has the same content as the build output):

| File | Purpose |
|---|---|
| `DiskGrowthMonitor.exe` | main program (x64) |
| `DiskGrowthMonitor.exe.config` | declares the required .NET Framework version and GC mode |
| `System.Data.SQLite.dll` | managed SQLite driver |
| `System.ValueTuple.dll` | tuple support (not in the BCL before .NET Framework 4.7) |
| `x64\SQLite.Interop.dll` | native SQLite engine; **must stay in the `x64` subdirectory**, missing it means an instant crash |
| `DiskGrowthMonitor.pdb` | debug symbols, optional |

| Item | Requirement |
|---|---|
| Prerequisites | **None.** Windows 7 SP1 and Windows Server 2012 or later all ship .NET Framework 4.5+ |
| Architecture | **64-bit only** (the exe carries an x64 machine header and cannot be loaded by a 32-bit host) |
| Folder permissions | The program folder must be **writable** (database and reports live there). Avoid `C:\Program Files` |
| Run privileges | **Running as administrator is recommended**: fewer access-denied directories, plus USN incremental reuse and accurate progress percentages. Running unelevated **will not error out**, but every run degrades to a full scan |
| Run artifacts | Delete `disk_growth.db` and `reports\` before copying, otherwise the target machine's first run builds on your old data |

---

## Command-line reference

### Scan scope (exactly one required; both may be combined)

| Option | Description |
|---|---|
| `-d, --drive <ALL\|C\|D,...>` | Scan all local **fixed** drives, or the listed drive letters (comma-separated) |
| `-r, --root <directory>` | Scan only the given directory (repeatable, for monitoring a few specific trees) |

When `-r` and `-d` overlap, covered subdirectories are **merged** into the parent so no path is recorded twice.

### Report content

| Option | Default | Description |
|---|---|---|
| `-t, --top <N>` | 30 | Number of growth / shrink leaderboard entries |
| `--detail-top <N>` | 500 | Maximum rows in the detail table |
| `-m, --min-mb <N>` | 100 | Only report directories growing by more than N MB |
| `-p, --min-percent <N>` | 0 | Only report directories growing by more than N% |
| `--detail-min-mb <N>` | 1 | Minimum change for the detail table |

> If nothing crosses the threshold in a run, the report automatically falls back to showing the N largest movers and says so on the page.

### Exclusion and skipping

| Option | Description |
|---|---|
| `-x, --exclude <path prefix>` | Extra exclusion (repeatable); takes the highest priority |
| `--no-default-exclude` | Keep the default exclusions (`Windows` / `$Recycle.Bin` / `System Volume Information`) |
| `--reset-skips` | Clear recorded "access denied / I/O error" entries and retry those directories |
| `--no-knowledge-rescan` | **Disable the knowledge-base exemption**, so matching directories no longer bypass default exclusions or the skip list (the exemption is on by default) |
| `--follow-reparse` | Follow directory symlinks and junctions (**off by default**) |
| `--no-dedup` | Disable hard-link deduplication (faster, but double counts) |

### Performance

| Option | Default | Description |
|---|---|---|
| `--enum-engine <engine>` | `auto` | Directory enumeration engine: `auto`, `extd`, `both` or `win32`. **All four produce identical results; only speed differs** |
| `--threads <N>` | `0` | Parallel scan threads. `0` = one per scan root, **capped at the physical core count**; an explicit value may go up to the logical core count. ⚠ Parallelism is per **scan root**, so scanning a single drive gains nothing |
| `--full` | — | Force a full scan, disabling USN incremental reuse |
| `--no-elevate` | — | Do not attempt to restart with administrator rights |

Engine values:

| Engine | Mechanism | When to use |
|---|---|---|
| `auto` | Measures per volume and falls back `extd → both → win32`, caching the result per volume | **Default**; does not rely on documented availability claims |
| `extd` | `NtQueryDirectoryFile` + `FileIdExtdDirectoryInformation`, directory entries carry a **128-bit** file ID | Modern systems; fastest in local tests |
| `both` | Same, 64-bit variant (available since Vista / Server 2008) | Safety net on older systems; measured within 1% of `extd` |
| `win32` | `FindFirstFileExW` plus a `CreateFileW` per file | Compatibility fallback — switch with no code changes if anything misbehaves |

### Output and storage

The database is fixed at `<exe directory>\disk_growth.db` with **no switch**; a leftover `--db` in old scripts is ignored with a warning line.

| Option | Description |
|---|---|
| `-o, --out <path>` | HTML report path; defaults to `<exe dir>\reports\disk_growth_report_<timestamp>.html` |
| `--no-report` | Scan and store only, do not generate a report |
| `--open` | Open the report in the default browser when done |
| `--lang <auto\|zh\|en>` | Language used by the program's own messages; `auto` means Chinese only when both UI language and console code page are Chinese |
| `--show-env` | Probe and print OS / CPU / memory / media / file system / language plus the derived scan strategy, then exit; **no scan, no database writes** |
| `--list-drives` | List local drives and exit |
| `--list-skips` | List the recorded skip list and exit |
| `--list-knowledge` | List the directory knowledge base and exit |
| `--reset-db` | Wipe all records, returning to "never scanned" (interactive `y/N` confirmation; the `dir_knowledge` table is kept) |
| `--dev` | Developer mode: extra diagnostics (environment details, incremental decisions, MFT estimates, engine probing, …); off by default |
| `-h, --help` | Show help |

### Examples

```bash
# Scan every fixed drive and open the report when done
DiskGrowthMonitor.exe -d ALL --open

# Only C:, growth above 500 MB, top 50
DiskGrowthMonitor.exe -d C -m 500 -t 50

# Monitor just two business directories (far faster than a full scan)
DiskGrowthMonitor.exe -r D:\Build -r D:\Data -m 10

# After fixing permissions on a directory, bring it back into scope
DiskGrowthMonitor.exe -d C --reset-skips

# Drop just the big C:\Windows tree, leaving every other exemption in place
DiskGrowthMonitor.exe -d C -x C:\Windows

# Turn the knowledge-base exemption off to get back to ~20 s scans
DiskGrowthMonitor.exe -d C --no-knowledge-rescan

# Diagnose "why this engine / parallelism" without scanning anything
DiskGrowthMonitor.exe --show-env
```

---

## How it works

**Single-pass recursion with bottom-up accumulation.** Every directory is enumerated exactly once: file sizes are summed as they are seen, subdirectories are recursed into and their subtree totals added, and the directory's own row is written on the way back. Each file-system entry is therefore visited **exactly once**, avoiding the O(depth × file count) blow-up of summing each directory independently.

**File IDs come back with the directory entry, so deduplication no longer opens a handle per file.** Hard-link deduplication keys on `(volume, file ID)`. The default NT enumeration path (`extd` / `both`) returns that ID alongside each entry, so deduplication costs a single hash insert; only the `win32` fallback engine needs an extra handle per non-empty file. The dedup set is isolated per **volume serial number** (one physical volume can carry several drive letters, and grouping by letter would silently miss duplicates), then sharded with per-shard locks.

**Two-phase commit, so a crash never costs you the baseline.** Before scanning, the previous snapshot of each affected root is copied into `dir_snapshots_prev`; results go into a stage table during the scan; and afterwards a **single transaction** deletes the old rows, moves the staged data in, records the batch and writes growth history. The live snapshot is untouched for the whole scan, so a crash or power loss still leaves the previous baseline intact and the next run can compare correctly.

**Comparison runs inside SQL.** Results are materialised into a temp table, indexed, and then sorted for each leaderboard — **millions of rows are never pulled into managed memory**. Growth is `current − previous`; a zero or missing previous value is reported as "new".

**Reparse points are not followed.** Junctions such as `C:\Users\All Users` and `C:\Documents and Settings` are not recursed into by default (following them would count the same subtree two or three times), and they are recorded in the skip list.

**Skip list and knowledge-base exemption.** All five skip reasons (default exclusion / user `-x` exclusion / access denied / I/O error / unfollowed symlink) are recorded in `skip_paths`. Access-denied and I/O-error entries are **skipped outright** next run instead of re-raising exceptions. But if a directory **matches the knowledge base** — itself or via an ancestor — it is let through and **retried every run**. That is precisely what breaks the blind spot created by stale permission records; the full `C:\Windows` tree (of which `C:\Windows\Installer` alone is 159.8 GB) becomes visible again, at a cost of roughly 45 extra seconds per run (narrow it with `-x C:\Windows` if you prefer).

**Progress display.** Each scan root occupies two lines (progress bar and current path) plus one total line at the end, redrawn in place with absolute cursor positioning — no scrolling, no flicker — and it goes silent automatically when output is redirected. The percentage denominator prefers the previous snapshot's directory count; on a first unelevated run it degrades to "bytes scanned / volume used space" (marked `~`) or shows only the scanned amount.

---

## Environment and adaptive strategy

The target is **three heterogeneous tiers** of Windows: Server 2012 (NT 6.2, the capability floor), Windows 10 and Windows 11. Hard-coding one parameter set would collapse to their intersection, so the program probes first and decides the **enumeration engine, parallelism and dedup strategy** from the result:

```bash
DiskGrowthMonitor.exe --show-env      # probe and print only; no scan, no database writes
```

| Probe input | Derived strategy |
|---|---|
| OS version (via `RtlGetVersion`, not `GetVersionExW`, which shims lie through) | ≥ 6.2 enables NT enumeration candidates and measures a fallback chain per volume; older uses Win32 enumeration directly |
| Media type (SSD / HDD, derived from the volume → physical disk mapping) | SSD: candidates `{physical/2, physical, physical+4, logical}`; HDD: `{1, 2, 3, 4}` (high concurrency hurts when seeks are expensive) |
| Media unknown (unelevated) | Narrowed to `{1, physical}`, letting measurements decide the rest |
| Whether the volume supports hard links | If no volume does (FAT32/exFAT), deduplication is disabled entirely, saving a per-file handle |

> 🔴 The OS version must come from ntdll's `RtlGetVersion`: since Windows 8.1, `Environment.OSVersion` and `GetVersionExW` are hijacked by the application-compatibility shim and **lie that the OS is 6.2** unless the process manifests support for newer versions.
>
> ⚠ **Drive letters and physical disk numbers are unrelated.** On multi-disk machines (especially NVMe plus SATA), the system disk may well be `PhysicalDrive1/2/3`; assuming `C: == 0` would pick the wrong media type. The program asks `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`, letting the volume manager answer directly.
>
> "Media unknown" and "media is a mechanical disk" are different things, distinguished in code by `bool?` (`null` = unknown). Treating a failed probe as "not an SSD" would run a 16-thread NVMe machine single-threaded.

---

## HTML report

The report is a **self-contained single file** (inlined CSS/JS, no external dependencies, opens offline, safe to forward) with a light theme.

- **Overview**: KPI cards (total usage, change vs. last run, directories, files, elapsed time, skipped), change scale (grown / shrunk / new / removed) and the configuration used for this scan
- **Safe-to-clean directories**: matching knowledge-base entries whose cleanability is *safe*, with a total
- **Per scan root**: current vs. previous usage, delta and rate, directory/file counts, volume capacity and free space, plus the **volume-used vs. measured gap** and its structural causes
- **Top N fastest growing / most shrinking**: delta, rate, current and previous usage, file-count change, relative magnitude bar
- **New / removed directories**, sorted by size
- **Full detail table**: path keyword search, click-to-sort headers, status filtering, and a "deepest level only" toggle that hides parents already accounted for by their children
- **Skip list**: grouped and collapsible by reason, marking which entries will be skipped automatically next time and which are retried every run
- **Annotations for known directories**: rows matching the knowledge base get a small caption underneath (`▸ Chrome web cache · safe to delete`), colour-coded by cleanability, with the full explanation on hover — **no extra columns**, no horizontal scrolling

> Seeing both a parent and its children in a leaderboard is **expected**: the parent's number already includes the children. Overlap between directory-level changes is a property of the data, not double counting.

---

## Database schema

The database lives at `<exe directory>\disk_growth.db` and can be queried with any SQLite tool.

| Table | Purpose |
|---|---|
| `scan_runs` | Per-run summary (time, roots, file/directory counts, total bytes, elapsed, first-run flag) |
| `dir_snapshots` | **Current** directory snapshot, one row per directory (path, depth, subtree bytes, file and directory counts) |
| `dir_snapshots_prev` | **Previous** snapshot, for the roots being rescanned; rebuilt before each scan |
| `growth_history` | Growth entries that made it into a report, for long-term lookback |
| `skip_paths` | Skip list (five reasons, hit counts, first and last batch seen) |
| `dir_knowledge` | Directory knowledge base (configuration data, **unaffected by `--reset-db`**; 66 built-in entries with bilingual fields) |

Every value is a **subtree total** including all descendants; the row with `depth = 0` is the scan root itself.

---

## FAQ

**Q: Why is the total smaller than the "used space" Windows reports?**
A: First rule out the two causes. A **structural gap** of 2–4% is expected: the program sums logical file sizes after hard-link deduplication, while the OS reports allocated clusters (MFT reserved area, metadata, cluster rounding). A much larger gap usually means **permissions**; check with `--list-skips` and re-run elevated. If stale permission records linger after elevating, add `--reset-skips`.

**Q: Why do both parents and children show up at the top of the growth list?**
A: Directory-level changes inherently overlap — a parent already contains its children. Tick "deepest level only" in the detail table to see only the finest-grained growth.

**Q: Why is the second run so much faster?**
A: Two reasons: metadata is now in the system cache; and if nothing changed, the scan root is **reused wholesale** based on the USN change journal, finishing in seconds. Add `--full` to force a full scan if you suspect the numbers.

**Q: A directory never appears in the report. Why?**
A: Run `--list-skips` to check. If it matches the knowledge base it is retried automatically every run and needs no intervention; otherwise fix the permissions and add `--reset-skips`.

**Q: Why is the first run's percentage marked `~` or shown as `--%`?**
A: A percentage needs a directory count as denominator. Elevated, the program enumerates the NTFS MFT to count directories precisely (~1–3 s); unelevated it cannot, so a whole-volume root falls back to "bytes scanned / volume used" (marked `~`) and a subdirectory root shows only the scanned amount. **From the second run on, the previous snapshot's count is used — exact, and free.**

**Q: Why does a scan take around a minute?**
A: That is the cost of the knowledge-base exemption, and what it buys is visibility into system directories. With the exemption on, the whole `C:\Windows` tree (about 130k directories) is scanned and `-d C` takes about 65 s. Three options: ① accept it (default); ② `--no-knowledge-rescan` to get back to about 20 s; ③ `-x C:\Windows` to drop just that tree while keeping every other exemption.

**Q: Can I monitor only a few directories?**
A: Yes: `-r D:\Build -r D:\Data` is far faster than a full scan.

**Q: If the scan scope changes (say `-d c,d`, then `-d c`, then back), do I lose the baselines?**
A: No. Snapshots rotate per scan root: each root's baseline is **its own latest scan**, so comparison picks up where that root left off even after a few rounds of not being scanned. The report honestly labels this as "compared with this root's own last scan (run #N)"; a newly added root is labeled "first record". The only cost is that roots left out of a run keep their snapshot rows (use `--reset` to release them).

**Q: Can the database live somewhere else?**
A: No — it is fixed at `<exe directory>\disk_growth.db`. If you really need another volume, point the folder at it with a junction (a WAL for millions of directories can fill a nearly-full C:); the report path remains free via `-o`.

---

## Project layout

```
DiskGrowthMonitor/
├── DiskGrowthMonitor.csproj        # single target net45 / x64; ships only the x64 SQLite interop
├── app.config                      # runtime config (supportedRuntime, GC mode)
├── Program.cs                      # entry: arg parsing, elevation, env probe, orchestration, console output
├── Cli/
│   └── Options.cs                  # option definitions, parsing and bilingual help
├── Compat/                         # net45 compatibility shims for missing BCL types and APIs
│   ├── GlobalAliases.cs            #   SyncLock global alias
│   ├── PlatformCompat.cs           #   TickCount64 / Clamp / PtrToStructure<T>
│   └── NetFxPolyfill.cs            #   types the compiler needs that net45 lacks
├── Models/
│   ├── ScanRoot.cs                 # scan root (drive letter or custom directory)
│   ├── Snapshots.cs                # DirSnapshot / ScanRun / ScanStatistics
│   ├── GrowthItem.cs               # comparison result and status enum
│   ├── SkipRecord.cs               # skip record and reason classification
│   ├── ScanSettings.cs             # scanner configuration
│   ├── ReportModel.cs              # report data model
│   ├── SystemProfile.cs            # probe result and derived strategy
│   ├── VolumeGap.cs                # volume-used vs. measured accounting
│   ├── DirKnowledge.cs             # knowledge-base entry (bilingual fields + fallback)
│   ├── CleanableDir.cs             # safe-to-clean aggregate
│   ├── EnumEngine.cs               # enumeration engine tiers
│   ├── LanguageInfo.cs             # language probe result
│   └── OutputLanguage.cs           # output language enum
├── Native/
│   ├── DirectoryEnumerator.cs      # Win32 enumeration: FindFirstFileExW (with \\?\ long-path support)
│   ├── NtDirectoryEnumerator.cs    # NT enumeration: NtQueryDirectoryFile (IDs straight from entries)
│   ├── FileSystemNative.cs         # file IDs, volume hard-link capability, real volume path
│   └── SystemNative.cs             # RtlGetVersion, physical cores, memory, SSD/HDD media
├── Services/
│   ├── DiskScanner.cs              # single-pass recursion, post-order accumulation, per-root parallelism
│   ├── EnumEngineSelector.cs       # per-volume runtime engine selection with caching
│   ├── FileIdSet.cs                # dedup set (volume-serial isolation + sharded locks)
│   ├── SkipListService.cs          # exclusion rules and persistent skip decisions
│   ├── KnowledgeService.cs         # knowledge-base matching (suffix alignment + ancestor index)
│   ├── KnowledgeSeed.cs            # 66 built-in knowledge-base entries (bilingual)
│   ├── CleanupAdvisor.cs           # safe-to-clean aggregation
│   ├── DatabaseService.cs          # schema, migration, stage tables, two-phase snapshot rotation
│   ├── StageWriter.cs              # asynchronous batched persistence with backpressure
│   ├── GrowthAnalyzer.cs           # in-SQL snapshot comparison and leaderboards
│   ├── ReportBuilder.cs            # self-contained bilingual HTML report
│   ├── ConsoleProgressDisplay.cs   # in-place redrawn multi-line progress block
│   ├── VolumeDirEstimator.cs       # MFT directory count (progress denominator)
│   ├── UsnJournalService.cs        # USN change detection and whole-root reuse
│   ├── EnvironmentProbe.cs         # environment probing and strategy derivation
│   └── LanguageProbe.cs            # language decision and console code-page restore
└── Util/
    ├── FormatUtil.cs               # formatting, display-width alignment, HTML escaping
    ├── Lang.cs                     # inline bilingual strings T(zh, en) / F(zh, en, args)
    ├── ElevationHelper.cs          # UAC self-elevation (restart, exit-code relay, graceful decline)
    ├── ConsoleAttach.cs            # elevated child attaches to the parent's console
    └── ConsoleWindow.cs            # console window helpers
```

---

## Technology choices

| Item | Choice | Rationale |
|---|---|---|
| Target framework | `net45` | Windows 7 SP1 / Server 2012 and later all include .NET Framework 4.5+, so target machines need **zero prerequisites** |
| Bitness | `x64` + `Prefer32Bit=false` | A 32-bit process has only 2 GB of user address space, which a million-directory scan dictionary exhausts; fixing x64 also means shipping only `x64\SQLite.Interop.dll` |
| SQLite driver | `System.Data.SQLite.Core` 1.0.118 | SQLite's own ADO.NET provider, shipping both a `net45` assembly and the native library; the native side has zero UCRT / VC runtime dependencies |
| Where comparison runs | Inside SQLite (temp table + index) | Memory use is decoupled from directory count; millions of directories will not exhaust RAM |
| Report | Self-contained HTML (inlined CSS/JS) | No dependencies, opens offline, safe to forward |
| Enumeration API | `NtQueryDirectoryFile` first, `FindFirstFileExW` as fallback | The former returns file IDs with directory entries, removing the dominant per-file handle cost; the latter works on any Windows |

---

## Report contents

This repository ships source only and no sample reports, since report figures are specific to the machine that produced them. Two runs give you a comparison report; its fixed structure is:

| Section | First run | Every run after |
|---|---|---|
| Overview | accounting basis, directory and file counts, total usage | change vs. the previous round (bytes and percent), grown / shrunk counts |
| Largest directories | Top 30 by usage | same |
| Growth / shrink leaderboards | not shown (no baseline) | ranked by change; when nothing crosses the threshold the report falls back to the 30 largest movers and says so on the page |
| Detail table | every directory; filter by status, sort by any column | same |
| Cleanup advice | knowledge-base matches annotated with purpose and safety level | same |
| Skip list | default exclusion / `-x` exclusion / access denied / I/O error / symbolic link | same |
| Volume reconciliation | scanned total vs. volume used; qualitative reasons when the gap is ≥1% | same |

The report is a single self-contained HTML file (inlined CSS/JS, no external dependencies) that opens offline and can be forwarded; its UI language follows the program's language.

---

## Related documents

- [`README_zh.md`](README_zh.md) — this document in Simplified Chinese
- [`UserGuide_EN.md`](UserGuide_EN.md) — end-user manual (setup, running, reading the report, full option list, troubleshooting)
- [`使用说明.md`](使用说明.md) — the same manual in Simplified Chinese

---

## License

MIT License, Copyright (c) **Winrichxue** <winrichxue@126.com>. See [`LICENSE`](LICENSE).
