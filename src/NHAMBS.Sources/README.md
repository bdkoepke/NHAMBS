# NHAMBS.Sources

Shared helpers for reading and downloading CMHC source documents.

- `Source.fs` — HTTP text/binary downloads, temporary files, CSV field
  escaping, and the `NHAMBS_PARALLELISM` setting.
- `BatchReport.fs` — resumable JSON batch reports with atomic checkpoints and
  progress output.
- `Manifest.fs` — read/write support for `pool_urls.txt`-style URL manifests
  and a parallel downloader that skips existing files.

Both `NHAMBS` (the NHA MBS scraper) and `NHAMBS.Cli` use this project, so every
dataset handles URL lists the same way.
