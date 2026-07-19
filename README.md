# Auto Organize for Jellyfin 10.11

> Community maintenance build derived from the archived MIT-licensed Auto Organize plugin. It is not an official Jellyfin release.

This source tree updates the archived Jellyfin Auto Organize plugin for Jellyfin **10.11.11** and **.NET 9**. It retains the Jellyfin-native dependency-injection, naming-parser, path-safety, crash-safe transfer, and SQLite recovery work while incorporating useful behavior from the maintained Emby sibling.

## New organization options

### TV

- **Preserve original episode filename** stores the incoming basename and extension unchanged.
- **Always place episodes in season folders** forces the configured season folder pattern even when an existing series currently has a flat layout.

With both enabled, an incoming file such as:

```text
The.Show.S02E04.1080p.WEB-DL-GROUP.mkv
```

is placed as:

```text
TV/The Show (2026)/Season 02/The.Show.S02E04.1080p.WEB-DL-GROUP.mkv
```

The example uses the season-folder pattern `Season %0s`; other configured
season-folder patterns are respected.

### Movies

- **Preserve original movie filename** stores the incoming basename and extension unchanged.
- Enable **Create subdirectory per Movie** to produce `Movie Name (Year)/original-filename.ext`.

## Other parity and regression fixes

- Exact provider search followed by a normalized dotted/underscored/hyphenated-title retry.
- Manual creation works without provider IDs.
- Explicitly selected library roots are retained for manual corrections.
- Duplicate terminal year suffixes such as `The Office (2005) (2005)` are prevented.
- Input filename parsing remains delegated to Jellyfin's 10.11 naming library.
- Administration scripts remain plugin-local and do not modify the shared API client prototype.

## Build

Install the .NET 9 SDK, then run:

```bash
dotnet build AutoOrganize.sln -c Release
```

The plugin files are generated under `AutoOrganize/bin/Release/net9.0/`.

Run the regression suite with:

```bash
dotnet run --project AutoOrganize.Tests/AutoOrganize.Tests.csproj -c Release
```

See `VALIDATION.md` for the completed release checks and remaining runtime caveat.

## Installation

Create an Auto Organize plugin directory under Jellyfin's plugin data directory, copy the contents of `AutoOrganize/bin/Release/net9.0/` into it, and restart Jellyfin. The Lingua subtitle language detector requires `Lingua.dll` and `Lingua/LanguageModels` alongside `AutoOrganize.dll`. Back up the Jellyfin configuration and media library before first use, then test with copy mode and a small watch folder before enabling moves or overwrites.
