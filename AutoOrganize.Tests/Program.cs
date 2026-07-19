using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutoOrganize.Core;
using AutoOrganize.Data;
using AutoOrganize.Model;
using MediaBrowser.Model.Dto;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoOrganize.Tests;

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Body)> Tests = new();

    private static int _passed;
    private static int _skipped;

    public static async Task<int> Main()
    {
        SQLitePCL.Batteries_V2.Init();

        RegisterTests();

        foreach ((string name, Func<Task> body) in Tests)
        {
            try
            {
                await body().ConfigureAwait(false);
                _passed++;
                Console.WriteLine($"PASS  {name}");
            }
            catch (SkipTestException exception)
            {
                _skipped++;
                Console.WriteLine($"SKIP  {name}: {exception.Message}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL  {name}");
                Console.Error.WriteLine(exception);
                Console.Error.WriteLine();
            }
        }

        int failed = Tests.Count - _passed - _skipped;
        Console.WriteLine();
        Console.WriteLine($"Total: {Tests.Count}; Passed: {_passed}; Failed: {failed}; Skipped: {_skipped}");
        return failed == 0 ? 0 : 1;
    }

    private static void RegisterTests()
    {
        Add("remote search keeps exact title first", () =>
        {
            IReadOnlyList<string> candidates = NameUtils.GetRemoteSearchCandidates("The.Last_Of-Us");
            Equal(2, candidates.Count);
            Equal("The.Last_Of-Us", candidates[0]);
            Equal("The Last Of Us", candidates[1]);
        });
        Add("remote search does not add a duplicate normalized title", () =>
        {
            IReadOnlyList<string> candidates = NameUtils.GetRemoteSearchCandidates("The Last Of Us");
            SequenceEqual(new[] { "The Last Of Us" }, candidates);
        });
        Add("remote search collapses repeated release separators", () =>
        {
            IReadOnlyList<string> candidates = NameUtils.GetRemoteSearchCandidates("  The..Last___Of---Us  ");
            Equal("The Last Of Us", candidates[1]);
        });
        Add("name matching ignores common release separators", () =>
        {
            Equal(1, NameUtils.GetMatchScore("The.Last_of-Us", null, "The Last of Us", null));
        });
        Add("name matching treats diacritics consistently", () =>
        {
            Equal(1, NameUtils.GetMatchScore("Pokemon", null, "Pokémon", null));
        });
        Add("name matching rejects conflicting production years", () =>
        {
            Equal(0, NameUtils.GetMatchScore("The Office", 2005, "The Office", 2001));
        });
        Add("terminal year is added once", () =>
        {
            Equal("The Office (2005)", NameUtils.EnsureTerminalYear("The Office", 2005));
        });
        Add("terminal year is not duplicated", () =>
        {
            Equal("The Office (2005)", NameUtils.EnsureTerminalYear("The Office (2005)", 2005));
        });
        Add("terminal year matching tolerates whitespace", () =>
        {
            Equal("The Office ( 2005 )", NameUtils.EnsureTerminalYear("The Office ( 2005 )", 2005));
        });
        Add("terminal year is unchanged when metadata has no year", () =>
        {
            Equal("The Office", NameUtils.EnsureTerminalYear("The Office", null));
        });
        Add("matching terminal year can be removed before token expansion", () =>
        {
            Equal("The Office", NameUtils.RemoveTerminalYear("The Office (2005)", 2005));
        });
        Add("different terminal year is retained", () =>
        {
            Equal("2001: A Space Odyssey (1968)", NameUtils.RemoveTerminalYear("2001: A Space Odyssey (1968)", 2001));
        });

        Add("season folder formatter expands longest tokens first", () =>
        {
            Equal("Season 002 - 02 - 2", EpisodeNameFormatter.FormatSeasonFolder("Season %00s - %0s - %s", 2));
        });
        Add("episode formatter expands single episode tokens", () =>
        {
            string result = EpisodeNameFormatter.FormatEpisodeFile(
                "%sn - S%0sE%0e - %en.%ext",
                "/watch/Release.Name.mkv",
                "Series Name",
                "Episode Four",
                1,
                4,
                null);
            Equal("Series Name - S01E04 - Episode Four.mkv", result);
        });
        Add("episode formatter expands multi-episode ending tokens", () =>
        {
            string result = EpisodeNameFormatter.FormatEpisodeFile(
                "%sn - S%0sE%0e-E%0ed.%ext",
                "/watch/Release.Name.mkv",
                "Series Name",
                "Combined Episode",
                1,
                4,
                5);
            Equal("Series Name - S01E04-E05.mkv", result);
        });
        Add("episode formatter preserves original basename and extension", () =>
        {
            string result = EpisodeNameFormatter.FormatEpisodeFile(
                "%fn.%ext",
                "/watch/The.Show.S02E04.1080p.WEB-DL-GROUP.MKV",
                "The Show",
                string.Empty,
                2,
                4,
                null);
            Equal("The.Show.S02E04.1080p.WEB-DL-GROUP.MKV", result);
        });
        Add("episode formatter preserves multiple dots in original basename", () =>
        {
            string result = EpisodeNameFormatter.FormatEpisodeFile(
                "%fn.%ext",
                "/watch/A.B.C.S01E01.2160p.mkv",
                "A B C",
                string.Empty,
                1,
                1,
                null);
            Equal("A.B.C.S01E01.2160p.mkv", result);
        });
        Add("episode formatter replaces repeated tokens", () =>
        {
            string result = EpisodeNameFormatter.FormatEpisodeFile(
                "%0s-%0s-%0e-%0e.%ext",
                "/watch/source.mkv",
                "Series",
                "Episode",
                3,
                7,
                null);
            Equal("03-03-07-07.mkv", result);
        });

        Add("TV preserve option overrides the single-episode pattern", () =>
        {
            var options = new TvFileOrganizationOptions
            {
                PreserveOriginalFilename = true,
                EpisodeNamePattern = "renamed.%ext"
            };
            Equal("%fn.%ext", OrganizationOptionResolver.GetEpisodePattern(options, false));
        });
        Add("TV preserve option overrides the multi-episode pattern", () =>
        {
            var options = new TvFileOrganizationOptions
            {
                PreserveOriginalFilename = true,
                MultiEpisodeNamePattern = "renamed-multi.%ext"
            };
            Equal("%fn.%ext", OrganizationOptionResolver.GetEpisodePattern(options, true));
        });
        Add("TV stored single pattern remains active when preservation is disabled", () =>
        {
            var options = new TvFileOrganizationOptions
            {
                PreserveOriginalFilename = false,
                EpisodeNamePattern = "custom.%ext"
            };
            Equal("custom.%ext", OrganizationOptionResolver.GetEpisodePattern(options, false));
        });
        Add("TV stored multi pattern remains active when preservation is disabled", () =>
        {
            var options = new TvFileOrganizationOptions
            {
                PreserveOriginalFilename = false,
                MultiEpisodeNamePattern = "custom-multi.%ext"
            };
            Equal("custom-multi.%ext", OrganizationOptionResolver.GetEpisodePattern(options, true));
        });
        Add("movie preserve option overrides the stored pattern", () =>
        {
            var options = new MovieFileOrganizationOptions
            {
                PreserveOriginalFilename = true,
                MoviePattern = "%mn (%my).%ext"
            };
            Equal("%fn.%ext", OrganizationOptionResolver.GetMoviePattern(options));
        });
        Add("movie stored pattern remains active when preservation is disabled", () =>
        {
            var options = new MovieFileOrganizationOptions
            {
                PreserveOriginalFilename = false,
                MoviePattern = "%mn (%my).%ext"
            };
            Equal("%mn (%my).%ext", OrganizationOptionResolver.GetMoviePattern(options));
        });
        Add("flat series layout is retained by default", () =>
        {
            var options = new TvFileOrganizationOptions { AlwaysCreateSeasonFolders = false };
            True(OrganizationOptionResolver.ShouldUseSeriesRoot(options, containsFlatEpisodes: true));
        });
        Add("forced season folders override a flat series layout", () =>
        {
            var options = new TvFileOrganizationOptions { AlwaysCreateSeasonFolders = true };
            False(OrganizationOptionResolver.ShouldUseSeriesRoot(options, containsFlatEpisodes: true));
        });
        Add("series root is not used when no flat episodes exist", () =>
        {
            var options = new TvFileOrganizationOptions { AlwaysCreateSeasonFolders = false };
            False(OrganizationOptionResolver.ShouldUseSeriesRoot(options, containsFlatEpisodes: false));
        });
        Add("manual series creation does not require provider ids", () =>
        {
            var request = new EpisodeFileOrganizationRequest
            {
                NewSeriesName = "The Show",
                TargetFolder = "/library/tv",
                NewSeriesProviderIds = null
            };
            True(OrganizationOptionResolver.ShouldCreateNewSeries(request));
        });
        Add("an existing series id prevents accidental new-series creation", () =>
        {
            var request = new EpisodeFileOrganizationRequest
            {
                SeriesId = Guid.NewGuid().ToString("N"),
                NewSeriesProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" }
            };
            False(OrganizationOptionResolver.ShouldCreateNewSeries(request));
        });
        Add("manual movie creation does not require provider ids", () =>
        {
            var request = new MovieFileOrganizationRequest
            {
                NewMovieName = "Example Movie",
                TargetFolder = "/library/movies",
                NewMovieProviderIds = null
            };
            True(OrganizationOptionResolver.ShouldCreateNewMovie(request));
        });
        Add("an existing movie id prevents accidental new-movie creation", () =>
        {
            var request = new MovieFileOrganizationRequest
            {
                MovieId = Guid.NewGuid().ToString("N"),
                NewMovieProviderIds = new Dictionary<string, string> { ["Tmdb"] = "456" }
            };
            False(OrganizationOptionResolver.ShouldCreateNewMovie(request));
        });
        Add("episode title requirement detects all supported title tokens", () =>
        {
            True(OrganizationOptionResolver.PatternRequiresEpisodeTitle("%en.%ext"));
            True(OrganizationOptionResolver.PatternRequiresEpisodeTitle("%e.n.%ext"));
            True(OrganizationOptionResolver.PatternRequiresEpisodeTitle("%e_n.%ext"));
            False(OrganizationOptionResolver.PatternRequiresEpisodeTitle("%fn.%ext"));
        });
        Add("new TV options preserve legacy rename behavior", () =>
        {
            var options = new TvFileOrganizationOptions();
            False(options.PreserveOriginalFilename);
            False(options.AlwaysCreateSeasonFolders);
            True(options.AutoDetectSeries);
        });
        Add("new movie options retain the legacy preservation pattern", () =>
        {
            var options = new MovieFileOrganizationOptions();
            False(options.PreserveOriginalFilename);
            Equal("%fn.%ext", options.MoviePattern);
            True(options.AutoDetectMovie);
        });

        Add("episode identity includes season number", () =>
        {
            True(EpisodeIdentity.IsMatch(2, 4, null, 2, 4, null));
            False(EpisodeIdentity.IsMatch(2, 4, null, 1, 4, null));
        });
        Add("episode identity includes ending episode number", () =>
        {
            True(EpisodeIdentity.IsMatch(2, 4, 5, 2, 4, 5));
            False(EpisodeIdentity.IsMatch(2, 4, 5, 2, 4, null));
        });
        Add("episode identity rejects missing primary numbers", () =>
        {
            False(EpisodeIdentity.IsMatch(null, 4, null, null, 4, null));
            False(EpisodeIdentity.IsMatch(2, null, null, 2, null, null));
        });

        Add("path containment rejects sibling prefix collisions", () =>
        {
            using var temporary = new TemporaryDirectory();
            string library = Path.Combine(temporary.Path, "tv");
            string sibling = Path.Combine(temporary.Path, "tv-backup", "episode.mkv");
            Directory.CreateDirectory(library);
            False(PathSafety.IsSameOrSubPath(library, sibling));
        });
        Add("path containment accepts descendants", () =>
        {
            using var temporary = new TemporaryDirectory();
            string library = Path.Combine(temporary.Path, "tv");
            string episode = Path.Combine(library, "Show", "Season 01", "episode.mkv");
            Directory.CreateDirectory(library);
            True(PathSafety.IsSameOrSubPath(library, episode));
        });
        Add("watch and library overlap is detected in either direction", () =>
        {
            using var temporary = new TemporaryDirectory();
            string library = Path.Combine(temporary.Path, "library");
            string nested = Path.Combine(library, "incoming");
            True(PathSafety.PathsOverlap(library, nested));
            True(PathSafety.PathsOverlap(nested, library));
        });
        Add("hidden watch folder under library is treated as ignored", () =>
        {
            using var temporary = new TemporaryDirectory();
            string library = Path.Combine(temporary.Path, "library");
            string incoming = Path.Combine(library, ".NEW", "#in");
            True(PathSafety.HasHiddenSegmentUnderRoot(library, incoming));
        });
        Add("ordinary watch folder under library is not treated as ignored", () =>
        {
            using var temporary = new TemporaryDirectory();
            string library = Path.Combine(temporary.Path, "library");
            string incoming = Path.Combine(library, "NEW", "#in");
            False(PathSafety.HasHiddenSegmentUnderRoot(library, incoming));
        });
        Add("TV and movie organizers reject overlapping watch locations", () =>
        {
            using var temporary = new TemporaryDirectory();
            string root = Path.Combine(temporary.Path, "watch");
            var overlap = OrganizerScheduledTask.FindWatchLocationOverlap(
                new[] { root },
                new[] { Path.Combine(root, "movies") });
            True(overlap.HasValue);
            Equal(root, overlap.GetValueOrDefault().Tv);
        });
        Add("TV and movie organizers allow separate watch locations", () =>
        {
            using var temporary = new TemporaryDirectory();
            Equal<(string Tv, string Movie)?>(null, OrganizerScheduledTask.FindWatchLocationOverlap(
                new[] { Path.Combine(temporary.Path, "tv") },
                new[] { Path.Combine(temporary.Path, "movies") }));
        });
        Add("authorized library root must match an exact configured root", () =>
        {
            using var temporary = new TemporaryDirectory();
            string root = Path.Combine(temporary.Path, "library");
            string nested = Path.Combine(root, "nested");
            Directory.CreateDirectory(root);
            Equal(PathSafety.Normalize(root), PathSafety.GetAuthorizedLibraryRoot(root, new[] { root }));
            Throws<OrganizationException>(() => PathSafety.GetAuthorizedLibraryRoot(nested, new[] { root }));
        });
        Add("missing target library falls back to the only configured root", () =>
        {
            using var temporary = new TemporaryDirectory();
            string root = Path.Combine(temporary.Path, "library");
            Directory.CreateDirectory(root);
            Equal(PathSafety.Normalize(root), PathSafety.GetAuthorizedLibraryRoot(null, new[] { root }));
            Throws<OrganizationException>(() => PathSafety.GetAuthorizedLibraryRoot(null, new[] { root, Path.Combine(temporary.Path, "other") }));
        });
        Add("relative paths are rejected", () =>
        {
            False(PathSafety.TryNormalize("relative/path", out _));
            Throws<ArgumentException>(() => PathSafety.Normalize("relative/path"));
        });
        Add("not-yet-created destinations inside a library are authorized", () =>
        {
            using var temporary = new TemporaryDirectory();
            string root = Path.Combine(temporary.Path, "library");
            string target = Path.Combine(root, "New Show", "Season 01", "episode.mkv");
            Directory.CreateDirectory(root);
            True(PathSafety.IsSafelyWithinAnyRoot(target, new[] { root }));
        });
        Add("symbolic-link traversal is rejected", () =>
        {
            using var temporary = new TemporaryDirectory();
            string root = Path.Combine(temporary.Path, "library");
            string outside = Path.Combine(temporary.Path, "outside");
            string link = Path.Combine(root, "link");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(outside);
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                throw new SkipTestException("Symbolic links are unavailable in this environment.");
            }

            True(PathSafety.TraversesSymbolicLink(root, Path.Combine(link, "episode.mkv")));
            False(PathSafety.IsSafelyWithinAnyRoot(Path.Combine(link, "episode.mkv"), new[] { root }));
        });

        AddAsync("safe copy commits complete data and retains source", async () =>
        {
            using var temporary = new TemporaryDirectory();
            string source = Path.Combine(temporary.Path, "source.bin");
            string target = Path.Combine(temporary.Path, "target", "copy.bin");
            byte[] content = CreateContent(524_411);
            await File.WriteAllBytesAsync(source, content).ConfigureAwait(false);
            await SafeFileTransfer.TransferAsync(source, target, copySource: true, overwrite: false, CancellationToken.None).ConfigureAwait(false);
            True(File.Exists(source));
            SequenceEqual(content, await File.ReadAllBytesAsync(target).ConfigureAwait(false));
        });
        AddAsync("safe move commits data before deleting source", async () =>
        {
            using var temporary = new TemporaryDirectory();
            string source = Path.Combine(temporary.Path, "source.bin");
            string target = Path.Combine(temporary.Path, "target", "moved.bin");
            byte[] content = CreateContent(262_157);
            await File.WriteAllBytesAsync(source, content).ConfigureAwait(false);
            await SafeFileTransfer.TransferAsync(source, target, copySource: false, overwrite: false, CancellationToken.None).ConfigureAwait(false);
            False(File.Exists(source));
            SequenceEqual(content, await File.ReadAllBytesAsync(target).ConfigureAwait(false));
        });
        AddAsync("safe move carries subtitle sidecars and preserves language suffixes", async () =>
        {
            using var temporary = new TemporaryDirectory();
            string source = Path.Combine(temporary.Path, "Avatar.2009.mkv");
            string subtitle = Path.Combine(temporary.Path, "Avatar.2009.en.forced.srt");
            string ignored = Path.Combine(temporary.Path, "Avatar.2009.nfo");
            string target = Path.Combine(temporary.Path, "target", "Avatar (2009)", "Avatar (2009).mkv");
            await File.WriteAllBytesAsync(source, CreateContent(1024)).ConfigureAwait(false);
            await File.WriteAllTextAsync(subtitle, "subtitle").ConfigureAwait(false);
            await File.WriteAllTextAsync(ignored, "metadata").ConfigureAwait(false);
            await SafeFileTransfer.TransferAsync(source, target, copySource: false, overwrite: false, CancellationToken.None).ConfigureAwait(false);
            False(File.Exists(source));
            False(File.Exists(subtitle));
            True(File.Exists(ignored));
            Equal("subtitle", await File.ReadAllTextAsync(Path.Combine(temporary.Path, "target", "Avatar (2009)", "Avatar (2009).eng.srt")).ConfigureAwait(false));
            False(File.Exists(Path.Combine(temporary.Path, "target", "Avatar (2009)", "Avatar (2009).en.forced.srt")));
        });
        AddAsync("exact subtitle sidecars do not treat title tokens as language", async () =>
        {
            using var temporary = new TemporaryDirectory();
            string source = Path.Combine(temporary.Path, "Stephen.Kings.It.mkv");
            string subtitle = Path.Combine(temporary.Path, "Stephen.Kings.It.srt");
            string target = Path.Combine(temporary.Path, "target", "Stephen King's It (1990)", "Stephen King's It (1990).mkv");
            await File.WriteAllBytesAsync(source, CreateContent(1024)).ConfigureAwait(false);
            await File.WriteAllTextAsync(subtitle, string.Empty).ConfigureAwait(false);
            await SafeFileTransfer.TransferAsync(source, target, copySource: false, overwrite: false, CancellationToken.None).ConfigureAwait(false);
            True(File.Exists(Path.Combine(temporary.Path, "target", "Stephen King's It (1990)", "Stephen King's It (1990).srt")));
            False(File.Exists(Path.Combine(temporary.Path, "target", "Stephen King's It (1990)", "Stephen King's It (1990).ita.srt")));
        });
        Add("subtitle sidecar matching treats wildcard characters literally", () =>
        {
            True(SafeFileTransfer.IsSidecarFor(Path.Combine("watch", "What If...?.en.srt"), "What If...?"));
            False(SafeFileTransfer.IsSidecarFor(Path.Combine("watch", "What If...X.en.srt"), "What If...?"));
        });
        AddAsync("subtitle sidecar conflict leaves video source in place", async () =>
        {
            using var temporary = new TemporaryDirectory();
            string source = Path.Combine(temporary.Path, "Avatar.2009.mkv");
            string subtitle = Path.Combine(temporary.Path, "Avatar.2009.en.srt");
            string target = Path.Combine(temporary.Path, "target", "Avatar (2009)", "Avatar (2009).mkv");
            string targetSubtitle = Path.Combine(temporary.Path, "target", "Avatar (2009)", "Avatar (2009).eng.srt");
            await File.WriteAllBytesAsync(source, CreateContent(1024)).ConfigureAwait(false);
            await File.WriteAllTextAsync(subtitle, "new subtitle").ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(targetSubtitle) ?? temporary.Path);
            await File.WriteAllTextAsync(targetSubtitle, "existing subtitle").ConfigureAwait(false);
            await ThrowsAsync<IOException>(() => SafeFileTransfer.TransferAsync(source, target, copySource: false, overwrite: false, CancellationToken.None)).ConfigureAwait(false);
            True(File.Exists(source));
            True(File.Exists(subtitle));
            False(File.Exists(target));
            Equal("existing subtitle", await File.ReadAllTextAsync(targetSubtitle).ConfigureAwait(false));
        });
        AddAsync("duplicate subtitle sidecar targets leave video source in place", async () =>
        {
            using var temporary = new TemporaryDirectory();
            string source = Path.Combine(temporary.Path, "Avatar.2009.mkv");
            string subtitle = Path.Combine(temporary.Path, "Avatar.2009.en.srt");
            string forcedSubtitle = Path.Combine(temporary.Path, "Avatar.2009.en.forced.srt");
            string target = Path.Combine(temporary.Path, "target", "Avatar (2009)", "Avatar (2009).mkv");
            await File.WriteAllBytesAsync(source, CreateContent(1024)).ConfigureAwait(false);
            await File.WriteAllTextAsync(subtitle, "plain").ConfigureAwait(false);
            await File.WriteAllTextAsync(forcedSubtitle, "forced").ConfigureAwait(false);
            await ThrowsAsync<IOException>(() => SafeFileTransfer.TransferAsync(source, target, copySource: false, overwrite: false, CancellationToken.None)).ConfigureAwait(false);
            True(File.Exists(source));
            True(File.Exists(subtitle));
            True(File.Exists(forcedSubtitle));
            False(File.Exists(target));
        });
        Add("subtitle target names use the matched media basename", () =>
        {
            string target = SafeFileTransfer.GetSubtitleTargetPath(
                Path.Combine("watch", "Avatar.2009.en.forced.srt"),
                Path.Combine("library", "Avatar (2009)", "Avatar (2009).mkv"));
            Equal(Path.Combine("library", "Avatar (2009)", "Avatar (2009).eng.srt"), target);
            True(SafeFileTransfer.IsSubtitleFile(target));
        });
        Add("subtitle target names ignore invalid short title tokens", () =>
        {
            using var temporary = new TemporaryDirectory();
            string subtitle = Path.Combine(temporary.Path, "The.OA.srt");
            File.WriteAllText(subtitle, string.Empty);
            string target = SafeFileTransfer.GetSubtitleTargetPath(
                subtitle,
                Path.Combine(temporary.Path, "library", "The OA (2016)", "The OA (2016).mkv"));
            Equal(Path.Combine(temporary.Path, "library", "The OA (2016)", "The OA (2016).srt"), target);
        });
        Add("bare srt target names use detected subtitle language", () =>
        {
            using var temporary = new TemporaryDirectory();
            string subtitle = Path.Combine(temporary.Path, "Avatar.2009.srt");
            File.WriteAllText(subtitle, "1\n00:00:01,000 --> 00:00:02,000\nNu este pentru cine stie ce, dar sunt aici.\n");
            string target = SafeFileTransfer.GetSubtitleTargetPath(
                subtitle,
                Path.Combine(temporary.Path, "library", "Avatar (2009)", "Avatar (2009).mkv"));
            Equal(Path.Combine(temporary.Path, "library", "Avatar (2009)", "Avatar (2009).ron.srt"), target);
        });
        Add("explicit subtitle language beats content detection", () =>
        {
            using var temporary = new TemporaryDirectory();
            string subtitle = Path.Combine(temporary.Path, "Avatar.2009.en.srt");
            File.WriteAllText(subtitle, "1\n00:00:01,000 --> 00:00:02,000\nNu este pentru cine stie ce, dar sunt aici.\n");
            string target = SafeFileTransfer.GetSubtitleTargetPath(
                subtitle,
                Path.Combine(temporary.Path, "library", "Avatar (2009)", "Avatar (2009).mkv"));
            Equal(Path.Combine(temporary.Path, "library", "Avatar (2009)", "Avatar (2009).eng.srt"), target);
        });
        Add("explicit three-letter subtitle language is preserved", () =>
        {
            using var temporary = new TemporaryDirectory();
            string subtitle = Path.Combine(temporary.Path, "Avatar.2009.eng.srt");
            File.WriteAllText(subtitle, "1\n00:00:01,000 --> 00:00:02,000\nNu este pentru cine stie ce, dar sunt aici.\n");
            string target = SafeFileTransfer.GetSubtitleTargetPath(
                subtitle,
                Path.Combine(temporary.Path, "library", "Avatar (2009)", "Avatar (2009).mkv"));
            Equal(Path.Combine(temporary.Path, "library", "Avatar (2009)", "Avatar (2009).eng.srt"), target);
        });
        AddAsync("safe overwrite replaces destination atomically", async () =>
        {
            using var temporary = new TemporaryDirectory();
            string source = Path.Combine(temporary.Path, "source.bin");
            string target = Path.Combine(temporary.Path, "target.bin");
            byte[] replacement = CreateContent(131_101);
            await File.WriteAllTextAsync(target, "old").ConfigureAwait(false);
            await File.WriteAllBytesAsync(source, replacement).ConfigureAwait(false);
            await SafeFileTransfer.TransferAsync(source, target, copySource: true, overwrite: true, CancellationToken.None).ConfigureAwait(false);
            SequenceEqual(replacement, await File.ReadAllBytesAsync(target).ConfigureAwait(false));
            True(File.Exists(source));
        });
        AddAsync("failed non-overwrite leaves destination and removes staged temp file", async () =>
        {
            using var temporary = new TemporaryDirectory();
            string source = Path.Combine(temporary.Path, "source.bin");
            string target = Path.Combine(temporary.Path, "target.bin");
            await File.WriteAllTextAsync(source, "new").ConfigureAwait(false);
            await File.WriteAllTextAsync(target, "old").ConfigureAwait(false);
            await ThrowsAsync<IOException>(() => SafeFileTransfer.TransferAsync(source, target, copySource: true, overwrite: false, CancellationToken.None)).ConfigureAwait(false);
            Equal("old", await File.ReadAllTextAsync(target).ConfigureAwait(false));
            True(File.Exists(source));
            Equal(0, Directory.GetFiles(temporary.Path, "*.autoorganize-*.tmp", SearchOption.AllDirectories).Length);
        });
        AddAsync("canceled transfer has no destination side effect", async () =>
        {
            using var temporary = new TemporaryDirectory();
            string source = Path.Combine(temporary.Path, "source.bin");
            string target = Path.Combine(temporary.Path, "target.bin");
            await File.WriteAllBytesAsync(source, CreateContent(1024)).ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => SafeFileTransfer.TransferAsync(source, target, copySource: true, overwrite: false, cancellation.Token)).ConfigureAwait(false);
            True(File.Exists(source));
            False(File.Exists(target));
        });
        AddAsync("same source and target path is rejected", async () =>
        {
            using var temporary = new TemporaryDirectory();
            string source = Path.Combine(temporary.Path, "source.bin");
            await File.WriteAllTextAsync(source, "data").ConfigureAwait(false);
            await ThrowsAsync<OrganizationException>(() => SafeFileTransfer.TransferAsync(source, source, copySource: true, overwrite: true, CancellationToken.None)).ConfigureAwait(false);
            Equal("data", await File.ReadAllTextAsync(source).ConfigureAwait(false));
        });

        Add("file readiness rejects missing paths", () =>
        {
            using var temporary = new TemporaryDirectory();
            False(FileSystemHelpers.IsFileReady(Path.Combine(temporary.Path, "missing.mkv")));
        });
        Add("file readiness accepts an unlocked file", () =>
        {
            using var temporary = new TemporaryDirectory();
            string path = Path.Combine(temporary.Path, "ready.mkv");
            File.WriteAllText(path, "data");
            True(FileSystemHelpers.IsFileReady(path));
        });

        Add("SQLite repository initializes a new database", RepositoryInitializesDatabase);
        Add("SQLite repository round-trips file results", RepositoryRoundTripsFileResults);
        Add("SQLite repository upserts file results", RepositoryUpsertsFileResults);
        Add("SQLite repository applies paging in date order", RepositoryAppliesPaging);
        Add("SQLite repository rejects invalid paging", RepositoryRejectsInvalidPaging);
        AddAsync("SQLite repository deletes completed results only", RepositoryDeletesCompletedResults);
        Add("SQLite repository honors canceled writes", RepositoryHonorsCanceledWrites);
        Add("SQLite smart matches round-trip and upsert", SmartMatchesRoundTripAndUpsert);
        Add("SQLite smart-match saves merge logical duplicates", SmartMatchSavesMergeLogicalDuplicates);
        AddAsync("SQLite smart-match deletion is case-insensitive", SmartMatchDeletionIsCaseInsensitive);
        AddAsync("SQLite smart-match deletion removes empty rows", SmartMatchDeletionRemovesEmptyRows);
        AddAsync("SQLite smart-match batches validate before mutation", SmartMatchBatchValidationIsAtomic);
        AddAsync("SQLite smart-match additions merge concurrent corrections", SmartMatchAdditionsMergeConcurrently);
        Add("SQLite initialization merges duplicate smart matches", SmartMatchInitializationMergesDuplicates);
        Add("SQLite initialization removes malformed rows", RepositoryInitializationRemovesMalformedRows);
        Add("SQLite repository reads legacy duplicate paths and Unix dates", RepositoryReadsLegacyRows);
        Add("SQLite repository recovers and preserves a corrupt database", RepositoryRecoversCorruptDatabase);
        AddAsync("SQLite repository serializes concurrent writes", RepositorySerializesConcurrentWrites);

        Add("assembly version is 13.1.0.0", () =>
        {
            Assembly assembly = typeof(EpisodeNameFormatter).Assembly;
            Equal(new Version(13, 1, 0, 0), assembly.GetName().Version);
        });
        Add("all plugin types load against the Jellyfin 10.11 runtime surface", () =>
        {
            Assembly assembly = typeof(EpisodeNameFormatter).Assembly;
            try
            {
                Type[] types = assembly.GetTypes();
                True(types.Length > 0);
                True(types.Any(type => type.FullName == "AutoOrganize.AutoOrganizePlugin"));
                True(types.Any(type => type.FullName == "AutoOrganize.Api.FileOrganizationController"));
            }
            catch (ReflectionTypeLoadException exception)
            {
                string loaderErrors = string.Join(
                    Environment.NewLine,
                    exception.LoaderExceptions
                        .Where(loaderException => loaderException != null)
                        .Select(loaderException => loaderException!.Message));
                throw new InvalidOperationException("One or more plugin types could not be loaded:" + Environment.NewLine + loaderErrors, exception);
            }
        });
        Add("Lingua runtime files stay in package and install instructions", () =>
        {
            string buildManifest = ReadRepositoryFile("build.yaml");
            Contains("- \"AutoOrganize.dll\"", buildManifest);
            Contains("- \"Lingua.dll\"", buildManifest);
            Contains("- \"Lingua/LanguageModels\"", buildManifest);
            string readme = ReadRepositoryFile("README.md");
            Contains("copy the contents of `AutoOrganize/bin/Release/net9.0/`", readme);
            Contains("`Lingua.dll`", readme);
            Contains("`Lingua/LanguageModels`", readme);
        });
        Add("subtitle language detector unloads Lingua models after use", () =>
        {
            string source = File.ReadAllText(Path.Combine(ReadRepositoryDirectory(), "AutoOrganize", "Core", "SafeFileTransfer.cs"));
            Contains("detector.UnloadLanguageModels();", source);
            False(source.Contains("static readonly Lazy<LanguageDetector>", StringComparison.Ordinal));
            Contains("WithLanguageModelsDirectory(GetBundledLanguageModelsDirectory())", source);
            Contains("typeof(SafeFileTransfer).Assembly.Location", source);
            Contains("AggregateException", source);
        });
        Add("dashboard embeds all expected resources", () =>
        {
            Assembly assembly = typeof(EpisodeNameFormatter).Assembly;
            string[] resources = assembly.GetManifestResourceNames();
            string[] expected =
            {
                "AutoOrganize.Web.autoorganizelog.html",
                "AutoOrganize.Web.autoorganizelog.js",
                "AutoOrganize.Web.autoorganizemovie.html",
                "AutoOrganize.Web.autoorganizemovie.js",
                "AutoOrganize.Web.autoorganizesmart.html",
                "AutoOrganize.Web.autoorganizesmart.js",
                "AutoOrganize.Web.autoorganizetv.html",
                "AutoOrganize.Web.autoorganizetv.js",
                "AutoOrganize.Web.fileorganizer.js",
                "AutoOrganize.Web.fileorganizer.template.html"
            };
            foreach (string resource in expected)
            {
                True(resources.Contains(resource, StringComparer.Ordinal), $"Missing embedded resource {resource}");
            }
        });
        Add("TV dashboard exposes both new switches", () =>
        {
            string html = ReadResource("AutoOrganize.Web.autoorganizetv.html");
            Contains("id=\"chkPreserveEpisodeFilename\"", html);
            Contains("id=\"chkAlwaysCreateSeasonFolders\"", html);
            string script = ReadResource("AutoOrganize.Web.autoorganizetv.js");
            Contains("tvOptions.PreserveOriginalFilename", script);
            Contains("tvOptions.AlwaysCreateSeasonFolders", script);
        });
        Add("movie dashboard exposes filename preservation", () =>
        {
            string html = ReadResource("AutoOrganize.Web.autoorganizemovie.html");
            Contains("id=\"chkPreserveMovieFilename\"", html);
            string script = ReadResource("AutoOrganize.Web.autoorganizemovie.js");
            Contains("movieOptions.PreserveOriginalFilename", script);
        });
        Add("dashboard exposes guarded saves and recoverable list states", () =>
        {
            foreach (string page in new[] { "tv", "movie" })
            {
                string html = ReadResource($"AutoOrganize.Web.autoorganize{page}.html");
                string script = ReadResource($"AutoOrganize.Web.autoorganize{page}.js");
                Contains("class=\"aoSaveLabel\"", html);
                Contains("aoDependentDisabled", html);
                Contains("updateOrganizerState", script);
                Contains("button.setAttribute('aria-busy', 'true')", script);
            }

            Contains("btnRetryLog", ReadResource("AutoOrganize.Web.autoorganizelog.html"));
            string logScript = ReadResource("AutoOrganize.Web.autoorganizelog.js");
            Contains("btnRetryLog", logScript);
            Contains("btnRefreshLog", ReadResource("AutoOrganize.Web.autoorganizelog.html"));
            Contains("btnApproveAll", ReadResource("AutoOrganize.Web.autoorganizelog.html"));
            Contains("btnApproveResult", logScript);
            Contains("btnRejectResult", logScript);
            Contains("material-icons check\" aria-hidden=\"true", logScript);
            False(logScript.Contains("material-icons check\">check", StringComparison.Ordinal));
            False(logScript.Contains("material-icons edit\">edit", StringComparison.Ordinal));
            False(logScript.Contains("material-icons close\">close", StringComparison.Ordinal));
            Contains("function isSubtitleFile", logScript);
            Contains("&& !isSubtitleFile(item)", logScript);
            Contains("function isDeletable", logScript);
            Contains("} else if (isDeletable(item))", logScript);
            Contains("rejectOrganizationResult", logScript);
            Contains("formatFileSize", logScript);
            Contains("updateLogSummary", logScript);
            Contains("item.Type !== 'Log'", logScript);
            Contains("Matched: ", logScript);
            Contains("getMatchedMetadataText", logScript);
            Contains("ApiClient.clearOrganizationLog", logScript);
            Contains("const requestQuery = { ...query }", logScript);
            Contains("generation !== reloadGeneration", logScript);
            False(logScript.Contains("spinnerReloads", StringComparison.Ordinal));
            Contains("btnRetrySmart", ReadResource("AutoOrganize.Web.autoorganizesmart.html"));
            Contains("btnRetrySmart", ReadResource("AutoOrganize.Web.autoorganizesmart.js"));
            foreach (string page in new[] { "tv", "movie" })
            {
                string script = ReadResource($"AutoOrganize.Web.autoorganize{page}.js");
                string html = ReadResource($"AutoOrganize.Web.autoorganize{page}.html");
                Contains("parseWatchLocations", script);
                Contains("watchLocations.join('\\n')", script);
                Contains("mediaLocations.length === 1 ? mediaLocations[0].value : ''", script);
                Contains("One folder per line", html);
                False(script.Contains("existingWatchLocations.slice(1)", StringComparison.Ordinal));
            }
        });
        Add("dashboard scripts do not modify the shared API client prototype", () =>
        {
            Assembly assembly = typeof(EpisodeNameFormatter).Assembly;
            foreach (string resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".js", StringComparison.Ordinal)))
            {
                string script = ReadResource(resource);
                False(script.Contains("ApiClient.prototype", StringComparison.Ordinal), $"{resource} modifies ApiClient.prototype");
            }
        });
        Add("dashboard HTML files contain no duplicate element ids", () =>
        {
            Assembly assembly = typeof(EpisodeNameFormatter).Assembly;
            foreach (string resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".html", StringComparison.Ordinal)))
            {
                string html = ReadResource(resource);
                string[] ids = Regex.Matches(html, "\\bid=[\\\"']([^\\\"']+)[\\\"']", RegexOptions.CultureInvariant)
                    .Select(match => match.Groups[1].Value)
                    .ToArray();
                string[] duplicates = ids.GroupBy(id => id, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToArray();
                Equal(0, duplicates.Length, $"Duplicate ids in {resource}: {string.Join(", ", duplicates)}");
            }
        });
    }

    private static void RepositoryInitializesDatabase()
    {
        using var temporary = new TemporaryDirectory();
        string database = Path.Combine(temporary.Path, "repository.db");
        using var repository = CreateRepository(database);
        repository.Initialize();
        True(File.Exists(database));
        Equal(0, repository.GetResults(new FileOrganizationResultQuery()).TotalRecordCount);
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        Equal(2L, Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    private static void RepositoryRoundTripsFileResults()
    {
        using var temporary = new TemporaryDirectory();
        string database = Path.Combine(temporary.Path, "repository.db");
        using var repository = CreateRepository(database);
        repository.Initialize();

        var expected = NewFileResult("roundtrip", new DateTime(2026, 7, 18, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234));
        expected.TargetPath = Path.Combine(temporary.Path, "library", "target.mkv");
        expected.FileSize = 987654321;
        expected.Status = FileSortingStatus.Success;
        expected.Type = FileOrganizerType.Episode;
        expected.StatusMessage = "organized";
        expected.ExtractedName = "The Show";
        expected.ExtractedYear = 2026;
        expected.ExtractedSeasonNumber = 2;
        expected.ExtractedEpisodeNumber = 4;
        expected.ExtractedEndingEpisodeNumber = 5;
        expected.DuplicatePaths = new[] { "/library/old-a.mkv", "/library/old-b.mkv" };
        repository.SaveResult(expected, CancellationToken.None);

        FileOrganizationResult actual = repository.GetResult(expected.Id)
            ?? throw new InvalidOperationException("The stored file result was not found.");
        Equal(expected.Id, actual.Id);
        Equal(expected.OriginalPath, actual.OriginalPath);
        Equal(Path.GetFileName(expected.OriginalPath), actual.OriginalFileName);
        Equal(expected.TargetPath, actual.TargetPath);
        Equal(expected.FileSize, actual.FileSize);
        Equal(expected.Date, actual.Date);
        Equal(expected.Status, actual.Status);
        Equal(expected.Type, actual.Type);
        Equal(expected.StatusMessage, actual.StatusMessage);
        Equal(expected.ExtractedName, actual.ExtractedName);
        Equal(expected.ExtractedYear, actual.ExtractedYear);
        Equal(expected.ExtractedSeasonNumber, actual.ExtractedSeasonNumber);
        Equal(expected.ExtractedEpisodeNumber, actual.ExtractedEpisodeNumber);
        Equal(expected.ExtractedEndingEpisodeNumber, actual.ExtractedEndingEpisodeNumber);
        SequenceEqual(expected.DuplicatePaths, actual.DuplicatePaths);
    }

    private static void RepositoryUpsertsFileResults()
    {
        using var temporary = new TemporaryDirectory();
        using var repository = CreateRepository(Path.Combine(temporary.Path, "repository.db"));
        repository.Initialize();
        FileOrganizationResult result = NewFileResult("upsert", DateTime.UtcNow);
        repository.SaveResult(result, CancellationToken.None);
        result.Status = FileSortingStatus.Success;
        result.StatusMessage = "updated";
        result.FileSize = 42;
        repository.SaveResult(result, CancellationToken.None);

        var page = repository.GetResults(new FileOrganizationResultQuery());
        Equal(1, page.TotalRecordCount);
        Equal(1, page.Items.Count);
        Equal(FileSortingStatus.Success, page.Items[0].Status);
        Equal("updated", page.Items[0].StatusMessage);
        Equal(42L, page.Items[0].FileSize);
    }

    private static void RepositoryAppliesPaging()
    {
        using var temporary = new TemporaryDirectory();
        using var repository = CreateRepository(Path.Combine(temporary.Path, "repository.db"));
        repository.Initialize();
        FileOrganizationResult oldest = NewFileResult("oldest", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        FileOrganizationResult middle = NewFileResult("middle", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        FileOrganizationResult newest = NewFileResult("newest", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        repository.SaveResult(oldest, CancellationToken.None);
        repository.SaveResult(middle, CancellationToken.None);
        repository.SaveResult(newest, CancellationToken.None);

        var page = repository.GetResults(new FileOrganizationResultQuery { StartIndex = 1, Limit = 1 });
        Equal(3, page.TotalRecordCount);
        Equal(1, page.Items.Count);
        Equal(middle.Id, page.Items[0].Id);
    }

    private static void RepositoryRejectsInvalidPaging()
    {
        using var temporary = new TemporaryDirectory();
        using var repository = CreateRepository(Path.Combine(temporary.Path, "repository.db"));
        repository.Initialize();
        Throws<ArgumentOutOfRangeException>(() => repository.GetResults(new FileOrganizationResultQuery { StartIndex = -1 }));
        Throws<ArgumentOutOfRangeException>(() => repository.GetResults(new FileOrganizationResultQuery { Limit = 0 }));
        Throws<ArgumentOutOfRangeException>(() => repository.GetSmartMatch(new FileOrganizationResultQuery { Limit = -2 }));
    }

    private static async Task RepositoryDeletesCompletedResults()
    {
        using var temporary = new TemporaryDirectory();
        using var repository = CreateRepository(Path.Combine(temporary.Path, "repository.db"));
        repository.Initialize();
        FileOrganizationResult success = NewFileResult("success", DateTime.UtcNow);
        success.Status = FileSortingStatus.Success;
        FileOrganizationResult failure = NewFileResult("failure", DateTime.UtcNow.AddSeconds(-1));
        failure.Status = FileSortingStatus.Failure;
        repository.SaveResult(success, CancellationToken.None);
        repository.SaveResult(failure, CancellationToken.None);
        await repository.DeleteCompleted(CancellationToken.None).ConfigureAwait(false);
        Equal<FileOrganizationResult?>(null, repository.GetResult(success.Id));
        Equal(failure.Id, repository.GetResult(failure.Id)?.Id);
    }

    private static void RepositoryHonorsCanceledWrites()
    {
        using var temporary = new TemporaryDirectory();
        using var repository = CreateRepository(Path.Combine(temporary.Path, "repository.db"));
        repository.Initialize();
        FileOrganizationResult result = NewFileResult("cancel", DateTime.UtcNow);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => repository.SaveResult(result, cancellation.Token));
        Equal<FileOrganizationResult?>(null, repository.GetResult(result.Id));
    }

    private static void SmartMatchesRoundTripAndUpsert()
    {
        using var temporary = new TemporaryDirectory();
        using var repository = CreateRepository(Path.Combine(temporary.Path, "repository.db"));
        repository.Initialize();
        var result = new SmartMatchResult
        {
            ItemName = "The Show",
            DisplayName = "The Show (2026)",
            OrganizerType = FileOrganizerType.Episode
        };
        result.MatchStrings.Add("The.Show");
        repository.SaveResult(result, CancellationToken.None);
        result.DisplayName = "The Show Updated";
        result.MatchStrings.Add("The_Show");
        repository.SaveResult(result, CancellationToken.None);

        var page = repository.GetSmartMatch(new FileOrganizationResultQuery());
        Equal(1, page.TotalRecordCount);
        Equal(result.Id, page.Items[0].Id);
        Equal("The Show Updated", page.Items[0].DisplayName);
        SequenceEqual(new[] { "The.Show", "The_Show" }, page.Items[0].MatchStrings);

        result.MatchStrings.Remove("The.Show");
        repository.SaveResult(result, CancellationToken.None);
        SequenceEqual(
            new[] { "The_Show" },
            repository.GetSmartMatch(new FileOrganizationResultQuery()).Items[0].MatchStrings);
    }

    private static void SmartMatchSavesMergeLogicalDuplicates()
    {
        using var temporary = new TemporaryDirectory();
        using var repository = CreateRepository(Path.Combine(temporary.Path, "repository.db"));
        repository.Initialize();
        var first = new SmartMatchResult
        {
            ItemName = "The Show",
            DisplayName = "The Show",
            OrganizerType = FileOrganizerType.Episode
        };
        first.MatchStrings.Add("One.Show");
        repository.SaveResult(first, CancellationToken.None);
        var second = new SmartMatchResult
        {
            ItemName = "the show",
            DisplayName = "The Show Updated",
            OrganizerType = FileOrganizerType.Episode
        };
        second.MatchStrings.Add("Two.Show");
        repository.SaveResult(second, CancellationToken.None);

        var page = repository.GetSmartMatch(new FileOrganizationResultQuery());
        Equal(1, page.TotalRecordCount);
        Equal(first.Id, second.Id);
        Equal("The Show Updated", page.Items[0].DisplayName);
        SequenceEqual(new[] { "One.Show", "Two.Show" }, page.Items[0].MatchStrings);
    }

    private static async Task SmartMatchDeletionIsCaseInsensitive()
    {
        using var temporary = new TemporaryDirectory();
        using var repository = CreateRepository(Path.Combine(temporary.Path, "repository.db"));
        repository.Initialize();
        var result = new SmartMatchResult { ItemName = "Show", OrganizerType = FileOrganizerType.Episode };
        result.MatchStrings.AddRange(new[] { "The.Show", "Other.Show" });
        repository.SaveResult(result, CancellationToken.None);
        await repository.DeleteSmartMatch(result.Id.ToString("N"), "THE.SHOW", CancellationToken.None).ConfigureAwait(false);
        var page = repository.GetSmartMatch(new FileOrganizationResultQuery());
        Equal(1, page.TotalRecordCount);
        SequenceEqual(new[] { "Other.Show" }, page.Items[0].MatchStrings);
    }

    private static async Task SmartMatchDeletionRemovesEmptyRows()
    {
        using var temporary = new TemporaryDirectory();
        using var repository = CreateRepository(Path.Combine(temporary.Path, "repository.db"));
        repository.Initialize();
        var result = new SmartMatchResult { ItemName = "Show", OrganizerType = FileOrganizerType.Episode };
        result.MatchStrings.Add("The.Show");
        repository.SaveResult(result, CancellationToken.None);
        await repository.DeleteSmartMatch(result.Id.ToString("N"), "The.Show", CancellationToken.None).ConfigureAwait(false);
        Equal(0, repository.GetSmartMatch(new FileOrganizationResultQuery()).TotalRecordCount);
    }

    private static async Task SmartMatchBatchValidationIsAtomic()
    {
        using var temporary = new TemporaryDirectory();
        using var repository = CreateRepository(Path.Combine(temporary.Path, "repository.db"));
        repository.Initialize();
        var result = new SmartMatchResult { ItemName = "Show", OrganizerType = FileOrganizerType.Episode };
        result.MatchStrings.AddRange(new[] { "One.Show", "Two.Show" });
        repository.SaveResult(result, CancellationToken.None);
        await ThrowsAsync<ArgumentException>(() => repository.DeleteSmartMatchEntries(
            new[]
            {
                new NameValuePair { Name = result.Id.ToString("N"), Value = "One.Show" },
                new NameValuePair { Name = "invalid", Value = "Two.Show" }
            },
            CancellationToken.None)).ConfigureAwait(false);
        SequenceEqual(
            new[] { "One.Show", "Two.Show" },
            repository.GetSmartMatch(new FileOrganizationResultQuery()).Items[0].MatchStrings);
    }

    private static async Task SmartMatchAdditionsMergeConcurrently()
    {
        using var temporary = new TemporaryDirectory();
        using var repository = CreateRepository(Path.Combine(temporary.Path, "repository.db"));
        repository.Initialize();
        await Task.WhenAll(
            repository.AddSmartMatchString("Show", "Show", FileOrganizerType.Episode, "One.Show", CancellationToken.None),
            repository.AddSmartMatchString("show", "Show", FileOrganizerType.Episode, "Two.Show", CancellationToken.None)).ConfigureAwait(false);
        var page = repository.GetSmartMatch(new FileOrganizationResultQuery());
        Equal(1, page.TotalRecordCount);
        SequenceEqual(new[] { "One.Show", "Two.Show" }, page.Items[0].MatchStrings.OrderBy(value => value, StringComparer.Ordinal));
    }

    private static void SmartMatchInitializationMergesDuplicates()
    {
        using var temporary = new TemporaryDirectory();
        string database = Path.Combine(temporary.Path, "repository.db");
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE SmartMatch (Id BLOB PRIMARY KEY, ItemName TEXT NOT NULL, DisplayName TEXT, OrganizerType TEXT NOT NULL, MatchStrings TEXT NULL);"
                + "INSERT INTO SmartMatch VALUES ($FirstId, 'Show', 'Show', 'Episode', '[\"One.Show\"]');"
                + "INSERT INTO SmartMatch VALUES ($SecondId, 'show', 'Show', 'Episode', '[\"Two.Show\"]');";
            command.Parameters.AddWithValue("$FirstId", Guid.NewGuid().ToByteArray());
            command.Parameters.AddWithValue("$SecondId", Guid.NewGuid().ToByteArray());
            command.ExecuteNonQuery();
        }
        using var repository = CreateRepository(database);
        repository.Initialize();
        var page = repository.GetSmartMatch(new FileOrganizationResultQuery());
        Equal(1, page.TotalRecordCount);
        SequenceEqual(new[] { "One.Show", "Two.Show" }, page.Items[0].MatchStrings.OrderBy(value => value, StringComparer.Ordinal));
    }

    private static void RepositoryInitializationRemovesMalformedRows()
    {
        using var temporary = new TemporaryDirectory();
        string database = Path.Combine(temporary.Path, "repository.db");
        using (var repository = CreateRepository(database))
        {
            repository.Initialize();
        }
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 1; INSERT INTO FileOrganizerResults (ResultId, OriginalPath, OrganizationDate, Status, OrganizationType) VALUES ($Id, NULL, 'invalid', 'Failure', 'Unknown');";
            command.Parameters.AddWithValue("$Id", Guid.NewGuid().ToByteArray());
            command.ExecuteNonQuery();
        }
        using var recovered = CreateRepository(database);
        recovered.Initialize();
        Equal(0, recovered.GetResults(new FileOrganizationResultQuery()).TotalRecordCount);
    }

    private static void RepositoryReadsLegacyRows()
    {
        using var temporary = new TemporaryDirectory();
        string database = Path.Combine(temporary.Path, "repository.db");
        using var repository = CreateRepository(database);
        repository.Initialize();
        Guid id = Guid.NewGuid();
        const long unixSeconds = 1767225600; // 2026-01-01T00:00:00Z
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT INTO FileOrganizerResults (ResultId, OriginalPath, FileLength, OrganizationDate, Status, OrganizationType, DuplicatePaths) VALUES ($Id, $Path, 10, $Date, 'Failure', 'Episode', $Duplicates);";
            command.Parameters.AddWithValue("$Id", id.ToByteArray());
            command.Parameters.AddWithValue("$Path", "/watch/legacy.mkv");
            command.Parameters.AddWithValue("$Date", unixSeconds);
            command.Parameters.AddWithValue("$Duplicates", "/a.mkv | /b.mkv");
            command.ExecuteNonQuery();
        }

        FileOrganizationResult result = repository.GetResult(id.ToString("N"))
            ?? throw new InvalidOperationException("The legacy result row was not found.");
        Equal(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime, result.Date);
        SequenceEqual(new[] { "/a.mkv", "/b.mkv" }, result.DuplicatePaths);
    }

    private static void RepositoryRecoversCorruptDatabase()
    {
        using var temporary = new TemporaryDirectory();
        string database = Path.Combine(temporary.Path, "repository.db");
        File.WriteAllText(database, "this is not a sqlite database", Encoding.UTF8);
        using var repository = CreateRepository(database);
        repository.Initialize();
        True(File.Exists(database));
        Equal(0, repository.GetResults(new FileOrganizationResultQuery()).TotalRecordCount);
        string[] backups = Directory.GetFiles(temporary.Path, "repository.db.corrupt-*");
        Equal(1, backups.Length);
        True(new FileInfo(backups[0]).Length > 0);
    }

    private static async Task RepositorySerializesConcurrentWrites()
    {
        using var temporary = new TemporaryDirectory();
        using var repository = CreateRepository(Path.Combine(temporary.Path, "repository.db"));
        repository.Initialize();
        Task[] writes = Enumerable.Range(0, 20)
            .Select(index => Task.Run(() => repository.SaveResult(
                NewFileResult($"concurrent-{index}", DateTime.UtcNow.AddSeconds(index)),
                CancellationToken.None)))
            .ToArray();
        await Task.WhenAll(writes).ConfigureAwait(false);
        Equal(20, repository.GetResults(new FileOrganizationResultQuery()).TotalRecordCount);
    }

    private static SqliteFileOrganizationRepository CreateRepository(string databasePath)
    {
        return new SqliteFileOrganizationRepository(NullLogger<SqliteFileOrganizationRepository>.Instance, databasePath);
    }

    private static FileOrganizationResult NewFileResult(string suffix, DateTime date)
    {
        return new FileOrganizationResult
        {
            Id = Guid.NewGuid().ToString("N"),
            OriginalPath = Path.Combine(Path.GetTempPath(), $"{suffix}.mkv"),
            OriginalFileName = $"{suffix}.mkv",
            Date = date,
            Status = FileSortingStatus.Failure,
            Type = FileOrganizerType.Unknown
        };
    }

    private static string ReadResource(string name)
    {
        Assembly assembly = typeof(EpisodeNameFormatter).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Resource {name} was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string ReadRepositoryFile(string name)
    {
        return File.ReadAllText(Path.Combine(ReadRepositoryDirectory(), name));
    }

    private static string ReadRepositoryDirectory()
    {
        string? directory = Directory.GetCurrentDirectory();
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (File.Exists(Path.Combine(directory, "build.yaml")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static byte[] CreateContent(int length)
    {
        var content = new byte[length];
        for (int index = 0; index < content.Length; index++)
        {
            content[index] = (byte)((index * 31 + 17) % 251);
        }

        return content;
    }

    private static void Add(string name, Action body)
    {
        Tests.Add((name, () =>
        {
            body();
            return Task.CompletedTask;
        }));
    }

    private static void AddAsync(string name, Func<Task> body)
    {
        Tests.Add((name, body));
    }

    private static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message ?? "Expected condition to be true.");
        }
    }

    private static void False(bool condition, string? message = null)
    {
        if (condition)
        {
            throw new InvalidOperationException(message ?? "Expected condition to be false.");
        }
    }

    private static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(message ?? $"Expected <{expected}> but found <{actual}>.");
        }
    }

    private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException("Sequences are not equal.");
        }
    }

    private static void Contains(string expectedSubstring, string actual)
    {
        if (!actual.Contains(expectedSubstring, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected text to contain <{expectedSubstring}>.");
        }
    }

    private static TException Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name} to be thrown.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "autoorganize-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Test cleanup is best effort.
            }
        }
    }

    private sealed class SkipTestException : Exception
    {
        public SkipTestException(string message)
            : base(message)
        {
        }
    }
}
