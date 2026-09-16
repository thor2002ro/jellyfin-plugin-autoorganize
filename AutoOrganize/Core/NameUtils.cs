using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Providers;

namespace AutoOrganize.Core;

public static class NameUtils
{
    internal static int GetMatchScore(string sortedName, int? year, string? itemName, int? itemProductionYear)
    {
        if (string.IsNullOrWhiteSpace(sortedName) || string.IsNullOrWhiteSpace(itemName))
        {
            return 0;
        }

        var score = 0;
        var comparisonName = itemName;
        if (itemProductionYear.HasValue)
        {
            comparisonName = comparisonName.Replace(
                itemProductionYear.Value.ToString(CultureInfo.InvariantCulture),
                string.Empty,
                StringComparison.Ordinal);
        }

        if (IsNameMatch(sortedName, comparisonName))
        {
            score++;
            if (year.HasValue && itemProductionYear.HasValue)
            {
                if (year.Value != itemProductionYear.Value)
                {
                    return 0;
                }

                score++;
            }
        }

        return score;
    }

    internal static Tuple<T, int> GetMatchScore<T>(string sortedName, int? year, T item)
        where T : BaseItem
    {
        ArgumentNullException.ThrowIfNull(item);
        return new Tuple<T, int>(item, GetMatchScore(sortedName, year, item.Name, item.ProductionYear));
    }

    internal static IReadOnlyList<string> GetRemoteSearchCandidates(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var exact = name.Trim();
        var normalized = NormalizeReleaseSeparators(exact);
        if (string.Equals(exact, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return new[] { exact };
        }

        return new[] { exact, normalized };
    }

    internal static string EnsureTerminalYear(string name, int? year)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmedName = name.Trim();
        if (!year.HasValue || HasTerminalYear(trimmedName, year.Value))
        {
            return trimmedName;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{trimmedName} ({year.Value})");
    }

    internal static string RemoveTerminalYear(string name, int? year)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmedName = name.Trim();
        if (!year.HasValue)
        {
            return trimmedName;
        }

        return Regex.Replace(
            trimmedName,
            GetTerminalYearPattern(year.Value),
            string.Empty,
            RegexOptions.CultureInvariant).TrimEnd();
    }

    internal static RemoteSearchResult? SelectBestRemoteResult(
        IEnumerable<RemoteSearchResult> searchResults,
        string requestedName,
        int? requestedYear)
    {
        ArgumentNullException.ThrowIfNull(searchResults);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedName);

        var resultGroups = searchResults
            .Where(result => !string.IsNullOrWhiteSpace(result.Name))
            .GroupBy(result => new { Name = result.Name!.Trim(), result.ProductionYear })
            .ToList();

        if (resultGroups.Count == 1)
        {
            return resultGroups[0].First();
        }

        return resultGroups
            .Select(group => new
            {
                Group = group,
                Score = GetMatchScore(
                    requestedName,
                    requestedYear,
                    group.Key.Name,
                    group.Key.ProductionYear)
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Group.First())
            .FirstOrDefault();
    }

    private static bool IsNameMatch(string name1, string name2)
    {
        name1 = GetComparableName(name1);
        name2 = GetComparableName(name2);
        return string.Equals(name1, name2, StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetComparableName(string name)
    {
        name = RemoveDiacritics(name);
        name = " " + name + " ";
        name = name.Replace('.', ' ').Replace('_', ' ').Replace(" and ", " ", StringComparison.OrdinalIgnoreCase)
            .Replace(".and.", " ", StringComparison.OrdinalIgnoreCase)
            .Replace('&', ' ')
            .Replace('!', ' ')
            .Replace('(', ' ')
            .Replace(')', ' ')
            .Replace(':', ' ')
            .Replace(',', ' ')
            .Replace('-', ' ')
            .Replace('\'', ' ')
            .Replace('[', ' ')
            .Replace(']', ' ')
            .Replace(" a ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" the ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return name.Trim();
    }

    private static string RemoveDiacritics(string name)
    {
        string normalized = name.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (char character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string NormalizeReleaseSeparators(string name)
    {
        var builder = new StringBuilder(name.Length);
        var pendingSpace = false;

        foreach (var character in name)
        {
            if (character is '.' or '_' or '-' || char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString().Trim();
    }

    private static bool HasTerminalYear(string name, int year)
    {
        return Regex.IsMatch(name, GetTerminalYearPattern(year), RegexOptions.CultureInvariant);
    }

    private static string GetTerminalYearPattern(int year)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $@"\(\s*{year}\s*\)\s*$");
    }
}
