using System;
using System.Globalization;
using System.IO;

namespace AutoOrganize.Core;

internal static class EpisodeNameFormatter
{
	public static string FormatSeasonFolder(string pattern, int seasonNumber)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pattern, "pattern");
		return pattern.Replace("%00s", seasonNumber.ToString("000", CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("%0s", seasonNumber.ToString("00", CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("%s", seasonNumber.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
	}

	public static string FormatEpisodeFile(string pattern, string sourcePath, string seriesName, string episodeTitle, int seasonNumber, int episodeNumber, int? endingEpisodeNumber)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pattern, "pattern");
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath, "sourcePath");
		ArgumentNullException.ThrowIfNull(seriesName, "seriesName");
		ArgumentNullException.ThrowIfNull(episodeTitle, "episodeTitle");
		string newValue = (Path.GetExtension(sourcePath) ?? string.Empty).TrimStart('.');
		string text = pattern.Replace("%sn", seriesName, StringComparison.Ordinal).Replace("%s.n", seriesName.Replace(' ', '.'), StringComparison.Ordinal).Replace("%s_n", seriesName.Replace(' ', '_'), StringComparison.Ordinal)
			.Replace("%00s", seasonNumber.ToString("000", CultureInfo.InvariantCulture), StringComparison.Ordinal)
			.Replace("%0s", seasonNumber.ToString("00", CultureInfo.InvariantCulture), StringComparison.Ordinal)
			.Replace("%s", seasonNumber.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
			.Replace("%ext", newValue, StringComparison.Ordinal)
			.Replace("%en", "%#1", StringComparison.Ordinal)
			.Replace("%e.n", "%#2", StringComparison.Ordinal)
			.Replace("%e_n", "%#3", StringComparison.Ordinal)
			.Replace("%fn", Path.GetFileNameWithoutExtension(sourcePath), StringComparison.Ordinal);
		if (endingEpisodeNumber.HasValue)
		{
			text = text.Replace("%00ed", endingEpisodeNumber.Value.ToString("000", CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("%0ed", endingEpisodeNumber.Value.ToString("00", CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("%ed", endingEpisodeNumber.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
		}
		text = text.Replace("%00e", episodeNumber.ToString("000", CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("%0e", episodeNumber.ToString("00", CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("%e", episodeNumber.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
		return text.Replace("%#1", episodeTitle, StringComparison.Ordinal).Replace("%#2", episodeTitle.Replace(' ', '.'), StringComparison.Ordinal).Replace("%#3", episodeTitle.Replace(' ', '_'), StringComparison.Ordinal);
	}
}
