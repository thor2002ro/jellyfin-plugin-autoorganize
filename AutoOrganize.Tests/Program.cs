using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutoOrganize.Core;
using AutoOrganize.Data;
using AutoOrganize.Model;
using Emby.Naming.Common;
using Emby.Naming.TV;
using Emby.Naming.Video;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
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
        Add("shared Jellyfin references match target ABI", () =>
        {
            string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            string buildManifest = File.ReadAllText(Path.Combine(repositoryRoot, "build.yaml"));
            Match targetAbiMatch = Regex.Match(buildManifest, "(?m)^targetAbi:\\s*\"(?<version>[^\"]+)\"\\s*$");
            True(targetAbiMatch.Success, "The build manifest does not declare targetAbi.");

            Version targetAbi = Version.Parse(targetAbiMatch.Groups["version"].Value);
            AssemblyName[] sharedReferences = typeof(AutoOrganizePlugin).Assembly.GetReferencedAssemblies()
                .Where(reference => reference.Name is "MediaBrowser.Common" or "MediaBrowser.Controller" or "MediaBrowser.Model")
                .ToArray();

            Equal(3, sharedReferences.Length);
            foreach (AssemblyName reference in sharedReferences)
            {
                Equal(targetAbi, reference.Version);
            }
        });
        Add("release build produces installable ZIP", () =>
        {
            string configuration = Directory.GetParent(AppContext.BaseDirectory)!.Parent!.Name;
            if (!string.Equals(configuration, "Release", StringComparison.Ordinal))
            {
                throw new SkipTestException("Packaging is only produced by Release builds.");
            }

            string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            string releaseDirectory = Path.Combine(repositoryRoot, "AutoOrganize", "bin", configuration, "net10.0");
            string archivePath = Directory.GetFiles(releaseDirectory, "AutoOrganize_*.zip").Single();
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string[] files = archive.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.Name))
                .Select(entry => entry.FullName.Replace('\\', '/'))
                .ToArray();

            True(files.Contains("AutoOrganize.dll", StringComparer.Ordinal), "The package is missing AutoOrganize.dll.");
            True(files.Contains("Lingua.dll", StringComparer.Ordinal), "The package is missing Lingua.dll.");
            True(files.Any(path => path.StartsWith("Lingua/LanguageModels/", StringComparison.Ordinal)), "The package is missing Lingua language models.");
            True(archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).All(entry => entry.Length > 0), "The package contains an empty file.");
        });

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
        Add("new organizers require approval by default", () =>
        {
            True(new TvFileOrganizationOptions().RequireApproval);
            True(new MovieFileOrganizationOptions().RequireApproval);
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
        Add("TV single file parser extracts show season and episode", () =>
        {
            using var temporary = new TemporaryDirectory();
            string path = Path.Combine(temporary.Path, "The.Show.S02E04.1080p.WEB-DL.mkv");
            File.WriteAllText(path, string.Empty);

            EpisodeInfo info = new EpisodeResolver(new NamingOptions()).Resolve(path, isDirectory: false)
                ?? throw new InvalidOperationException("TV episode was not parsed.");
            Equal("The.Show", info.SeriesName);
            Equal(2, info.SeasonNumber);
            Equal(4, info.EpisodeNumber);
        });
        Add("movie single file parser extracts name and year", () =>
        {
            VideoFileInfo info = VideoResolver.Resolve("Avatar.2009.2160p.WEB-DL.mkv", isDirectory: false, new NamingOptions())
                ?? throw new InvalidOperationException("Movie was not parsed.");
            Equal("Avatar", info.Name);
            Equal(2009, info.Year);
        });
        Add("movie directory parser keeps separate movie identities", () =>
        {
            using var temporary = new TemporaryDirectory();
            string first = Path.Combine(temporary.Path, "Avatar.2009.2160p.mkv");
            string second = Path.Combine(temporary.Path, "Aliens.1986.1080p.mkv");
            File.WriteAllText(first, string.Empty);
            File.WriteAllText(second, string.Empty);

            string[] names = new[] { first, second }
                .Select(path => VideoResolver.Resolve(path, isDirectory: false, new NamingOptions())?.Name ?? string.Empty)
                .ToArray();
            SequenceEqual(new[] { "Avatar", "Aliens" }, names);
        });
        Add("movie directory parser ignores non-video sidecars", () =>
        {
            using var temporary = new TemporaryDirectory();
            string movie = Path.Combine(temporary.Path, "Avatar.2009.mkv");
            string subtitle = Path.Combine(temporary.Path, "Avatar.2009.eng.srt");
            string metadata = Path.Combine(temporary.Path, "Avatar.2009.nfo");
            File.WriteAllText(movie, string.Empty);
            File.WriteAllText(subtitle, string.Empty);
            File.WriteAllText(metadata, string.Empty);

            string[] videos = Directory.GetFiles(temporary.Path)
                .Where(SafeFileTransfer.IsLikelyVideoFile)
                .ToArray();
            Equal(1, videos.Length);
            Equal(movie, videos[0]);
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
        Add("TV season directory detection uses first middle and last video", () =>
        {
            using var temporary = new TemporaryDirectory();
            string season = Path.Combine(temporary.Path, "incoming");
            Directory.CreateDirectory(season);
            string first = Path.Combine(season, "The.Show.S02E01.mkv");
            string middle = Path.Combine(season, "The.Show.S02E05.mkv");
            string ignored = Path.Combine(season, "notes.txt");
            string last = Path.Combine(season, "The.Show.S02E10.mkv");
            File.WriteAllText(first, string.Empty);
            File.WriteAllText(ignored, string.Empty);
            File.WriteAllText(middle, string.Empty);
            File.WriteAllText(last, string.Empty);

            EpisodeFileOrganizer.SeasonDirectoryInfo? info = EpisodeFileOrganizer.TryResolveSeasonDirectoryInfo(new[] { ignored, middle, last, first }, new NamingOptions());

            True(info.HasValue);
            EpisodeFileOrganizer.SeasonDirectoryInfo value = info.GetValueOrDefault();
            Equal("The.Show", value.SeriesName);
            Equal(2, value.SeasonNumber);
            Equal(1, value.FirstEpisodeNumber);
            Equal(10, value.LastEpisodeNumber);
        });
        Add("TV season directory detection rejects mixed middle show", () =>
        {
            using var temporary = new TemporaryDirectory();
            string season = Path.Combine(temporary.Path, "incoming");
            Directory.CreateDirectory(season);
            string first = Path.Combine(season, "A.Show.S02E01.mkv");
            string middle = Path.Combine(season, "B.Show.S02E05.mkv");
            string last = Path.Combine(season, "A.Show.S02E10.mkv");
            File.WriteAllText(first, string.Empty);
            File.WriteAllText(middle, string.Empty);
            File.WriteAllText(last, string.Empty);

            Equal<EpisodeFileOrganizer.SeasonDirectoryInfo?>(null, EpisodeFileOrganizer.TryResolveSeasonDirectoryInfo(new[] { first, middle, last }, new NamingOptions()));
        });
        Add("TV season directory detection rejects mixed seasons", () =>
        {
            using var temporary = new TemporaryDirectory();
            string season = Path.Combine(temporary.Path, "incoming");
            Directory.CreateDirectory(season);
            string first = Path.Combine(season, "The.Show.S02E01.mkv");
            string last = Path.Combine(season, "The.Show.S03E01.mkv");
            File.WriteAllText(first, string.Empty);
            File.WriteAllText(last, string.Empty);

            Equal<EpisodeFileOrganizer.SeasonDirectoryInfo?>(null, EpisodeFileOrganizer.TryResolveSeasonDirectoryInfo(new[] { first, last }, new NamingOptions()));
        });
        Add("TV season directory detection rejects mixed movie and episode files", () =>
        {
            using var temporary = new TemporaryDirectory();
            string season = Path.Combine(temporary.Path, "incoming");
            Directory.CreateDirectory(season);
            string episode = Path.Combine(season, "The.Show.S02E01.mkv");
            string movie = Path.Combine(season, "Avatar.2009.mkv");
            string last = Path.Combine(season, "The.Show.S02E10.mkv");
            File.WriteAllText(episode, string.Empty);
            File.WriteAllText(movie, string.Empty);
            File.WriteAllText(last, string.Empty);

            Equal<EpisodeFileOrganizer.SeasonDirectoryInfo?>(null, EpisodeFileOrganizer.TryResolveSeasonDirectoryInfo(new[] { episode, movie, last }, new NamingOptions()));
        });
        Add("TV season directory detection accepts non-mixed season with sidecars", () =>
        {
            using var temporary = new TemporaryDirectory();
            string season = Path.Combine(temporary.Path, "incoming");
            Directory.CreateDirectory(season);
            string first = Path.Combine(season, "The.Show.S03E01.mkv");
            string subtitle = Path.Combine(season, "The.Show.S03E01.eng.srt");
            string last = Path.Combine(season, "The.Show.S03E08.mkv");
            File.WriteAllText(first, string.Empty);
            File.WriteAllText(subtitle, string.Empty);
            File.WriteAllText(last, string.Empty);

            EpisodeFileOrganizer.SeasonDirectoryInfo? info = EpisodeFileOrganizer.TryResolveSeasonDirectoryInfo(new[] { first, subtitle, last }, new NamingOptions());
            True(info.HasValue);
            Equal("The.Show", info.GetValueOrDefault().SeriesName);
            Equal(3, info.GetValueOrDefault().SeasonNumber);
        });
        Add("TV new season directory samples three files and bundles whole directory", () =>
        {
            using var temporary = new TemporaryDirectory();
            string season = Path.Combine(temporary.Path, "incoming");
            Directory.CreateDirectory(season);
            string first = Path.Combine(season, "The.Show.S04E01.mkv");
            string middle = Path.Combine(season, "The.Show.S04E05.mkv");
            string last = Path.Combine(season, "The.Show.S04E10.mkv");
            string subtitle = Path.Combine(season, "The.Show.S04E05.eng.srt");
            File.WriteAllText(first, string.Empty);
            File.WriteAllText(middle, string.Empty);
            File.WriteAllText(last, string.Empty);
            File.WriteAllText(subtitle, string.Empty);

            EpisodeFileOrganizer.SeasonDirectoryInfo? info = EpisodeFileOrganizer.TryResolveSeasonDirectoryInfo(new[] { first, middle, subtitle, last }, new NamingOptions());
            True(info.HasValue);
            Equal("The.Show", info.GetValueOrDefault().SeriesName);
            Equal(4, info.GetValueOrDefault().SeasonNumber);
            Equal(1, info.GetValueOrDefault().FirstEpisodeNumber);
            Equal(10, info.GetValueOrDefault().LastEpisodeNumber);

            string source = File.ReadAllText(Path.Combine(ReadRepositoryDirectory(), "AutoOrganize", "Core", "EpisodeFileOrganizer.cs"));
            Contains("videoPaths[0]", source);
            Contains("videoPaths[videoPaths.Count / 2]", source);
            Contains("videoPaths[videoPaths.Count - 1]", source);
            Contains("GetSeasonBundleItems(files, series", source);
            Contains("GetSeasonBundleTargetPath", source);
        });
        Add("TV season bundle preview reuses confirmed series", () =>
        {
            string source = File.ReadAllText(Path.Combine(ReadRepositoryDirectory(), "AutoOrganize", "Core", "EpisodeFileOrganizer.cs"));
            Contains("GetSeasonBundleItems(files, series, options", source);
            Contains("CreatePendingSeries(seriesName, seriesYear, options)", source);
            Contains("GetMetadataSeriesDirectoryName(series)", source);
            Contains("NameUtils.EnsureTerminalYear(name.Trim(), series.ProductionYear)", source);
            Contains("UseMetadataSeriesPathUnlessExistingSeasonFolders(series, options)", source);
            Contains("HasExistingSeasonFolders(series)", source);
            Contains("_fileSystem.DirectoryExists(series.Path)", source);
            Contains("_fileSystem.DirectoryExists(season.Path)", source);
            Contains("bool hasPath = !string.IsNullOrWhiteSpace(season.Path) && _fileSystem.DirectoryExists(season.Path)", source);
            Contains("!PathSafety.AreSame(season.Path, series.Path)", source);
            Contains("CreatePendingEpisode(series, seasonNumber, episodeNumber", source);
            False(source.Contains("PreviewEpisodeFile", StringComparison.Ordinal));
            False(source.Contains("saveResult: false", StringComparison.Ordinal));
            Contains("if (saveResult && !_organizationService.AddToInProgressList", source);
        });
        Add("detection scans metadata before approval and approval uses stored targets", () =>
        {
            string episodeSource = File.ReadAllText(Path.Combine(ReadRepositoryDirectory(), "AutoOrganize", "Core", "EpisodeFileOrganizer.cs"));
            Contains("AutoDetectSeries(seriesName, seriesYear, options, updateLibrary: false", episodeSource);
            Contains("AutoDetectSeries(seriesName, seriesYear, options, updateLibrary: !requireApproval", episodeSource);
            Contains("SeriesSearchProviders = { \"TVmaze\" }", episodeSource);
            Contains("LibraryProviderResolver.GetMetadataProviders(_libraryManager, options.DefaultSeriesLibraryPath, nameof(Series), SeriesSearchProviders)", episodeSource);
            Contains("LibraryProviderResolver.GetMetadataProviders(_libraryManager, options.DefaultSeriesLibraryPath, nameof(Episode), SeriesSearchProviders)", episodeSource);
            Contains("SearchProviderName = providerName", episodeSource);
            string movieSource = File.ReadAllText(Path.Combine(ReadRepositoryDirectory(), "AutoOrganize", "Core", "MovieFileOrganizer.cs"));
            Contains("AutoDetectMovie(movieName, movieYear, result, options, cancellationToken)", movieSource);
            Contains("MovieSearchProviders = { \"The Open Movie Database\", \"TheMovieDb\" }", movieSource);
            Contains("LibraryProviderResolver.GetMetadataProviders(_libraryManager, options.DefaultMovieLibraryPath, nameof(Movie), MovieSearchProviders)", movieSource);
            Contains("SearchProviderName = providerName", movieSource);
            string providerSource = File.ReadAllText(Path.Combine(ReadRepositoryDirectory(), "AutoOrganize", "Core", "LibraryProviderResolver.cs"));
            Contains("MetadataFetchers", providerSource);
            Contains("MetadataFetcherOrder", providerSource);
            Contains("typeOptions?.MetadataFetchers is { Length: 0 }", providerSource);
            Contains("return Array.Empty<string>();", providerSource);
            False(providerSource.Contains("EnableInternetProviders", StringComparison.Ordinal));
            string serviceSource = File.ReadAllText(Path.Combine(ReadRepositoryDirectory(), "AutoOrganize", "Core", "FileOrganizationService.cs"));
            Contains("OrganizeDetectedFileToStoredTarget", serviceSource);
            Contains("RefreshMetadata(string resultId", serviceSource);
            Contains("RefreshTvSeasonBundleMetadata", serviceSource);
            Contains("Metadata/Refresh", File.ReadAllText(Path.Combine(ReadRepositoryDirectory(), "AutoOrganize", "Api", "FileOrganizationController.cs")));
            Contains("IsSameDetectedResult", File.ReadAllText(Path.Combine(ReadRepositoryDirectory(), "AutoOrganize", "Core", "EpisodeFileOrganizer.cs")));
            Contains("IsSameDetectedResult", File.ReadAllText(Path.Combine(ReadRepositoryDirectory(), "AutoOrganize", "Core", "MovieFileOrganizer.cs")));
        });
        Add("library provider resolver honors enabled fetchers", () =>
        {
            using var temporary = new TemporaryDirectory();
            string libraryRoot = Path.Combine(temporary.Path, "tv");
            var folders = new[]
            {
                new VirtualFolderInfo
                {
                    Locations = new[] { libraryRoot },
                    LibraryOptions = new LibraryOptions
                    {
                        TypeOptions = new[]
                        {
                            new TypeOptions
                            {
                                Type = "Series",
                                MetadataFetchers = new[] { "TVmaze", "TheMovieDb" },
                                MetadataFetcherOrder = new[] { "DisabledProvider", "TheMovieDb", "TVmaze" }
                            },
                            new TypeOptions
                            {
                                Type = "Episode",
                                MetadataFetchers = Array.Empty<string>(),
                                MetadataFetcherOrder = new[] { "TVmaze" }
                            }
                        }
                    }
                }
            };

            SequenceEqual(
                new[] { "TheMovieDb", "TVmaze" },
                LibraryProviderResolver.GetMetadataProviders(folders, libraryRoot, "Series", new[] { "Fallback" }));
            SequenceEqual(
                Array.Empty<string>(),
                LibraryProviderResolver.GetMetadataProviders(folders, libraryRoot, "Episode", new[] { "Fallback" }));
            SequenceEqual(
                new[] { "Fallback" },
                LibraryProviderResolver.GetMetadataProviders(folders, libraryRoot, "Movie", new[] { "Fallback" }));
        });
        Add("TV season bundle approval uses stored file list", () =>
        {
            string source = File.ReadAllText(Path.Combine(ReadRepositoryDirectory(), "AutoOrganize", "Core", "FileOrganizationService.cs"));
            Contains("fileOrganizationResult.BundleItems", source);
            False(source.Contains("_fileSystem.DirectoryExists(fileOrganizationResult.OriginalPath) && fileOrganizationResult.BundleItems.Count > 0", StringComparison.Ordinal));
            False(source.Contains("_fileSystem.DirectoryExists(result.OriginalPath) && result.BundleItems.Count > 0", StringComparison.Ordinal));
            Contains("_fileSystem.DirectoryExists(fileOrganizationResult.OriginalPath)", source);
            Contains("_fileSystem.DirectoryExists(result.OriginalPath)", source);
            Contains("approvedBundleItems", source);
            Contains("AddToInProgressList(fileOrganizationResult, fullClientRefresh: false)", source);
            Contains("RemoveFromInprogressList(fileOrganizationResult)", source);
            Contains("OrganizeTvSeasonDirectory(fileOrganizationResult.OriginalPath, autoOrganizeOptions.TvOptions, approvedBundleItems", source);
            Contains("OrganizeDetectedFileToStoredTarget", source);
            Contains("SafeFileTransfer.TransferAsync(result.OriginalPath, result.TargetPath!", source);
            Contains("ShouldCleanApprovedSource(fileOrganizationResult2, autoOrganizeOptions)", source);
            Contains("CleanApprovedSource(fileOrganizationResult2.OriginalPath, fileOrganizationResult2.Type, autoOrganizeOptions, cancellationToken)", source);
            Contains("ShouldCleanApprovedSource(result, options)", source);
            Contains("CleanApprovedSource(result.OriginalPath, result.Type, options, cancellationToken)", source);
            Contains("fileOrganizationResult.Type = FileOrganizerType.Episode;", source);
            Contains("fileOrganizationResult.Type = FileOrganizerType.Movie;", source);
            Contains("ShouldCleanApprovedSource(fileOrganizationResult, autoOrganizeOptions)", source);
            Contains("EnsureResultSourcePathIsAuthorized(request.ResultId);", source);
            Contains("private void EnsureResultSourcePathIsAuthorized", source);
            Contains("CleanApprovedSource(fileOrganizationResult.OriginalPath, FileOrganizerType.Episode, autoOrganizeOptions, cancellationToken)", source);
            Contains("CleanApprovedSource(fileOrganizationResult.OriginalPath, FileOrganizerType.Movie, autoOrganizeOptions, cancellationToken)", source);
            Contains("private static bool WasMoved", source);
            Contains("private static bool ShouldCleanApprovedSource", source);
            Contains("!PathSafety.AreSame(result.OriginalPath, result.TargetPath)", source);
            Contains("private void CleanApprovedSource", source);
            string folderSource = File.ReadAllText(Path.Combine(ReadRepositoryDirectory(), "AutoOrganize", "Core", "FolderOrganizer.cs"));
            Contains("Approved bundle no longer contains any approved files.", folderSource);
            Contains("OrganizeApprovedBundle(path, approvedBundleItems", folderSource);
            Contains("if (approvedBundleItems != null)", folderSource);
            Contains("RefreshTvSeasonBundleMetadata", folderSource);
            Contains("SafeFileTransfer.TransferSingleAsync(item.SourcePath, item.TargetPath", folderSource);
            Contains("watchLocations.FirstOrDefault(root => PathSafety.IsSameOrSubPath(root, item.SourcePath))", folderSource);
            Contains("PathSafety.TraversesSymbolicLink(sourceRoot, item.SourcePath)", folderSource);
            Contains("var movedSourcePaths = new List<string>();", folderSource);
            Contains("movedSourcePaths.Add(item.SourcePath);", folderSource);
            Contains("CleanApprovedSources(movedSourcePaths, options.WatchLocations, options.DeleteEmptyFolders, options.LeftOverFileExtensionsToDelete, \"TV\", cancellationToken);", folderSource);
            Contains("public void CleanApprovedSources", folderSource);
            Contains("ArgumentNullException.ThrowIfNull(sourcePaths);", folderSource);
            Contains("private static List<string> GetDeleteExtensions", folderSource);
            Contains("MergeSeasonBundles(detectedResults)", folderSource);
            Contains("Detected {seasons.Count} season(s)", folderSource);
            Contains("GetBundleTargetRoot(result) ?? Path.GetDirectoryName(result.OriginalPath)", folderSource);
            Contains("string sourceRoot = first.OriginalPath;", folderSource);
            False(folderSource.Contains("string sourceRoot = Path.GetDirectoryName(first.OriginalPath) ?? first.OriginalPath;", StringComparison.Ordinal));
            Contains("Path.GetDirectoryName(result.TargetPath)", folderSource);
            Contains("GetMergedBundleItemTargetPath(items)", folderSource);
            Contains("private static string? GetMergedBundleItemTargetPath", folderSource);
            Contains("progress.Report(1 + 9.0 * (groupIndex + 1) / directoryGroups.Count)", folderSource);
            Contains("_logger.LogInformation(\"AutoOrganize: {Message}\", message);", folderSource);
            Contains("Jellyfin logs: AutoOrganize", folderSource);
            False(folderSource.Contains("File.AppendAllText", StringComparison.Ordinal));
            False(folderSource.Contains("Directory.CreateDirectory(_logDirectoryPath)", StringComparison.Ordinal));
            string scheduledTaskSource = File.ReadAllText(Path.Combine(ReadRepositoryDirectory(), "AutoOrganize", "Core", "OrganizerScheduledTask.cs"));
            Contains("public bool IsLogged => true;", scheduledTaskSource);
            Contains("return Array.Empty<TaskTriggerInfo>();", scheduledTaskSource);
            False(scheduledTaskSource.Contains("TimeSpan.FromMinutes(5L)", StringComparison.Ordinal));
            Contains("SeasonNumber", File.ReadAllText(Path.Combine(ReadRepositoryDirectory(), "AutoOrganize", "Model", "FileOrganizationResult.cs")));
            Contains("DeleteBundledFileResults(result", folderSource);
            Contains("_organizationService.GetResultBySourcePath(sourcePath)", folderSource);
            Contains("_organizationService.DeleteResult(existing.Id", folderSource);
            int deleteIndex = folderSource.IndexOf("DeleteBundledFileResults(result", StringComparison.Ordinal);
            True(folderSource.LastIndexOf("_organizationService.SaveResult(result, cancellationToken);", deleteIndex, StringComparison.Ordinal) >= 0);
            Contains("Distinct(PathSafety.PathComparer)", folderSource);
            Contains("addedSourcePaths", File.ReadAllText(Path.Combine(ReadRepositoryDirectory(), "AutoOrganize", "Core", "EpisodeFileOrganizer.cs")));
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
        Add("new TV options preserve original names and use season folders by default", () =>
        {
            var options = new TvFileOrganizationOptions();
            True(options.PreserveOriginalFilename);
            True(options.AlwaysCreateSeasonFolders);
            True(options.DeleteEmptyFolders);
            SequenceEqual(new[] { "nfo" }, options.LeftOverFileExtensionsToDelete);
            True(options.AutoDetectSeries);
        });
        Add("new movie options delete empty folders by default", () =>
        {
            var options = new MovieFileOrganizationOptions();
            True(options.PreserveOriginalFilename);
            Equal("%fn.%ext", options.MoviePattern);
            True(options.DeleteEmptyFolders);
            SequenceEqual(new[] { "nfo" }, options.LeftOverFileExtensionsToDelete);
            True(options.MovieFolder);
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
        Add("video prefilter accepts common videos and rejects junk", () =>
        {
            True(SafeFileTransfer.IsLikelyVideoFile("episode.mkv"));
            True(SafeFileTransfer.IsLikelyVideoFile("episode.m2ts"));
            True(SafeFileTransfer.IsLikelyVideoFile("episode.mk3d"));
            True(SafeFileTransfer.IsLikelyVideoFile("episode.rec"));
            True(SafeFileTransfer.IsLikelyVideoFile("episode.strm"));
            False(SafeFileTransfer.IsLikelyVideoFile("episode.srt"));
            False(SafeFileTransfer.IsLikelyVideoFile("episode.nfo"));
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
        Add("SQLite repository repairs missing bundle column", RepositoryRepairsMissingBundleColumn);
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

        Add("all plugin types load against the Jellyfin 12 runtime surface", () =>
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
            Contains("extract `AutoOrganize_<version>.zip`", readme);
            Contains("`Lingua.dll`", readme);
            Contains("`Lingua/LanguageModels`", readme);
        });
        Add("subtitle language detector unloads Lingua models after use", () =>
        {
            string source = File.ReadAllText(Path.Combine(ReadRepositoryDirectory(), "AutoOrganize", "Core", "SafeFileTransfer.cs"));
            Contains("detector.UnloadLanguageModels();", source);
            False(source.Contains("static readonly Lazy<LanguageDetector>", StringComparison.Ordinal));
            False(source.Contains("private static readonly HashSet<string> VideoExtensions", StringComparison.Ordinal));
            Contains("namingOptions.VideoFileExtensions", source);
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

            string logHtml = ReadResource("AutoOrganize.Web.autoorganizelog.html");
            Contains("btnRetryLog", logHtml);
            string logScript = ReadResource("AutoOrganize.Web.autoorganizelog.js");
            Contains("btnRetryLog", logScript);
            Contains("btnRefreshLog", logHtml);
            Contains("aoOrganizeLabel", logHtml);
            Contains("aoCancelTask", logHtml);
            Contains("btnApproveAll", logHtml);
            Contains("btnApproveResult", logScript);
            Contains("btnRejectResult", logScript);
            Contains("material-icons check\" aria-hidden=\"true", logScript);
            False(logScript.Contains("material-icons check\">check", StringComparison.Ordinal));
            False(logScript.Contains("material-icons edit\">edit", StringComparison.Ordinal));
            False(logScript.Contains("material-icons close\">close", StringComparison.Ordinal));
            False(logScript.Contains("material-icons arrow_back\">arrow_back", StringComparison.Ordinal));
            False(logScript.Contains("material-icons arrow_forward\">arrow_forward", StringComparison.Ordinal));
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
            Contains("refreshOrganizationMetadata", logScript);
            Contains("btnRefreshMetadata", logScript);
            Contains("manage_search", logScript);
            Contains("function isMetadataRefreshable", logScript);
            Contains("function renderBundleList", logScript);
            Contains("BundleItems", logScript);
            Contains("(item.TargetPath || isBundle(item))", logScript);
            Contains("!item.TargetPath && !isBundle(item)", logScript);
            Contains("SeasonNumber", logScript);
            Contains("aoBundleSeason", logScript);
            Contains("SourcePath", logScript);
            Contains("TargetPath", logScript);
            Contains("ScheduledTasks/Running/", logScript);
            Contains("ScheduledTaskStarted", logScript);
            Contains("Cancel", logScript);
            Contains("button.disabled = false;", logScript);
            Contains("function setServerEvents(enabled)", logScript);
            Contains("AutoOrganize/Status", logScript);
            Contains("status?.PluginVersion", logScript);
            Contains("class=\"aoMuted aoPluginVersion\"", logHtml);
            Contains("Events.on(ServerNotifications, event, onServerEvent);", logScript);
            Contains("Events.off(ServerNotifications, event, onServerEvent);", logScript);
            False(logScript.Contains("const method = enabled ? Events.on : Events.off;", StringComparison.Ordinal));
            True(
                Regex.IsMatch(logScript, @"setServerEvents\(false\);\s*setServerEvents\(true\);"),
                "The dashboard does not reset its server event subscriptions.");
            Contains("scheduleOrganizeTaskRefresh", logScript);
            Contains("organizeTaskRefreshRetries", logScript);
            Contains("running ? 1500 : 1000", logScript);
            Contains("running ? 20 : organizeTaskRefreshRetries - 1", logScript);
            Contains("setOrganizeTaskRunning(page, false);", logScript);
            Contains("const wasRunning = organizeTaskRunning;", logScript);
            Contains("if (wasRunning && !running)", logScript);
            Contains("reloadItems(page);", logScript);
            Contains("organizeTaskId = null;", logScript);
            Contains("scheduleOrganizeTaskRefresh(view, 1000, 3);", logScript);
            Contains("scheduleOrganizeTaskRefresh(page, 1000, 2);", logScript);
            Contains("scheduleOrganizeTaskRefresh(pageGlobal);", logScript);
            Contains("let organizeTaskEntryRefreshTimer = null;", logScript);
            Contains("clearTimeout(organizeTaskEntryRefreshTimer);", logScript);
            False(logScript.Contains("setTimeout(function ()", StringComparison.Ordinal));
            Contains("setTimeout(async function ()", logScript);
            Contains("if (pageGlobal !== view)", logScript);
            Contains("if (!wasRunning)", logScript);
            Contains("getScheduledTaskKey", logScript);
            Contains("isTaskRunning", logScript);
            Contains("3000", logScript);
            Contains("reloadItems(view);", logScript);
            Contains("setOrganizeTaskRunning", logScript);
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
        Equal(3L, Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture));
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
        expected.BundleItems = new[]
        {
            new FileOrganizationBundleItem
            {
                SourcePath = Path.Combine(temporary.Path, "watch", "The.Show.S02E04.mkv"),
                TargetPath = Path.Combine(temporary.Path, "library", "The Show", "Season 2", "The Show - S02E04.mkv"),
                SeasonNumber = 2
            },
            new FileOrganizationBundleItem
            {
                SourcePath = Path.Combine(temporary.Path, "watch", "The.Show.S02E04.eng.srt"),
                TargetPath = Path.Combine(temporary.Path, "library", "The Show", "Season 2", "The Show - S02E04.eng.srt"),
                SeasonNumber = 2
            }
        };
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
        Equal(2, actual.BundleItems.Count);
        Equal(expected.BundleItems[0].SourcePath, actual.BundleItems[0].SourcePath);
        Equal(expected.BundleItems[0].TargetPath, actual.BundleItems[0].TargetPath);
        Equal(expected.BundleItems[0].SeasonNumber, actual.BundleItems[0].SeasonNumber);
        Equal(expected.BundleItems[1].SourcePath, actual.BundleItems[1].SourcePath);
        Equal(expected.BundleItems[1].TargetPath, actual.BundleItems[1].TargetPath);
        Equal(expected.BundleItems[1].SeasonNumber, actual.BundleItems[1].SeasonNumber);
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

    private static void RepositoryRepairsMissingBundleColumn()
    {
        using var temporary = new TemporaryDirectory();
        string database = Path.Combine(temporary.Path, "repository.db");
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE FileOrganizerResults (ResultId BLOB PRIMARY KEY, OriginalPath TEXT, TargetPath TEXT, FileLength INTEGER NOT NULL DEFAULT 0, OrganizationDate TEXT NOT NULL, Status TEXT NOT NULL, OrganizationType TEXT NOT NULL, StatusMessage TEXT, ExtractedName TEXT, ExtractedYear INTEGER NULL, ExtractedSeasonNumber INTEGER NULL, ExtractedEpisodeNumber INTEGER NULL, ExtractedEndingEpisodeNumber INTEGER NULL, DuplicatePaths TEXT NULL); PRAGMA user_version = 3;";
            command.ExecuteNonQuery();
        }

        using var repository = CreateRepository(database);
        repository.Initialize();
        repository.SaveResult(NewFileResult("repaired", DateTime.UtcNow), CancellationToken.None);
        Equal(1, repository.GetResults(new FileOrganizationResultQuery()).TotalRecordCount);
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
