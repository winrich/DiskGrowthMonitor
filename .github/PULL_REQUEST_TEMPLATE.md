## What this changes

<!-- One paragraph. If it fixes an issue, link it: "Fixes #12". -->

## Why

<!-- The problem this solves. For scanner or schema changes, summarise the design first. -->

## How it was verified

<!-- Commands, the tree scanned, before/after numbers. "It builds" is not verification
     for anything touching scanning or SQL. -->

- [ ] Built `Release|x64` and the output contains `x64\SQLite.Interop.dll`
- [ ] Two consecutive runs on a throwaway database (`--data-dir`) produce a real comparison
- [ ] Enumeration changes only: `--enum-engine win32` and `auto` give identical totals on the same tree
- [ ] User-visible changes: `README.md` and `README_zh.md` both updated

## Constraints

- [ ] Single `net45` target kept (no `netstandard2.1` / `net5+` references)
- [ ] No API newer than Windows 8 (NT 6.2)
- [ ] Report still renders as one self-contained HTML file
