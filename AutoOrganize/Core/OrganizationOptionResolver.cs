using System;
using AutoOrganize.Model;

namespace AutoOrganize.Core;

/// <summary>
/// Resolves behavior switches without mutating the user's stored naming patterns.
/// </summary>
internal static class OrganizationOptionResolver
{
    internal const string OriginalFilenamePattern = "%fn.%ext";

    internal static string GetEpisodePattern(TvFileOrganizationOptions options, bool isMultiEpisode)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.PreserveOriginalFilename
            ? OriginalFilenamePattern
            : (isMultiEpisode ? options.MultiEpisodeNamePattern : options.EpisodeNamePattern);
    }

    internal static string GetMoviePattern(MovieFileOrganizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.PreserveOriginalFilename
            ? OriginalFilenamePattern
            : options.MoviePattern;
    }

    internal static bool ShouldUseSeriesRoot(TvFileOrganizationOptions options, bool containsFlatEpisodes)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !options.AlwaysCreateSeasonFolders && containsFlatEpisodes;
    }

    internal static bool ShouldCreateNewSeries(EpisodeFileOrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.IsNullOrWhiteSpace(request.SeriesId);
    }

    internal static bool ShouldCreateNewMovie(MovieFileOrganizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.IsNullOrWhiteSpace(request.MovieId);
    }

    internal static bool PatternRequiresEpisodeTitle(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        return pattern.Contains("%en", StringComparison.Ordinal)
            || pattern.Contains("%e.n", StringComparison.Ordinal)
            || pattern.Contains("%e_n", StringComparison.Ordinal);
    }
}
