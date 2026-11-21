# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] — 2026-11-21

First public release. Windows x64, single `net45` target, no prerequisites.

### Scanning

- Single-pass recursive scan: every directory is enumerated once and subtree totals are
  accumulated bottom-up, so each file-system entry is visited exactly once.
- NT enumeration engine (`NtQueryDirectoryFile`) returns the file ID together with the
  directory entry, removing the per-file handle that hard-link deduplication used to need.
  `FindFirstFileExW` remains as a fallback engine on older systems.
- `--enum-engine auto|extd|both|win32` — all four produce identical results; only speed differs.
  Default `auto` is chosen from per-volume measurements at startup.
- Hard-link deduplication keyed on `(volume serial, file ID)`, isolated per volume so a
  volume carrying several drive letters is not missed.
- Reparse points are not followed by default (`--follow-reparse` to opt in), because
  junctions such as `C:\Users\All Users` would count the same subtree repeatedly.
- Default exclusions for `Windows`, `$Recycle.Bin` and `System Volume Information`,
  with a knowledge-base exemption that keeps known cache/temp directories in scope.
- `--threads` is capped at the physical core count by default; parallelism is per scan root.

### Incremental reuse

- NTFS USN change journal tracks whether a scan root actually changed. An unchanged root
  reuses its previous snapshot wholesale instead of rescanning.
- `--full` forces a full scan and bypasses reuse.

### Data model

- SQLite snapshot per run, one row per directory, with subtree bytes, file count and
  directory count; the previous snapshot of each root is kept for comparison.
- Two-phase commit: results go into a staging table during the scan and are moved in with a
  single transaction at the end, so a crash or power loss never damages the last baseline.
- Comparison is executed inside SQLite against an indexed temp table; millions of rows are
  never materialised in managed memory.
- `skip_paths` records all five skip reasons; access-denied and I/O-error entries are
  skipped on later runs instead of re-raising, and can be retried with `--reset-skips`.
- `root_last_scan` keeps the most recent scan time per root, so a multi-root database
  comparing at different times still reuses the right baseline.

### Report

- One self-contained HTML file with inlined CSS/JS, no external dependencies, opens offline.
- Overview KPIs, change scale, per-root reconciliation between measured total and volume
  used space, growth/shrink leaderboards, new and removed directories, full detail table
  with search / sort / status filter, grouped skip list.
- Directory knowledge base of 66 built-in entries annotates recognised rows in place with
  purpose and cleanability (safe / caution / keep).
- Bilingual UI (Chinese / English), following the program language or `--lang`.
- Multi-root reports are split into tabs, one per scan root.
- A provenance line at the foot of the report links back to the project repository.

### Console

- Five-stage progress with a per-root progress bar, current path and total line, redrawn in
  place with absolute cursor positioning; silent when output is redirected.
- Colour-coded summary tables with right-aligned numbers; colours are never counted in
  display-width calculation, so alignment survives CJK paths.
- Growth leaderboard shows leaf-level directories by default, so one large change does not
  fill the list with every ancestor; `--console-all-levels` restores the old behaviour.

### Robustness

- Automatic UAC elevation that attaches to the current terminal, so output and progress look
  identical after the restart; declining UAC degrades to an unelevated scan.
- Disk-full handling: staging writes fail with exit code 121 instead of a partially
  committed snapshot, and the crash log stays readable.
- Long-path support (`\\?\`) including directory creation, which `Directory.CreateDirectory`
  cannot handle.

### Command line

Scan scope (`-d`, `-r`) · report sizing (`-t`, `--detail-top`, `-m`, `-p`, `--detail-min-mb`) ·
exclusions and retries (`-x`, `--no-default-exclude`, `--reset-skips`, `--no-knowledge-rescan`,
`--follow-reparse`, `--no-dedup`) · performance (`--enum-engine`, `--threads`, `--full`,
`--no-elevate`) · storage and output (`--data-dir`, `--out`, `--no-report`, `--open`, `--lang`) ·
diagnostics (`--show-env`, `--list-drives`, `--list-knowledge`, `--list-skips`, `--reset-db`,
`--console-all-levels`, `--dev`). `--help` prints the complete list with descriptions.

[1.0.0]: https://github.com/winrich/DiskGrowthMonitor/releases/tag/v1.0.0
