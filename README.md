# Auto Organize for Jellyfin 12

Community maintenance fork of the archived Auto Organize plugin. This fork targets Jellyfin **12.0.0**, **.NET 10**, and is not an official Jellyfin release.

## What this fork adds

- Jellyfin 12 plugin/runtime port with Jellyfin-native dependency injection, hosted startup migration, elevated API endpoints, and embedded dashboard pages.
- TV episode and movie organization from configurable watch folders.
- Approval-first workflow: detected items can be approved, rejected, corrected manually, retried, refreshed, approved in bulk, or deleted from the activity log.
- TV season bundle detection for whole season folders, including multi-season bundles for the same show.
- Subtitle organization for sidecars and standalone subtitle files.
- Lingua-backed `.srt` language detection, including standalone subtitles that do not have a video file beside them.
- Safer transfers with staged copy/commit, source deletion only after the target is written, overwrite checks, cancellation cleanup, duplicate subtitle target detection, and same-path protection.
- Path safety checks for watch folders, library roots, symlink traversal, and overlapping TV/movie watch folders.
- SQLite activity-log and smart-match storage with WAL mode, corrupt database backup, malformed row cleanup, duplicate smart-match merge, and bundle-item persistence.
- Modernized Jellyfin dashboard pages for activity, TV settings, movie settings, and smart matches.
- Release metadata and regression runner for the fork.

## Default behavior

New TV and movie organizer options are conservative by default:

- Require approval before organizing: enabled.
- Preserve original incoming filenames: enabled.
- Overwrite existing destination files: disabled.
- Copy original file: disabled, so approved organization moves files.
- Minimum video file size: 50 MB.
- Delete empty source folders after moving: enabled.
- Extended cleanup: disabled.
- Queue a library scan after organizing: disabled.
- Default scheduled triggers: none. Run the task manually or add a Jellyfin schedule.

TV defaults also enable season folders and use `Season %s`. Movie defaults create one folder per movie using `%mn (%my)` and keep the original movie filename.

## TV organization

TV files are parsed with Jellyfin's 12.0 naming library. The organizer can match existing series and episodes, auto-detect series through configured metadata providers, or create pending library items for approval.

Useful TV options:

- **Preserve original episode filename** stores the incoming basename and extension unchanged.
- **Always place episodes in season folders** uses the configured season folder pattern even when an existing series currently stores episodes directly in the series root.
- **Series folder pattern** controls new-series folder names.
- **Default TV library** controls where new detected series are created.

With preserve-original-filename and season folders enabled:

```text
The.Show.S02E04.1080p.WEB-DL-GROUP.mkv
```

can become:

```text
TV/The Show (2026)/Season 2/The.Show.S02E04.1080p.WEB-DL-GROUP.mkv
```

The season directory follows your configured `SeasonFolderPattern`.

## TV season bundles

When approval is enabled, a watch folder containing a whole TV season can be detected as one pending bundle instead of many unrelated rows. Bundle approval stores every source-to-target item and then organizes the approved set together.

Season bundles support:

- Season folders containing video files and matching subtitle sidecars.
- Multi-season folders for the same detected show.
- Metadata refresh before approval.
- Per-item safety checks at approval time.
- Partial result reporting: organized, skipped, and failed counts.

## Movie organization

Movie files are parsed with Jellyfin's naming library. The organizer can match existing movies, auto-detect through configured providers, or create pending movie items for approval.

Useful movie options:

- **Preserve original movie filename** stores the incoming basename and extension unchanged.
- **Create a subdirectory for each movie** places files under a movie folder.
- **Movie folder pattern** controls the folder name, defaulting to `%mn (%my)`.
- **Default movie library** controls where new detected movies are created.

With the default movie folder option:

```text
Avatar.2009.1080p.mkv
```

can become:

```text
Movies/Avatar (2009)/Avatar.2009.1080p.mkv
```

## Subtitles

Subtitle support covers both sidecars and single subtitle files.

Supported subtitle extensions:

```text
.srt .ass .ssa .sub .idx .vtt .smi .sami .sup
```

Behavior:

- A subtitle beside an organized video is moved or copied with that video.
- A subtitle without a matching video in the same watch-folder scan is processed on its own.
- Standalone episode subtitles are parsed as if they were episode video files, then matched to existing Jellyfin episodes.
- Standalone movie subtitles are parsed as if they were movie video files, then matched to existing Jellyfin movies.
- Explicit two-letter or three-letter language suffixes are normalized to ISO-639-3, for example `en` becomes `eng`.
- Bare `.srt` files use bundled Lingua language models to infer a language suffix when possible.
- Non-`.srt` subtitles keep explicit language suffixes when present; otherwise they are named from the matched media item without language detection.

The release package must include `Lingua.dll` and `Lingua/LanguageModels` next to `AutoOrganize.dll`.

## Matching and corrections

This fork keeps Jellyfin's parser as the source of truth and adds fork-specific matching fixes:

- Remote provider searches retry normalized dotted, underscored, and hyphenated release titles.
- Manual new-series and new-movie creation works without provider IDs.
- Manual corrections keep the selected target library root.
- Generated names avoid duplicate terminal years such as `The Office (2005) (2005)`.
- Smart matches remember approved corrections and can be managed from the Smart Matches page.

## Safety notes

- TV and movie watch folders must not overlap each other.
- Watch folders that overlap Jellyfin library roots are skipped.
- Symlink traversal is rejected for organization and cleanup.
- Targets must stay inside configured Jellyfin library roots.
- Existing targets are skipped unless overwrite is enabled.
- Source cleanup only runs inside configured watch folders.

Back up your Jellyfin configuration and media library before first use. Test with approval enabled and a small watch folder before allowing unattended moves.

## Build

Install the .NET 10 SDK, then run:

```bash
dotnet build AutoOrganize.sln --configuration Release
```

The plugin files and installable `AutoOrganize_<version>.zip` archive are generated under `AutoOrganize/bin/Release/net10.0/`.

Run the regression suite with:

```bash
dotnet run --project AutoOrganize.Tests/AutoOrganize.Tests.csproj -c Release
```

## Installation

Add this URL as a plugin repository in the Jellyfin dashboard:

```text
https://raw.githubusercontent.com/thor2002ro/jellyfin-plugin-autoorganize/manifest/manifest.json
```

The `manifest` branch is generated from published GitHub releases. Only releases with a valid `AutoOrganize_<version>.zip` asset are listed, and the branch is maintained as one amended `Local: Update plugin repository manifest` commit.

For a manual install, extract `AutoOrganize_<version>.zip` into an Auto Organize plugin directory under Jellyfin's plugin data directory and restart Jellyfin.

The package artifacts are:

```text
AutoOrganize.dll
Lingua.dll
Lingua/LanguageModels
```

`Lingua.dll` and `Lingua/LanguageModels` are required for subtitle language detection.

GitHub releases use the prebuilt ZIP uploaded to the release. Manifest automation validates the package, reads its version from the archive name, and reads the target ABI from the tagged `build.yaml` without rebuilding the plugin.

## License

This fork is licensed under GPL-3.0-or-later.
