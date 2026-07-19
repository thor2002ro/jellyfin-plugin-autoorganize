namespace AutoOrganize.Core;

internal static class EpisodeIdentity
{
	public static bool IsMatch(int? seasonNumber, int? episodeNumber, int? endingEpisodeNumber, int? candidateSeasonNumber, int? candidateEpisodeNumber, int? candidateEndingEpisodeNumber)
	{
		if (seasonNumber.HasValue && episodeNumber.HasValue && candidateSeasonNumber == seasonNumber && candidateEpisodeNumber == episodeNumber)
		{
			return candidateEndingEpisodeNumber == endingEpisodeNumber;
		}
		return false;
	}
}
