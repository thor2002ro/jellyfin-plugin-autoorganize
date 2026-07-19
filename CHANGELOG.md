# Changelog

## 13.1.0.0 — Jellyfin 10.11 / Emby parity

- Added preserve-original-filename switches for episode and movie organization.
- Added forced season-folder organization for flat and conventional series layouts.
- Added normalized provider-search retry for release-style dotted, underscored, and hyphenated titles.
- Fixed manual new-series and new-movie creation when provider IDs are absent.
- Ensured manually selected target library roots take precedence over defaults.
- Prevented duplicate terminal production-year suffixes.
- Added structured logging for selected creation roots and normalized provider-search retries.
- Retained the Jellyfin 10.11 parser, asynchronous controller, safe-transfer, path-authorization, duplicate detection, and SQLite hardening work from 13.0.0.0.
