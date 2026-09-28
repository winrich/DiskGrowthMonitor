# DiskGrowthMonitor User Guide

**English** | [简体中文](使用说明.md)

**What it does**: scans your disks and records how much space every directory uses. Scan again later and it tells you **which directories grew the most and by how much**, and produces a report you can open straight in a browser.

> ⚠️ **You need two runs before you get growth data. The first run only establishes a baseline; the second is what shows "what grew".**

---

## 1. Setup (copy the folder and run — nothing to install)

1. Copy the **whole program folder** to the target machine, e.g. `D:\Tools\DiskGrowthMonitor\`.
   **Do not copy the exe alone** — these files must travel with it (one of them lives in a subdirectory):

   ```
   DiskGrowthMonitor.exe                              <- main program
   DiskGrowthMonitor.exe.config                       <- declares the required .NET Framework version
   System.Data.SQLite.dll                             <- SQLite driver (managed)
   System.ValueTuple.dll                              <- tuple support (not in .NET Framework before 4.7)
   x64\SQLite.Interop.dll                             <- native SQLite engine, **must stay in the x64 subfolder**
   ```

   > ⚠️ `SQLite.Interop.dll` **must sit in the `x64` subfolder**, not next to the exe — the driver looks it up
   > at a fixed relative path and will fail with "Unable to load DLL 'SQLite.Interop.dll'" otherwise.

   (`DiskGrowthMonitor.pdb` is a debug symbol file; copying it is optional.)

2. Target machine requirements:

   | Item | Requirement |
   |---|---|
   | Operating system | **Windows Server 2012 or later**, Windows 7 SP1 or later — all ship .NET Framework 4.5+, so **nothing needs installing** |
   | Architecture | **64-bit** (the program is 64-bit only and will not run on 32-bit Windows) |
   | Run privileges | **Running as administrator is strongly recommended**: ① fewer access-denied directories; ② accurate progress on the first scan; ③ incremental reuse, which makes repeat scans take seconds. **It runs fine unelevated**, but every run degrades to a full rescan and takes noticeably longer |
   | Location | The folder must be **writable** (the database and reports are created there). Avoid `C:\Program Files` |
   | Freshness | A freshly deployed folder should contain only the exe and the files above. If you ran it on your own machine, delete `disk_growth.db` and `reports\` before copying |

---

## 2. Running it

The program **must be started from a command line with arguments** — double-clicking the exe just flashes a window and exits, because it does not know what to scan.

### First run: establish the baseline

```
DiskGrowthMonitor.exe -d ALL
```

- `-d ALL` scans every **fixed** drive (C, D, E, …; USB drives and optical drives are excluded)
- A single drive also works: `DiskGrowthMonitor.exe -d C`
- Typical duration: **20 seconds to 2 minutes**, depending on directory count, disk type and whether you are elevated. A live progress bar is shown
- When a scan needs administrator rights the program **requests elevation and attaches to the current window**, so the progress bar and output keep working normally. Declining the UAC prompt is fine — it continues unelevated. Add `--no-elevate` to disable this behaviour entirely

### Second run: get the growth report

Run the same command again after a while (say a week later):

```
DiskGrowthMonitor.exe -d ALL --open
```

`--open` opens the report in your browser when the scan finishes. The report includes the **top 30 fastest-growing directories**, the **most-shrinking directories**, and a searchable, sortable detail table.

> If the disk barely changed and you are running elevated, the program can tell from the change journal that a rescan is unnecessary — it **finishes in seconds** by reusing the previous run's results.

### Recommended: make a launcher

So users do not have to remember command lines, create `run.bat` in the same folder:

```bat
@echo off
cd /d "%~dp0"
DiskGrowthMonitor.exe -d ALL --open
```

Double-clicking the bat is then enough. (To always run elevated, right-click the bat → Properties → Compatibility → "Run this program as an administrator".)

For scheduled runs, point **Windows Task Scheduler** at the bat, e.g. once a week.

### Language

Console messages and the HTML report are bilingual. The default picks the language from the system UI language plus the console code page:

```
DiskGrowthMonitor.exe -d C --lang=zh    force Chinese
DiskGrowthMonitor.exe -d C --lang=en    force English
```

---

## 3. Where the results are

| Output | Location | Notes |
|---|---|---|
| **HTML report** | `reports\disk_growth_report_<timestamp>.html` | A single file — double-click to open, viewable offline, safe to send to others. Use `-o <path>` to put it elsewhere |
| Database | `disk_growth.db` (next to the exe) | Stores the historical snapshot. **Do not delete it** — deleting it resets the tool to "never scanned" and the next run is a first run again |

### Reading the report

1. **Top cards**: total usage, change vs. last run (red = up, green = down), directory count, file count, elapsed time, skipped items
2. **Fastest-growing directories**: sorted by growth, with delta, rate and a magnitude bar — this is where you find what is eating the disk
3. **Most-shrinking directories**: the reverse leaderboard, useful for confirming a cleanup took effect
4. **Detail table**: path keyword search, click-to-sort headers, status filtering; the **"deepest level only"** toggle removes parents already accounted for by their children so you can pinpoint the exact directory
5. **Skip list**: explains which directories were not counted (default exclusion / access denied / symlinks). Entries marked **retried every run** are re-attempted on every scan (see section 6)

**What is the small grey caption under some paths?**

That is the built-in **directory knowledge base**. When it recognises a directory it adds a one-line note beneath the path:

```
C:\Users\henry\AppData\Local\Google\Chrome\User Data\Default\Cache
  ▸ Chrome web cache · safe to delete
```

- Green = **safe to delete**; orange = **delete with care**; red = **do not delete**
- Hover over the caption for the full explanation (what the directory is and what happens if you remove it)
- Unrecognised directories simply show nothing — no effect on anything else
- 66 built-in entries covering system temp / cache / logs / dumps, Chrome / Edge / Firefox, WeChat / DingTalk / Teams, VS Code / NuGet / pip and more

> The skip-list section shows a different green caption, **retried every run** — that means the directory
> matches the knowledge base and is re-attempted on each scan. It is not a statement about deleting anything.

**Why do system directories such as `C:\Windows` appear in the report?**

The program follows one rule: **any directory the knowledge base recognises is re-attempted on every scan** rather than staying permanently skipped because of a stale access-denied record or a default exclusion. The clearest example is the whole `C:\Windows` tree — `WinSxS`, `Temp`, `Logs` and `Installer` inside it are scanned as well. The cost is roughly 45 extra seconds per run (see section 6). If you do not want this, either disable it wholesale with `--no-knowledge-rescan` or drop just that one tree with `-x C:\Windows`.

> Seeing both a parent and its children in a leaderboard is **normal** — the parent's number already includes its children.

---

## 4. Common commands

```bat
DiskGrowthMonitor.exe -d ALL                    scan every fixed drive (the usual choice)
DiskGrowthMonitor.exe -d C                      scan C: only
DiskGrowthMonitor.exe -d C,D                    scan C: and D:
DiskGrowthMonitor.exe -d C -m 500 -t 50         only growth above 500 MB, top 50
DiskGrowthMonitor.exe -d ALL --open             open the report when done
DiskGrowthMonitor.exe -r D:\Build -r D:\Data    monitor specific directories (much faster)
DiskGrowthMonitor.exe --show-env                show the environment and derived strategy (no scan)
DiskGrowthMonitor.exe --list-drives             list local drives
DiskGrowthMonitor.exe --list-skips              show the skip list
DiskGrowthMonitor.exe --list-knowledge          show the built-in directory knowledge base
DiskGrowthMonitor.exe -d C --reset-skips        re-include directories after fixing permissions
DiskGrowthMonitor.exe -d C --no-knowledge-rescan  turn off per-run retries (back to ~20 s scans)
DiskGrowthMonitor.exe -d C -x C:\Windows        drop just the C:\Windows tree
DiskGrowthMonitor.exe -d ALL --full             force a full rescan (when numbers look wrong)
DiskGrowthMonitor.exe --reset-db                wipe all history, back to "never scanned" (asks y/N)
DiskGrowthMonitor.exe -h                        show help
```

`-r` and `-d` can be combined; overlapping paths are merged automatically and never recorded twice.

---

## 5. Option reference

**Scan scope** (exactly one required; both may be used)

| Option | Description |
|---|---|
| `-d, --drive <ALL\|C\|D,...>` | All fixed drives, or specific letters (comma-separated) |
| `-r, --root <directory>` | Scan only the given directory; repeatable |

**Report content**

| Option | Default | Description |
|---|---|---|
| `-t, --top <N>` | 30 | Growth / shrink leaderboard size |
| `--detail-top <N>` | 500 | Maximum rows in the detail table |
| `--detail-min-mb <N>` | 1 | Minimum change shown in the detail table (MB) |
| `-m, --min-mb <N>` | 100 | Only report directories growing by more than N MB |
| `-p, --min-percent <N>` | 0 | Only report directories growing by more than N% |

> If nothing crosses the threshold this run, the report automatically shows the 30 largest movers instead and says so on the page.

**Exclusion and skipping**

| Option | Description |
|---|---|
| `-x, --exclude <path prefix>` | Extra exclusion; repeatable, and takes the highest priority |
| `--no-default-exclude` | Keep the default exclusions (`Windows`, `$Recycle.Bin`, `System Volume Information`) |
| `--reset-skips` | Clear "access denied / I/O error" records and retry those directories |
| `--no-knowledge-rescan` | Turn off per-run retries for knowledge-base directories (**on by default**; turning it off makes scans much faster, but system directories become blind spots again) |
| `--follow-reparse` | Follow directory symlinks and junctions (**off by default** — following them double counts the same data) |
| `--no-dedup` | Disable hard-link deduplication (faster, but `WinSxS` and similar trees inflate; **not recommended on a system drive**) |

**Performance**

| Option | Description |
|---|---|
| `--full` | Force a full scan instead of incremental reuse (use when numbers look wrong) |
| `--threads <N>` | Parallel scan threads; `0` = automatic (one per scan root, capped at physical core count) |
| `--enum-engine <engine>` | Enumeration engine: `auto` (default), `extd`, `both` or `win32`. **All four produce identical results, only speed differs**; `win32` is the compatibility fallback if anything misbehaves |
| `--no-elevate` | Do not attempt to restart with administrator rights |

**Output**

| Option | Description |
|---|---|
| `-o, --out <path>` | HTML report output path |
| `--no-report` | Scan and store only, generate no report |
| `--open` | Open the report in the default browser when done |
| `--lang <auto\|zh\|en>` | Language for program messages and the report; default `auto` |
| `--reset-db` | Wipe all records (requires typing y to confirm; the `dir_knowledge` table is preserved) |
| `--show-env` | Print the environment and derived scan strategy, then exit (**no scan, no database writes**) |
| `--list-drives` | List local drives and exit |
| `--list-skips` | List the skip list and exit |
| `--list-knowledge` | List every knowledge-base entry and exit |
| `--dev` | Developer mode: extra diagnostics; off by default |
| `-h, --help` | Show help |

> The database is fixed at `<exe directory>\disk_growth.db` and **cannot be relocated** (the `--db` option is obsolete and ignored).

---

## 6. Troubleshooting

**Q: Double-clicking the exe just flashes a window.**
A: Expected. The program must be run with arguments; without them it prints "missing scan scope" and exits (exit code 2). Use the commands in section 2, or make a bat file.

**Q: Why is the reported total smaller than the "used space" shown in Explorer?**
A: Separate the two causes. A **structural gap** is normal: the program sums logical file sizes after hard-link deduplication, while Windows reports allocated clusters — that alone accounts for 2–4% (MFT reserved area, metadata, cluster rounding). **Access denied** under-reports on top of that; check with `--list-skips`. Running elevated shrinks the permission gap to a minimum (6 remaining entries and a 2.77% gap on this machine). System directories such as `C:\Windows`, `$Recycle.Bin` and `System Volume Information` **are scanned by default**, because they match the built-in knowledge base and are exempted.

**Q: Why does it scan `C:\Windows` too, and why does a scan take about a minute?**
A: That is the cost of the knowledge-base exemption, and what it buys is visibility. With it on, the whole `C:\Windows` tree (about 130k directories) is scanned and `-d C` takes about 65 seconds; correspondingly C: reports about 275 GB, of which `C:\Windows\Installer` alone accounts for 159.8 GB — system directories like these become a permanent blind spot when they are excluded by default. Three options: ① accept it (default); ② `--no-knowledge-rescan` to get back to about 20 s; ③ `-x C:\Windows` to drop just that tree while keeping everything else.

**Q: A directory never shows up in the report.**
A: First check the skip list with `--list-skips`. If the directory matches the built-in knowledge base it is **retried automatically every run** and needs no action; otherwise fix the permissions and add `--reset-skips` to bring it back into scope.

**Q: The progress percentage has a `~`, or shows `--%`.**
A: On a first scan without elevation the program cannot count directories precisely, so it estimates from "bytes scanned / volume used space" (marked `~`) or shows nothing (`--%`). **From the second run on it uses the previous snapshot's count, so the percentage is always exact.**

**Q: Why is the second run much faster?**
A: Two reasons: metadata is now in the system cache (measured ~2.4× faster); and when running elevated, if nothing changed the program **reuses the entire previous snapshot** based on the change journal, finishing in seconds. Add `--full` to force a full scan if you suspect the numbers.

**Q: If the scan scope changes (say `-d c,d`, then `-d c`, then back), do I lose the baselines?**
A: No. Each scan root keeps **its own latest** snapshot as the baseline, so comparison resumes where that root left off even after a few rounds. The report labels this honestly as "compared with this root's own last scan (run #N)"; a newly added root is labeled "first record".

**Q: Does the database keep growing?**
A: No. Snapshots rotate per scan root and only the latest round per root is kept, so the size is stable (roughly 15–25 MB per 100k directories).

**Q: The disk feels sluggish while it runs.**
A: The program reads sequentially on a single thread, but mechanical drives still feel it. Antivirus real-time scanning also slows things down considerably — excluding the program folder helps.

**Q: Why do some directories have a caption and others not?**
A: The built-in knowledge base only lists directories whose purpose is well known and common; anything unrecognised shows nothing, deliberately, rather than guessing. To add an entry, insert a row into the `dir_knowledge` table of `disk_growth.db` with any SQLite tool, copying the format of an existing row. The program **only inserts missing built-in entries and never overwrites your edits**, and `--reset-db` does not clear that table either. Use `--list-knowledge` to see everything currently in it.

**Q: The caption says "safe to delete" — can I just delete it?**
A: The three levels mean different things:

| Caption | Meaning | Advice |
|---|---|---|
| Green · safe to delete | Pure cache or temporary files; the application recreates them | Safe to remove; at worst the app is slower the next time it starts |
| Orange · delete with care | May contain data you want (configuration, downloads, chat attachments) | Look inside before deciding |
| Red · do not delete | System components, or deleting it means losing chat history or documents | **Leave it alone**; use the application's own cleanup feature |

When in doubt, do not delete. For system drives, Windows' built-in Disk Cleanup or an application's own "clear cache" command is the safer route.

**Q: Can I pin the language?**
A: Yes — `--lang=zh` or `--lang=en`. The default `auto` decides from the system UI language plus the console code page: Chinese only when both are Chinese, English otherwise. `--show-env` shows the decision and the reason.

---

*For implementation details, database schema and project layout see [`README.md`](README.md) (Chinese: [`README_zh.md`](README_zh.md) / [`使用说明.md`](使用说明.md)).*
