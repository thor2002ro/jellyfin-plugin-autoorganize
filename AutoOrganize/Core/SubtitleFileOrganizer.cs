using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoOrganize.Model;
using Emby.Naming.Common;
using Emby.Naming.TV;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace AutoOrganize.Core;

internal sealed class SubtitleFileOrganizer
{
	private readonly IFileSystem _fileSystem;
	private readonly IFileOrganizationService _organizationService;
	private readonly ILogger<SubtitleFileOrganizer> _logger;
	private readonly ILibraryManager _libraryManager;
	private readonly ILibraryMonitor _libraryMonitor;
	private readonly NamingOptions _namingOptions;

	public SubtitleFileOrganizer(IFileOrganizationService organizationService, IFileSystem fileSystem, ILogger<SubtitleFileOrganizer> logger, ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, NamingOptions namingOptions)
	{
		_organizationService = organizationService;
		_fileSystem = fileSystem;
		_logger = logger;
		_libraryManager = libraryManager;
		_libraryMonitor = libraryMonitor;
		_namingOptions = namingOptions;
	}

	public Task<FileOrganizationResult> OrganizeEpisodeSubtitleFile(string path, TvFileOrganizationOptions options, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(options);
		return OrganizeSubtitleFile(path, FileOrganizerType.Episode, options.CopyOriginalFile, options.OverwriteExistingEpisodes, options.RequireApproval, FindEpisodePath, cancellationToken);
	}

	public Task<FileOrganizationResult> ApproveEpisodeSubtitleFile(string path, TvFileOrganizationOptions options, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(options);
		return OrganizeSubtitleFile(path, FileOrganizerType.Episode, options.CopyOriginalFile, options.OverwriteExistingEpisodes, requireApproval: false, FindEpisodePath, cancellationToken);
	}

	public Task<FileOrganizationResult> OrganizeMovieSubtitleFile(string path, MovieFileOrganizationOptions options, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(options);
		return OrganizeSubtitleFile(path, FileOrganizerType.Movie, options.CopyOriginalFile, options.OverwriteExistingFiles, options.RequireApproval, FindMoviePath, cancellationToken);
	}

	public Task<FileOrganizationResult> ApproveMovieSubtitleFile(string path, MovieFileOrganizationOptions options, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(options);
		return OrganizeSubtitleFile(path, FileOrganizerType.Movie, options.CopyOriginalFile, options.OverwriteExistingFiles, requireApproval: false, FindMoviePath, cancellationToken);
	}

	private async Task<FileOrganizationResult> OrganizeSubtitleFile(string path, FileOrganizerType type, bool copySource, bool overwrite, bool requireApproval, Func<string, FileOrganizationResult, string?> findMediaPath, CancellationToken cancellationToken)
	{
		var result = new FileOrganizationResult
		{
			Date = DateTime.UtcNow,
			OriginalPath = path,
			OriginalFileName = Path.GetFileName(path),
			Type = type
		};
		try
		{
			result.FileSize = _fileSystem.GetFileInfo(path).Length;
			if (!FileSystemHelpers.IsFileReady(path))
			{
				result.Status = FileSortingStatus.Failure;
				result.StatusMessage = "Path is locked by other processes. Please try again later.";
				return SaveAndReturn(result, cancellationToken);
			}

			string? mediaPath = findMediaPath(path, result);
			if (string.IsNullOrWhiteSpace(mediaPath))
			{
				result.Status = FileSortingStatus.Failure;
				result.StatusMessage = "Unable to find an existing library item matching subtitle " + path;
				return SaveAndReturn(result, cancellationToken);
			}

			result.TargetPath = SafeFileTransfer.GetSubtitleTargetPath(path, mediaPath);
			PathSafety.EnsureWithinLibraryRoots(result.TargetPath, GetLibraryRoots());

			if (!_organizationService.AddToInProgressList(result, fullClientRefresh: string.IsNullOrWhiteSpace(result.Id)))
			{
				return _organizationService.GetResultBySourcePath(path)
					?? throw new OrganizationException("File is currently processed otherwise. Please try again later.");
			}

			try
			{
				if (!overwrite && File.Exists(result.TargetPath))
				{
					result.Status = FileSortingStatus.SkippedExisting;
					result.StatusMessage = $"File '{path}' already exists as '{result.TargetPath}', stopping organization";
					return SaveAndReturn(result, cancellationToken);
				}
				if (requireApproval)
				{
					result.Status = FileSortingStatus.Detected;
					result.StatusMessage = "Detected and waiting for approval.";
					return SaveAndReturn(result, cancellationToken);
				}

				_libraryMonitor.ReportFileSystemChangeBeginning(result.TargetPath);
				try
				{
					await SafeFileTransfer.TransferSingleAsync(path, result.TargetPath, copySource, overwrite, cancellationToken).ConfigureAwait(false);
					result.Status = FileSortingStatus.Success;
					result.StatusMessage = string.Empty;
				}
				finally
				{
					_libraryMonitor.ReportFileSystemChangeComplete(result.TargetPath, refreshPath: true);
				}
			}
			finally
			{
				_organizationService.RemoveFromInprogressList(result);
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			result.Status = FileSortingStatus.Failure;
			result.StatusMessage = exception.Message;
			_logger.LogError(exception, "Error organizing subtitle {Path}", path);
		}

		_organizationService.SaveResult(result, cancellationToken);
		return result;
	}

	private FileOrganizationResult SaveAndReturn(FileOrganizationResult result, CancellationToken cancellationToken)
	{
		_organizationService.SaveResult(result, cancellationToken);
		return result;
	}

	private string? FindEpisodePath(string subtitlePath, FileOrganizationResult result)
	{
		Emby.Naming.TV.EpisodeInfo episodeInfo = new EpisodeResolver(_namingOptions).Resolve(Path.ChangeExtension(subtitlePath, ".mkv"), isDirectory: false) ?? new Emby.Naming.TV.EpisodeInfo(string.Empty);
		if (string.IsNullOrWhiteSpace(episodeInfo.SeriesName) || !episodeInfo.SeasonNumber.HasValue || !episodeInfo.EpisodeNumber.HasValue)
		{
			return null;
		}

		ItemLookupInfo itemLookupInfo = _libraryManager.ParseName(episodeInfo.SeriesName);
		string seriesName = string.IsNullOrWhiteSpace(itemLookupInfo.Name) ? episodeInfo.SeriesName : itemLookupInfo.Name;
		int? seriesYear = itemLookupInfo.Year;
		result.ExtractedName = seriesName;
		result.ExtractedYear = seriesYear;
		result.ExtractedSeasonNumber = episodeInfo.SeasonNumber;
		result.ExtractedEpisodeNumber = episodeInfo.EpisodeNumber;
		result.ExtractedEndingEpisodeNumber = episodeInfo.EndingEpisodeNumber;
		return _libraryManager.GetItemList(new InternalItemsQuery
			{
				IncludeItemTypes = new[] { BaseItemKind.Episode },
				Recursive = true,
				DtoOptions = new DtoOptions(allFields: true)
			})
			.OfType<Episode>()
			.Where(episode => !string.IsNullOrWhiteSpace(episode.Path))
			.Where(episode => EpisodeIdentity.IsMatch(episodeInfo.SeasonNumber, episodeInfo.EpisodeNumber, episodeInfo.EndingEpisodeNumber, episode.ParentIndexNumber, episode.IndexNumber, episode.IndexNumberEnd))
			.Where(episode => episode.Series != null && NameUtils.GetMatchScore(seriesName, seriesYear, episode.Series).Item2 > 0)
			.Select(episode => episode.Path)
			.FirstOrDefault();
	}

	private string? FindMoviePath(string subtitlePath, FileOrganizationResult result)
	{
		Emby.Naming.Video.VideoFileInfo? movieInfo = Emby.Naming.Video.VideoResolver.Resolve(Path.ChangeExtension(subtitlePath, ".mkv"), isDirectory: false, _namingOptions);
		if (string.IsNullOrWhiteSpace(movieInfo?.Name))
		{
			return null;
		}

		ItemLookupInfo itemLookupInfo = _libraryManager.ParseName(movieInfo.Name);
		string movieName = string.IsNullOrWhiteSpace(itemLookupInfo.Name) ? movieInfo.Name : itemLookupInfo.Name;
		int? movieYear = itemLookupInfo.Year ?? movieInfo.Year;
		result.ExtractedName = movieName;
		result.ExtractedYear = movieYear;
		return _libraryManager.GetItemList(new InternalItemsQuery
			{
				IncludeItemTypes = new[] { BaseItemKind.Movie },
				Recursive = true,
				DtoOptions = new DtoOptions(allFields: true)
			})
			.OfType<Movie>()
			.Select(movie => NameUtils.GetMatchScore(movieName, movieYear, movie))
			.Where(match => match.Item2 > 0 && !string.IsNullOrWhiteSpace(match.Item1.Path))
			.OrderByDescending(match => match.Item2)
			.Select(match => match.Item1.Path)
			.FirstOrDefault();
	}

	private string[] GetLibraryRoots()
	{
		return _libraryManager.GetVirtualFolders()
			.SelectMany(folder => folder.Locations ?? Array.Empty<string>())
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.ToArray();
	}
}
