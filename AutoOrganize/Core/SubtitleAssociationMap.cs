using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.Naming.Common;
using Emby.Naming.TV;
using Emby.Naming.Video;

namespace AutoOrganize.Core;

internal sealed class SubtitleAssociationMap
{
	private readonly Dictionary<string, IReadOnlyList<string>> _subtitlesByVideo;
	private readonly HashSet<string> _associatedSubtitles;

	private SubtitleAssociationMap(Dictionary<string, IReadOnlyList<string>> subtitlesByVideo, HashSet<string> associatedSubtitles)
	{
		_subtitlesByVideo = subtitlesByVideo;
		_associatedSubtitles = associatedSubtitles;
	}

	public IReadOnlyList<string> GetSubtitlePaths(string videoPath)
	{
		return _subtitlesByVideo.TryGetValue(videoPath, out IReadOnlyList<string>? subtitles)
			? subtitles
			: Array.Empty<string>();
	}

	public bool IsAssociatedSubtitle(string subtitlePath)
	{
		return _associatedSubtitles.Contains(subtitlePath);
	}

	public static SubtitleAssociationMap Create(IEnumerable<string> videoPaths, IEnumerable<string> subtitlePaths, NamingOptions namingOptions)
	{
		ArgumentNullException.ThrowIfNull(videoPaths);
		ArgumentNullException.ThrowIfNull(subtitlePaths);
		ArgumentNullException.ThrowIfNull(namingOptions);

		var subtitlesByVideo = new Dictionary<string, List<string>>(PathSafety.PathComparer);
		var associatedSubtitles = new HashSet<string>(PathSafety.PathComparer);
		Dictionary<string, DirectoryIndex> directories = videoPaths
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct(PathSafety.PathComparer)
			.GroupBy(path => Path.GetDirectoryName(path) ?? string.Empty, PathSafety.PathComparer)
			.Where(group => !string.IsNullOrWhiteSpace(group.Key))
			.ToDictionary(group => group.Key, group => new DirectoryIndex(group, namingOptions), PathSafety.PathComparer);

		foreach (string subtitlePath in subtitlePaths
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct(PathSafety.PathComparer)
			.OrderBy(path => path, PathSafety.PathComparer))
		{
			string directory = Path.GetDirectoryName(subtitlePath) ?? string.Empty;
			if (!directories.TryGetValue(directory, out DirectoryIndex? directoryIndex)
				|| !directoryIndex.TryFindVideo(subtitlePath, namingOptions, out string? videoPath))
			{
				continue;
			}

			if (!subtitlesByVideo.TryGetValue(videoPath!, out List<string>? subtitles))
			{
				subtitles = new List<string>();
				subtitlesByVideo.Add(videoPath!, subtitles);
			}
			subtitles.Add(subtitlePath);
			associatedSubtitles.Add(subtitlePath);
		}

		return new SubtitleAssociationMap(
			subtitlesByVideo.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, PathSafety.PathComparer),
			associatedSubtitles);
	}

	private sealed class DirectoryIndex
	{
		private readonly Dictionary<string, List<string>> _videosByBaseName;
		private readonly Dictionary<IdentityName, List<VideoIdentity>> _videosByIdentity;

		public DirectoryIndex(IEnumerable<string> videoPaths, NamingOptions namingOptions)
		{
			_videosByBaseName = videoPaths
				.GroupBy(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty, PathSafety.PathComparer)
				.ToDictionary(group => group.Key, group => group.ToList(), PathSafety.PathComparer);
			_videosByIdentity = videoPaths
				.Select(path => new VideoIdentity(path, ParseIdentity(path, namingOptions)))
				.Where(video => video.Identity.HasValue)
				.GroupBy(video => video.Identity!.Value.Name)
				.ToDictionary(group => group.Key, group => group.ToList());
		}

		public bool TryFindVideo(string subtitlePath, NamingOptions namingOptions, out string? videoPath)
		{
			if (TryFindStrictVideo(subtitlePath, out bool foundStrictName, out videoPath))
			{
				return true;
			}
			if (foundStrictName)
			{
				return false;
			}

			MediaIdentity? subtitleIdentity = ParseIdentity(Path.ChangeExtension(subtitlePath, ".mkv"), namingOptions);
			if (!subtitleIdentity.HasValue || !_videosByIdentity.TryGetValue(subtitleIdentity.Value.Name, out List<VideoIdentity>? candidates))
			{
				videoPath = null;
				return false;
			}

			List<string> matches = candidates
				.Where(candidate => candidate.Identity!.Value.Matches(subtitleIdentity.Value))
				.Select(candidate => candidate.Path)
				.ToList();
			videoPath = matches.Count == 1 ? matches[0] : null;
			return videoPath != null;
		}

		private bool TryFindStrictVideo(string subtitlePath, out bool foundStrictName, out string? videoPath)
		{
			string subtitleName = Path.GetFileNameWithoutExtension(subtitlePath);
			while (!string.IsNullOrWhiteSpace(subtitleName))
			{
				if (_videosByBaseName.TryGetValue(subtitleName, out List<string>? candidates))
				{
					foundStrictName = true;
					videoPath = candidates.Count == 1 ? candidates[0] : null;
					return videoPath != null;
				}

				int separator = subtitleName.LastIndexOf('.');
				if (separator < 0)
				{
					break;
				}
				subtitleName = subtitleName.Substring(0, separator);
			}

			foundStrictName = false;
			videoPath = null;
			return false;
		}
	}

	private static MediaIdentity? ParseIdentity(string path, NamingOptions namingOptions)
	{
		try
		{
			EpisodeInfo episode = new EpisodeResolver(namingOptions).Resolve(path, isDirectory: false) ?? new EpisodeInfo(string.Empty);
			if (!string.IsNullOrWhiteSpace(episode.SeriesName) && episode.SeasonNumber.HasValue && episode.EpisodeNumber.HasValue)
			{
				return MediaIdentity.ForEpisode(episode.SeriesName, episode.SeasonNumber.Value, episode.EpisodeNumber.Value, episode.EndingEpisodeNumber);
			}

			VideoFileInfo? movie = VideoResolver.Resolve(path, isDirectory: false, namingOptions);
			return string.IsNullOrWhiteSpace(movie?.Name) ? null : MediaIdentity.ForMovie(movie.Name, movie.Year);
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
		{
			return null;
		}
	}

	private readonly record struct VideoIdentity(string Path, MediaIdentity? Identity);

	private readonly record struct IdentityName(bool IsEpisode, string Name);

	private readonly record struct MediaIdentity(IdentityName Name, int? Year, int? SeasonNumber, int? EpisodeNumber, int? EndingEpisodeNumber)
	{
		public static MediaIdentity ForEpisode(string seriesName, int seasonNumber, int episodeNumber, int? endingEpisodeNumber)
		{
			return new MediaIdentity(new IdentityName(true, NameUtils.GetComparableName(seriesName).ToUpperInvariant()), null, seasonNumber, episodeNumber, endingEpisodeNumber);
		}

		public static MediaIdentity ForMovie(string movieName, int? year)
		{
			return new MediaIdentity(new IdentityName(false, NameUtils.GetComparableName(movieName).ToUpperInvariant()), year, null, null, null);
		}

		public bool Matches(MediaIdentity other)
		{
			if (Name != other.Name)
			{
				return false;
			}
			if (Name.IsEpisode)
			{
				return SeasonNumber == other.SeasonNumber
					&& EpisodeNumber == other.EpisodeNumber
					&& EndingEpisodeNumber == other.EndingEpisodeNumber;
			}

			return !Year.HasValue || !other.Year.HasValue || Year == other.Year;
		}
	}
}
