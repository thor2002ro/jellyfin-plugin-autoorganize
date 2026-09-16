using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoOrganize.Model;
using Emby.Naming.Common;
using Emby.Naming.TV;
using Emby.Naming.Video;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace AutoOrganize.Core;

public class EpisodeFileOrganizer
{
	private static readonly SemaphoreSlim SeriesCreationLock = new SemaphoreSlim(1, 1);

	private static readonly string[] SeriesSearchProviders = { "TVmaze" };

	private readonly ILibraryMonitor _libraryMonitor;

	private readonly ILibraryManager _libraryManager;

	private readonly ILogger<EpisodeFileOrganizer> _logger;

	private readonly IFileSystem _fileSystem;

	private readonly IFileOrganizationService _organizationService;

	private readonly IProviderManager _providerManager;

	private readonly NamingOptions _namingOptions;

	private FileOrganizerType CurrentFileOrganizerType => FileOrganizerType.Episode;

	public EpisodeFileOrganizer(IFileOrganizationService organizationService, IFileSystem fileSystem, ILogger<EpisodeFileOrganizer> logger, ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, IProviderManager providerManager, NamingOptions namingOptions)
	{
		_organizationService = organizationService;
		_fileSystem = fileSystem;
		_logger = logger;
		_libraryManager = libraryManager;
		_libraryMonitor = libraryMonitor;
		_providerManager = providerManager;
		_namingOptions = namingOptions;
	}

	public Task<FileOrganizationResult> OrganizeEpisodeFile(string path, TvFileOrganizationOptions options, CancellationToken cancellationToken)
	{
		return OrganizeEpisodeFile(path, options, requireApproval: false, SafeFileTransfer.GetAssociatedSubtitlePaths(path, _namingOptions), cancellationToken);
	}

	public async Task<FileOrganizationResult> OrganizeEpisodeFile(string path, TvFileOrganizationOptions options, bool requireApproval, CancellationToken cancellationToken)
	{
		return await OrganizeEpisodeFile(path, options, requireApproval, SafeFileTransfer.GetAssociatedSubtitlePaths(path, _namingOptions), cancellationToken).ConfigureAwait(false);
	}

	internal Task<FileOrganizationResult> OrganizeEpisodeFile(string path, TvFileOrganizationOptions options, bool requireApproval, IReadOnlyList<string> associatedSubtitlePaths, CancellationToken cancellationToken)
	{
		return OrganizeEpisodeFile(path, options, requireApproval, saveResult: true, associatedSubtitlePaths, cancellationToken);
	}

	private async Task<FileOrganizationResult> OrganizeEpisodeFile(string path, TvFileOrganizationOptions options, bool requireApproval, bool saveResult, IReadOnlyList<string> associatedSubtitlePaths, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path, "path");
		ArgumentNullException.ThrowIfNull(options, "options");
		cancellationToken.ThrowIfCancellationRequested();
		_logger.LogInformation("Sorting file {Path}", path);
		FileOrganizationResult result = new FileOrganizationResult
		{
			Date = DateTime.UtcNow,
			OriginalPath = path,
			OriginalFileName = Path.GetFileName(path),
			Type = FileOrganizerType.Unknown,
			FileSize = _fileSystem.GetFileInfo(path).Length,
			BundleItems = associatedSubtitlePaths.Select(subtitlePath => new FileOrganizationBundleItem { SourcePath = subtitlePath }).ToList()
		};
		try
		{
			if (!FileSystemHelpers.IsFileReady(path))
			{
				result.Status = FileSortingStatus.Failure;
				result.StatusMessage = "Path is locked by other processes. Please try again later.";
				_logger.LogInformation("Auto-organize source {Path} is locked by another process; it will be retried later", path);
				_organizationService.SaveResult(result, cancellationToken);
				return result;
			}
			Emby.Naming.TV.EpisodeInfo episodeInfo = new EpisodeResolver(_namingOptions).Resolve(path, isDirectory: false) ?? new Emby.Naming.TV.EpisodeInfo(string.Empty);
			string? text = episodeInfo.SeriesName;
			int? seriesYear = null;
			if (!string.IsNullOrEmpty(text))
			{
				ItemLookupInfo itemLookupInfo = _libraryManager.ParseName(text);
				text = itemLookupInfo.Name;
				seriesYear = itemLookupInfo.Year;
			}
			if (string.IsNullOrWhiteSpace(text))
			{
				text = episodeInfo.SeriesName;
			}
			if (!string.IsNullOrEmpty(text))
			{
				int? num = (result.ExtractedSeasonNumber = episodeInfo.SeasonNumber);
				int? num2 = (result.ExtractedEpisodeNumber = episodeInfo.EpisodeNumber);
				bool flag = episodeInfo.IsByDate && episodeInfo.Year.HasValue && episodeInfo.Month.HasValue && episodeInfo.Day.HasValue;
				DateTime? dateTime = (flag ? new DateTime?(new DateTime(episodeInfo.Year.GetValueOrDefault(), episodeInfo.Month.GetValueOrDefault(), episodeInfo.Day.GetValueOrDefault())) : ((DateTime?)null));
				if (flag || (num.HasValue && num2.HasValue))
				{
					if (flag)
					{
						_logger.LogDebug("Extracted information from {Path}. Series name {SeriesName}, Date {PremiereDate}", path, text, dateTime);
					}
					else
					{
						_logger.LogDebug("Extracted information from {Path}. Series name {SeriesName}, Season {SeasonNumber}, Episode {EpisodeNumber}", path, text, num, num2);
					}
					result.Type = CurrentFileOrganizerType;
					await OrganizeEpisode(endingEpiosdeNumber: result.ExtractedEndingEpisodeNumber = episodeInfo.EndingEpisodeNumber, sourcePath: path, seriesName: text, seriesYear: seriesYear, seasonNumber: num, episodeNumber: num2, premiereDate: dateTime, options: options, rememberCorrection: false, requireApproval: requireApproval, saveResult: saveResult, result: result, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				}
				else
				{
					string statusMessage = "Unable to determine episode number from " + path;
					result.Status = FileSortingStatus.Failure;
					result.StatusMessage = statusMessage;
					_logger.LogWarning("Unable to determine episode number from {Path}", path);
				}
			}
			else
			{
				string statusMessage2 = "Unable to determine series name from " + path;
				result.Status = FileSortingStatus.Failure;
				result.StatusMessage = statusMessage2;
				_logger.LogWarning("Unable to determine series name from {Path}", path);
			}
			FileOrganizationResult? resultBySourcePath = saveResult ? _organizationService.GetResultBySourcePath(path) : null;
			if (resultBySourcePath != null && (result.Type == FileOrganizerType.Unknown || (IsSameDetectedResult(resultBySourcePath, result) && result.Status != FileSortingStatus.Success)))
			{
				return resultBySourcePath;
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (OrganizationException ex2)
		{
			result.Status = FileSortingStatus.Failure;
			result.StatusMessage = ex2.Message;
		}
		catch (Exception ex3)
		{
			result.Status = FileSortingStatus.Failure;
			result.StatusMessage = ex3.Message;
			_logger.LogError(ex3, "Error organizing episode file {Path}", path);
		}
		if (saveResult)
		{
			_organizationService.SaveResult(result, cancellationToken);
		}
		return result;
	}

	private static bool IsSameDetectedResult(FileOrganizationResult existing, FileOrganizationResult current)
	{
		return existing.Status == current.Status
			&& existing.StatusMessage == current.StatusMessage
			&& existing.TargetPath == current.TargetPath
			&& existing.ExtractedName == current.ExtractedName
			&& existing.ExtractedYear == current.ExtractedYear
			&& existing.BundleItems.Select(item => (item.SourcePath, item.TargetPath)).SequenceEqual(current.BundleItems.Select(item => (item.SourcePath, item.TargetPath)));
	}

	public async Task<FileOrganizationResult?> DetectSeasonDirectory(string path, IReadOnlyList<FileSystemMetadata> files, TvFileOrganizationOptions options, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(files);
		ArgumentNullException.ThrowIfNull(options);
		List<string> eligibleVideoPaths = files
			.Where(file => SafeFileTransfer.IsLikelyVideoFile(file.FullName, _namingOptions) && VideoResolver.IsVideoFile(file.FullName, _namingOptions) && file.Length >= (long)options.MinFileSizeMb * 1024 * 1024)
			.Select(file => file.FullName)
			.ToList();
		SeasonDirectoryInfo? directoryInfo = TryResolveSeasonDirectoryInfo(eligibleVideoPaths, _namingOptions);
		if (directoryInfo == null)
		{
			return null;
		}

		string seriesName = directoryInfo.Value.SeriesName;
		int? seriesYear = null;
		ItemLookupInfo itemLookupInfo = _libraryManager.ParseName(seriesName);
		if (!string.IsNullOrWhiteSpace(itemLookupInfo.Name))
		{
			seriesName = itemLookupInfo.Name;
			seriesYear = itemLookupInfo.Year;
		}
		var result = new FileOrganizationResult
		{
			Date = DateTime.UtcNow,
			OriginalPath = path,
			OriginalFileName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
			ExtractedName = seriesName,
			ExtractedYear = seriesYear,
			ExtractedSeasonNumber = directoryInfo.Value.SeasonNumber,
			ExtractedEpisodeNumber = directoryInfo.Value.FirstEpisodeNumber,
			ExtractedEndingEpisodeNumber = directoryInfo.Value.LastEpisodeNumber,
			Type = CurrentFileOrganizerType,
			FileSize = files.Sum(file => file.Length)
		};
		Series? series = GetMatchingSeries(seriesName, seriesYear, string.Empty, result);
		if (series != null)
		{
			UseMetadataSeriesPathUnlessExistingSeasonFolders(series, options);
		}
		series ??= await AutoDetectSeries(seriesName, seriesYear, options, updateLibrary: false, cancellationToken).ConfigureAwait(false);
		series ??= options.AutoDetectSeries ? CreatePendingSeries(seriesName, seriesYear, options) : null;
		if (series == null)
		{
			return null;
		}
		result.ExtractedName = series.Name;
		result.ExtractedYear = series.ProductionYear;
		result.TargetPath = GetSeasonTargetPath(series, directoryInfo.Value.SeasonNumber, options);
		PathSafety.EnsureWithinLibraryRoots(result.TargetPath, GetLibraryRoots());
		result.BundleItems = GetSeasonBundleItems(files, series, options);
		if (result.BundleItems.Count == 0)
		{
			return null;
		}
		result.Status = FileSortingStatus.Detected;
		result.StatusMessage = $"Detected {result.BundleItems.Count} bundled file(s) as {series.Name} season {directoryInfo.Value.SeasonNumber}. Waiting for approval.";
		return result;
	}

	private async Task<Series?> AutoDetectSeries(string seriesName, int? seriesYear, TvFileOrganizationOptions options, bool updateLibrary, CancellationToken cancellationToken)
	{
		if (!options.AutoDetectSeries)
		{
			return null;
		}

		IReadOnlyList<RemoteSearchResult> searchResults = Array.Empty<RemoteSearchResult>();
		string successfulSearchName = seriesName;
		IReadOnlyList<string> providerNames = LibraryProviderResolver.GetMetadataProviders(_libraryManager, options.DefaultSeriesLibraryPath, nameof(Series), SeriesSearchProviders);
		foreach (string searchName in NameUtils.GetRemoteSearchCandidates(seriesName))
		{
			foreach (string providerName in providerNames)
			{
				var searchInfo = new MediaBrowser.Controller.Providers.SeriesInfo
				{
					Name = searchName,
					Year = seriesYear
				};
				var query = new RemoteSearchQuery<MediaBrowser.Controller.Providers.SeriesInfo>
				{
					SearchInfo = searchInfo,
					SearchProviderName = providerName
				};
				searchResults = (await _providerManager
					.GetRemoteSearchResults<Series, MediaBrowser.Controller.Providers.SeriesInfo>(query, cancellationToken)
					.ConfigureAwait(false))
					.ToList();

				if (searchResults.Count > 0)
				{
					successfulSearchName = searchName;
					break;
				}
			}

			if (searchResults.Count > 0)
			{
				break;
			}
		}

		if (searchResults.Count == 0)
		{
			return null;
		}

		if (!string.Equals(successfulSearchName, seriesName, StringComparison.Ordinal))
		{
			_logger.LogDebug(
				"Remote series search for {OriginalName} succeeded after retrying with normalized title {NormalizedName}",
				seriesName,
				successfulSearchName);
		}

		RemoteSearchResult? finalResult = NameUtils.SelectBestRemoteResult(searchResults, seriesName, seriesYear);
		if (finalResult == null)
		{
			return null;
		}

		var request = new EpisodeFileOrganizationRequest
		{
			NewSeriesName = finalResult.Name,
			NewSeriesProviderIds = finalResult.ProviderIds,
			NewSeriesYear = finalResult.ProductionYear,
			TargetFolder = options.DefaultSeriesLibraryPath
		};
		return await CreateNewSeries(request, finalResult, options, updateLibrary, cancellationToken).ConfigureAwait(false);
	}

	private Series CreatePendingSeries(string seriesName, int? seriesYear, TvFileOrganizationOptions options)
	{
		var series = new Series
		{
			Id = Guid.NewGuid(),
			Name = seriesName,
			ProductionYear = seriesYear,
			ProviderIds = new Dictionary<string, string>()
		};
		series.Path = Path.Combine(GetAuthorizedLibraryRoot(options.DefaultSeriesLibraryPath), GetMetadataSeriesDirectoryName(series));
		PathSafety.EnsureWithinLibraryRoots(series.Path, GetLibraryRoots());
		return series;
	}

	private static Episode CreatePendingEpisode(Series series, int? seasonNumber, int? episodeNumber, int? endingEpisodeNumber, TvFileOrganizationOptions options)
	{
		string pattern = OrganizationOptionResolver.GetEpisodePattern(options, endingEpisodeNumber.HasValue);
		return new Episode
		{
			ParentIndexNumber = seasonNumber,
			SeriesId = series.Id,
			IndexNumber = episodeNumber,
			IndexNumberEnd = endingEpisodeNumber,
			ProviderIds = new Dictionary<string, string>(),
			Name = OrganizationOptionResolver.PatternRequiresEpisodeTitle(pattern) && episodeNumber.HasValue
				? $"Episode {episodeNumber.Value.ToString(CultureInfo.InvariantCulture)}"
				: string.Empty
		};
	}


	private async Task<Series> CreateNewSeries(EpisodeFileOrganizationRequest request, RemoteSearchResult? result, TvFileOrganizationOptions options, CancellationToken cancellationToken)
	{
		return await CreateNewSeries(request, result, options, updateLibrary: true, cancellationToken).ConfigureAwait(false);
	}

	private async Task<Series> CreateNewSeries(EpisodeFileOrganizationRequest request, RemoteSearchResult? result, TvFileOrganizationOptions options, bool updateLibrary, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request, "request");
		ArgumentException.ThrowIfNullOrWhiteSpace(request.NewSeriesName, "request.NewSeriesName");
		string targetRoot = GetAuthorizedLibraryRoot(request.TargetFolder);
		_logger.LogInformation(
			"Creating or locating series {SeriesName} in library root {TargetRoot}",
			request.NewSeriesName,
			targetRoot);
		await SeriesCreationLock.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			Series? series = GetMatchingSeries(request.NewSeriesName, request.NewSeriesYear, targetRoot, null);
			if (series != null)
			{
				UseMetadataSeriesPathUnlessExistingSeasonFolders(series, options);
				if (updateLibrary)
				{
					Directory.CreateDirectory(series.Path);
				}
			}
			else
			{
				series = new Series
				{
					Id = Guid.NewGuid(),
					Name = request.NewSeriesName,
					ProductionYear = request.NewSeriesYear
				};
				string seriesDirectoryName = GetMetadataSeriesDirectoryName(series);
				series.Path = Path.Combine(targetRoot, seriesDirectoryName);
				PathSafety.EnsureWithinLibraryRoots(series.Path, GetLibraryRoots());
				if (updateLibrary)
				{
					Directory.CreateDirectory(series.Path);
				}
				series.ProviderIds = (request.NewSeriesProviderIds ?? new Dictionary<string, string>()).ToDictionary<KeyValuePair<string, string>, string, string>((KeyValuePair<string, string> x) => x.Key, (KeyValuePair<string, string> x) => x.Value);
			}
			if (!updateLibrary)
			{
				return series;
			}
			MetadataRefreshOptions refreshOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
			{
				SearchResult = result
			};
			try
			{
				await series.RefreshMetadata(refreshOptions, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception exception) when (result == null)
			{
				_logger.LogWarning(
					exception,
					"The new series {SeriesName} was created, but metadata refresh failed; organization will continue and Jellyfin can identify it during the library scan",
					series.Name);
			}

			return series;
		}
		finally
		{
			SeriesCreationLock.Release();
		}
	}

	public async Task<FileOrganizationResult> OrganizeWithCorrection(EpisodeFileOrganizationRequest request, TvFileOrganizationOptions options, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentException.ThrowIfNullOrWhiteSpace(request.ResultId);
		FileOrganizationResult? result = _organizationService.GetResult(request.ResultId);
		if (result == null)
		{
			throw new OrganizationException($"Organization result '{request.ResultId}' was not found.");
		}

		try
		{
			Series series;
			if (OrganizationOptionResolver.ShouldCreateNewSeries(request))
			{
				series = await CreateNewSeries(request, null, options, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				series = (_libraryManager.GetItemById(request.SeriesId!) as Series)
					?? throw new OrganizationException($"Series '{request.SeriesId}' was not found.");
			}

			result.Type = CurrentFileOrganizerType;
			await OrganizeEpisode(
				result.OriginalPath,
				series,
				request.SeasonNumber,
				request.EpisodeNumber,
				request.EndingEpisodeNumber,
				null,
				options,
				request.RememberCorrection,
				requireApproval: false,
				saveResult: true,
				result,
				cancellationToken).ConfigureAwait(false);
			_organizationService.SaveResult(result, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			result.Status = FileSortingStatus.Failure;
			result.StatusMessage = exception.Message;
			_logger.LogError(exception, "Error organizing corrected episode {OriginalPath}", result.OriginalPath);
			_organizationService.SaveResult(result, CancellationToken.None);
		}

		return result;
	}

	private async Task OrganizeEpisode(string sourcePath, string seriesName, int? seriesYear, int? seasonNumber, int? episodeNumber, int? endingEpiosdeNumber, DateTime? premiereDate, TvFileOrganizationOptions options, bool rememberCorrection, bool requireApproval, bool saveResult, FileOrganizationResult result, CancellationToken cancellationToken)
	{
		Series? series = GetMatchingSeries(seriesName, seriesYear, string.Empty, result);
		if (series != null)
		{
			UseMetadataSeriesPathUnlessExistingSeasonFolders(series, options);
		}
		if (series == null)
		{
			series = await AutoDetectSeries(seriesName, seriesYear, options, updateLibrary: !requireApproval, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			series ??= requireApproval && options.AutoDetectSeries
				? CreatePendingSeries(seriesName, seriesYear, options)
				: null;
			if (series == null)
			{
				string statusMessage = "Unable to find series in library matching name " + seriesName;
				result.Status = FileSortingStatus.Failure;
				result.StatusMessage = statusMessage;
				_logger.LogWarning("Unable to find series in library matching name {SeriesName}", seriesName);
				return;
			}
		}
		await OrganizeEpisode(sourcePath, series, seasonNumber, episodeNumber, endingEpiosdeNumber, premiereDate, options, rememberCorrection, requireApproval, saveResult, result, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private async Task OrganizeEpisode(string sourcePath, Series series, int? seasonNumber, int? episodeNumber, int? endingEpiosdeNumber, DateTime? premiereDate, TvFileOrganizationOptions options, bool rememberCorrection, bool requireApproval, bool saveResult, FileOrganizationResult result, CancellationToken cancellationToken)
	{
		UseMetadataSeriesPathUnlessExistingSeasonFolders(series, options);
		Episode episode = requireApproval && seasonNumber.HasValue && episodeNumber.HasValue
			? CreatePendingEpisode(series, seasonNumber, episodeNumber, endingEpiosdeNumber, options)
			: await GetMatchingEpisode(
				series,
				seasonNumber,
				episodeNumber,
				endingEpiosdeNumber,
				result,
				premiereDate,
				options,
				cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		Season season = GetMatchingSeason(series, episode, options, !requireApproval);
		if (string.IsNullOrEmpty(episode.Path))
		{
			SetEpisodeFileName(sourcePath, series, season, episode, options);
		}
		await OrganizeEpisode(sourcePath, series, episode, options, rememberCorrection, requireApproval, saveResult, result, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private async Task OrganizeEpisode(string sourcePath, Series series, Episode episode, TvFileOrganizationOptions options, bool rememberCorrection, bool requireApproval, bool saveResult, FileOrganizationResult result, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Sorting file {SourcePath} into series {SeriesPath}", sourcePath, series.Path);
		string? originalExtractedSeriesString = result.ExtractedName;
		result.ExtractedName = series.Name;
		result.ExtractedYear = series.ProductionYear;
		bool flag = string.IsNullOrWhiteSpace(result.Id);
		if (flag && saveResult)
		{
			_organizationService.SaveResult(result, cancellationToken);
		}
		bool addedToInProgress = false;
		if (saveResult && !_organizationService.AddToInProgressList(result, flag))
		{
			throw new OrganizationException("File is currently processed otherwise. Please try again later.");
		}
		addedToInProgress = saveResult;
		try
		{
			string path = episode.Path;
			if (string.IsNullOrEmpty(path))
			{
				throw new OrganizationException("Unable to sort " + sourcePath + " because target path could not be determined.");
			}
			_logger.LogInformation("Sorting file {SourcePath} to new path {NewPath}", sourcePath, path);
			result.TargetPath = path;
			result.BundleItems = SafeFileTransfer.GetSubtitleBundleItems(result, episode.ParentIndexNumber);
			PathSafety.EnsureWithinLibraryRoots(result.TargetPath, GetLibraryRoots());
			bool flag2 = File.Exists(result.TargetPath);
			List<string> otherDuplicatePaths = GetOtherDuplicatePaths(result.TargetPath, series, episode);
			if (!options.OverwriteExistingEpisodes)
			{
				if (options.CopyOriginalFile && flag2 && IsSameEpisode(sourcePath, path))
				{
					string statusMessage = $"File '{sourcePath}' already copied to new path '{path}', stopping organization";
					_logger.LogInformation("File {SourcePath} already copied to new path {NewPath}, stopping organization", sourcePath, path);
					result.Status = FileSortingStatus.SkippedExisting;
					result.StatusMessage = statusMessage;
					return;
				}
				if (flag2)
				{
					string statusMessage2 = $"File '{sourcePath}' already exists as '{path}', stopping organization";
					_logger.LogInformation("File '{SourcePath}' already exists as '{NewPath}', stopping organization", sourcePath, path);
					result.Status = FileSortingStatus.SkippedExisting;
					result.StatusMessage = statusMessage2;
					result.TargetPath = path;
					return;
				}
				if (otherDuplicatePaths.Count > 0)
				{
					string statusMessage3 = $"File '{sourcePath}' already exists as these:'{string.Join("', '", otherDuplicatePaths)}'. Stopping organization";
					_logger.LogInformation("File '{SourcePath}' already exists as these: {@OtherPaths}. Stopping organization", sourcePath, otherDuplicatePaths);
					result.Status = FileSortingStatus.SkippedExisting;
					result.StatusMessage = statusMessage3;
					result.DuplicatePaths = otherDuplicatePaths;
					return;
				}
			}
			if (requireApproval)
			{
				result.Status = FileSortingStatus.Detected;
				result.StatusMessage = "Detected and waiting for approval.";
				return;
			}
			await PerformFileSortingAsync(options, result, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (options.OverwriteExistingEpisodes)
			{
				bool flag3 = false;
				foreach (string item in otherDuplicatePaths)
				{
					_logger.LogDebug("Removing duplicate episode {Path}", item);
					_libraryMonitor.ReportFileSystemChangeBeginning(item);
					bool flag4 = !flag3 && PathSafety.PathComparer.Equals(Path.GetDirectoryName(item), Path.GetDirectoryName(result.TargetPath));
					if (flag4)
					{
						flag3 = true;
					}
					try
					{
						DeleteLibraryFile(item, flag4, result.TargetPath);
					}
					catch (Exception ex) when (((ex is IOException || ex is UnauthorizedAccessException) ? 1 : 0) != 0)
					{
						_logger.LogError(ex, "Error removing duplicate episode: {Path}", item);
					}
					finally
					{
						_libraryMonitor.ReportFileSystemChangeComplete(item, refreshPath: true);
					}
				}
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex3)
		{
			result.Status = FileSortingStatus.Failure;
			result.StatusMessage = ex3.Message;
			_logger.LogError(ex3, "Error organizing episode {SourcePath}", sourcePath);
			return;
		}
		finally
		{
			if (addedToInProgress)
			{
				_organizationService.RemoveFromInprogressList(result);
			}
		}
		if (rememberCorrection)
		{
			await SaveSmartMatchString(originalExtractedSeriesString, series, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private Task SaveSmartMatchString(string? matchString, Series series, CancellationToken cancellationToken)
	{
		if (string.IsNullOrEmpty(matchString) || matchString.Length < 3)
		{
			return Task.CompletedTask;
		}
		return _organizationService.AddSmartMatchString(series.Name, series.Name, CurrentFileOrganizerType, matchString, cancellationToken);
	}

	private void DeleteLibraryFile(string path, bool renameRelatedFiles, string targetPath)
	{
		_fileSystem.DeleteFile(path);
		if (!renameRelatedFiles)
		{
			return;
		}
		string originalFilenameWithoutExtension = Path.GetFileNameWithoutExtension(path);
		string? directoryName = Path.GetDirectoryName(path);
		if (string.IsNullOrWhiteSpace(originalFilenameWithoutExtension) || string.IsNullOrWhiteSpace(directoryName))
		{
			return;
		}
		List<string> list = (from i in _fileSystem.GetFilePaths(directoryName)
			where string.Equals(Path.GetFileNameWithoutExtension(i), originalFilenameWithoutExtension, StringComparison.OrdinalIgnoreCase)
			select i).ToList();
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(targetPath);
		foreach (string item in list)
		{
			string? directoryName2 = Path.GetDirectoryName(item);
			if (!string.IsNullOrWhiteSpace(directoryName2))
			{
				string fileName = Path.GetFileName(item);
				fileName = fileName.Replace(originalFilenameWithoutExtension, fileNameWithoutExtension, StringComparison.OrdinalIgnoreCase);
				string text = Path.Combine(directoryName2, fileName);
				PathSafety.EnsureWithinLibraryRoots(text, GetLibraryRoots());
				if (!PathSafety.AreSame(item, text) && !File.Exists(text))
				{
					File.Move(item, text);
				}
			}
		}
	}

	private List<string> GetOtherDuplicatePaths(string targetPath, Series series, Episode episode)
	{
		if (!episode.ParentIndexNumber.HasValue || !episode.IndexNumber.HasValue)
		{
			return new List<string>();
		}
		List<string> list = (from i in series.GetRecursiveChildren().OfType<Episode>().Where(delegate(Episode i)
			{
				LocationType locationType = i.LocationType;
				return (locationType != LocationType.Remote && locationType != LocationType.Virtual && EpisodeIdentity.IsMatch(episode.ParentIndexNumber, episode.IndexNumber, episode.IndexNumberEnd, i.ParentIndexNumber, i.IndexNumber, i.IndexNumberEnd)) ? true : false;
			})
			select i.Path).ToList();
		string? directoryName = Path.GetDirectoryName(targetPath);
		string targetFileNameWithoutExtension = Path.GetFileNameWithoutExtension(targetPath);
		try
		{
			IEnumerable<string> enumerable;
			if (!string.IsNullOrEmpty(directoryName))
			{
				enumerable = from i in _fileSystem.GetFilePaths(directoryName)
					where VideoResolver.IsVideoFile(i, _namingOptions) && string.Equals(Path.GetFileNameWithoutExtension(i), targetFileNameWithoutExtension, StringComparison.OrdinalIgnoreCase)
					select i;
			}
			else
			{
				IEnumerable<string> enumerable2 = Enumerable.Empty<string>();
				enumerable = enumerable2;
			}
			IEnumerable<string> collection = enumerable;
			list.AddRange(collection);
		}
		catch (IOException)
		{
		}
		List<string> libraryRoots = GetLibraryRoots();
		return (from i in list
			where IsSafeExistingLibraryPath(i, libraryRoots)
			where !PathSafety.PathComparer.Equals(i, targetPath)
			select i).Distinct<string>(PathSafety.PathComparer).ToList();
	}

	private async Task PerformFileSortingAsync(TvFileOrganizationOptions options, FileOrganizationResult result, CancellationToken cancellationToken)
	{
		string? targetPath = result.TargetPath;
		if (string.IsNullOrWhiteSpace(targetPath))
		{
			throw new OrganizationException("The target path could not be determined.");
		}
		if (PathSafety.AreSame(result.OriginalPath, targetPath))
		{
			result.Status = FileSortingStatus.Success;
			result.StatusMessage = string.Empty;
			return;
		}
		_libraryMonitor.ReportFileSystemChangeBeginning(targetPath);
		try
		{
			await SafeFileTransfer.TransferAsync(
				result.OriginalPath,
				targetPath,
				options.CopyOriginalFile,
				options.OverwriteExistingEpisodes,
				cancellationToken,
				_namingOptions,
				result.BundleItems.Select(item => item.SourcePath).ToList()).ConfigureAwait(continueOnCapturedContext: false);
			result.Status = FileSortingStatus.Success;
			result.StatusMessage = string.Empty;
		}
		finally
		{
			_libraryMonitor.ReportFileSystemChangeComplete(targetPath, refreshPath: true);
		}
	}

	private async Task<Episode> GetMatchingEpisode(
		Series series,
		int? seasonNumber,
		int? episodeNumber,
		int? endingEpisodeNumber,
		FileOrganizationResult result,
		DateTime? premiereDate,
		TvFileOrganizationOptions options,
		CancellationToken cancellationToken)
	{
		Episode? episode = series.GetRecursiveChildren()
			.OfType<Episode>()
			.FirstOrDefault(candidate =>
				candidate.ParentIndexNumber == seasonNumber
				&& candidate.IndexNumber == episodeNumber
				&& candidate.IndexNumberEnd == endingEpisodeNumber
				&& candidate.LocationType == LocationType.FileSystem
				&& string.Equals(
					Path.GetExtension(candidate.Path),
					Path.GetExtension(result.OriginalPath),
					StringComparison.OrdinalIgnoreCase));
		if (episode != null)
		{
			return episode;
		}

		return await CreateNewEpisode(
			series,
			seasonNumber,
			episodeNumber,
			endingEpisodeNumber,
			premiereDate,
			options,
			cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private Season GetMatchingSeason(Series series, Episode episode, TvFileOrganizationOptions options, bool updateLibrary)
	{
		Season? season = episode.Season;
		if (season == null)
		{
			season = series.GetRecursiveChildren()
				.OfType<Season>()
				.FirstOrDefault(candidate =>
					candidate.IndexNumber == episode.ParentIndexNumber
					&& candidate.LocationType == LocationType.FileSystem);
		}

		int? seasonNumber = episode.ParentIndexNumber ?? season?.IndexNumber;
		if (!seasonNumber.HasValue)
		{
			_logger.LogWarning(
				"No season found for {SeriesName} S{SeasonNumber}E{EpisodeNumber}",
				series.Name,
				episode.ParentIndexNumber,
				episode.IndexNumber);
			throw new OrganizationException(
				$"No season found for {series.Name} season {episode.ParentIndexNumber} episode {episode.IndexNumber}.");
		}

		season ??= new Season
		{
			Id = Guid.NewGuid(),
			SeriesId = series.Id,
			IndexNumber = seasonNumber
		};
		season.IndexNumber ??= seasonNumber;

		bool hasPath = !string.IsNullOrWhiteSpace(season.Path) && _fileSystem.DirectoryExists(season.Path);
		bool usesSeriesRoot = hasPath
			&& !string.IsNullOrWhiteSpace(series.Path)
			&& PathSafety.AreSame(season.Path, series.Path);
		if (!hasPath || (options.AlwaysCreateSeasonFolders && usesSeriesRoot))
		{
			season.Path = GetSeasonFolderPath(series, seasonNumber.Value, options);
			PathSafety.EnsureWithinLibraryRoots(season.Path, GetLibraryRoots());
			if (updateLibrary)
			{
				Directory.CreateDirectory(season.Path);
			}
		}

		return season;
	}

	private List<string> GetLibraryRoots()
	{
		return (from path in _libraryManager.GetVirtualFolders().SelectMany((VirtualFolderInfo folder) => folder.Locations ?? Array.Empty<string>())
			where !string.IsNullOrWhiteSpace(path)
			select path).ToList();
	}

	private string GetAuthorizedLibraryRoot(string? requestedRoot)
	{
		return PathSafety.GetAuthorizedLibraryRoot(requestedRoot, GetLibraryRoots());
	}

	private Series? GetMatchingSeries(string seriesName, int? seriesYear, string? targetFolder, FileOrganizationResult? result)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(seriesName, "seriesName");
		if (result != null)
		{
			result.ExtractedName = seriesName;
			result.ExtractedYear = seriesYear;
		}
		Series? series = (from Series i in _libraryManager.GetItemList(new InternalItemsQuery
			{
				IncludeItemTypes = new BaseItemKind[1] { BaseItemKind.Series },
				Recursive = true,
				DtoOptions = new DtoOptions(allFields: true)
			})
			select NameUtils.GetMatchScore(seriesName, seriesYear, i) into i
			where i.Item2 > 0
			orderby i.Item2 descending
			select i.Item1).FirstOrDefault((Series s) => IsInRequestedTargetFolder(s.Path, targetFolder));
		if (series == null)
		{
			SmartMatchResult? smartMatchResult = _organizationService.GetSmartMatchInfos().Items.FirstOrDefault((SmartMatchResult e) => e.MatchStrings.Contains<string>(seriesName, StringComparer.OrdinalIgnoreCase));
			if (smartMatchResult != null)
			{
				series = _libraryManager.GetItemList(new InternalItemsQuery
				{
					IncludeItemTypes = new BaseItemKind[1] { BaseItemKind.Series },
					Recursive = true,
					Name = smartMatchResult.ItemName,
					DtoOptions = new DtoOptions(allFields: true)
				}).Cast<Series>().FirstOrDefault((Series s) => IsInRequestedTargetFolder(s.Path, targetFolder));
			}
		}
		return series;
	}

	private static bool IsInRequestedTargetFolder(string? itemPath, string? targetFolder)
	{
		if (!string.IsNullOrWhiteSpace(itemPath))
		{
			if (!string.IsNullOrWhiteSpace(targetFolder))
			{
				return PathSafety.IsSameOrSubPath(targetFolder, itemPath);
			}
			return true;
		}
		return false;
	}

	private static bool IsSafeExistingLibraryPath(string? path, IReadOnlyList<string> libraryRoots)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		return PathSafety.IsSafelyWithinAnyRoot(path, libraryRoots);
	}

	private string GetSeriesDirectoryName(Series series, TvFileOrganizationOptions options)
	{
		string? rawName = series.Name;
		if (string.IsNullOrWhiteSpace(rawName))
		{
			throw new OrganizationException("The selected series does not have a valid name.");
		}

		string pattern = options.SeriesFolderPattern;
		if (string.IsNullOrWhiteSpace(pattern))
		{
			throw new OrganizationException("The configured series folder pattern is empty.");
		}

		int? productionYear = series.ProductionYear;
		bool patternContainsYear = pattern.Contains("%sy", StringComparison.Ordinal);
		string tokenName = patternContainsYear
			? NameUtils.RemoveTerminalYear(rawName, productionYear)
			: rawName.Trim();
		string fullName = patternContainsYear
			? tokenName
			: NameUtils.EnsureTerminalYear(rawName, productionYear);
		string filename = pattern
			.Replace("%sn", tokenName, StringComparison.Ordinal)
			.Replace("%s.n", tokenName.Replace(' ', '.'), StringComparison.Ordinal)
			.Replace("%s_n", tokenName.Replace(' ', '_'), StringComparison.Ordinal)
			.Replace("%sy", productionYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.Ordinal)
			.Replace("%fn", fullName, StringComparison.Ordinal);
		return _fileSystem.GetValidFilename(filename).Trim();
	}

	private string GetMetadataSeriesDirectoryName(Series series)
	{
		string? name = series.Name;
		if (string.IsNullOrWhiteSpace(name))
		{
			throw new OrganizationException("The selected series does not have a valid name.");
		}
		return _fileSystem.GetValidFilename(NameUtils.EnsureTerminalYear(name.Trim(), series.ProductionYear)).Trim();
	}

	private void UseMetadataSeriesPathUnlessExistingSeasonFolders(Series series, TvFileOrganizationOptions options)
	{
		if (HasExistingSeasonFolders(series) && _fileSystem.DirectoryExists(series.Path))
		{
			return;
		}
		series.Path = Path.Combine(GetAuthorizedLibraryRoot(options.DefaultSeriesLibraryPath), GetMetadataSeriesDirectoryName(series));
		PathSafety.EnsureWithinLibraryRoots(series.Path, GetLibraryRoots());
	}

	private async Task<Episode> CreateNewEpisode(
		Series series,
		int? seasonNumber,
		int? episodeNumber,
		int? endingEpisodeNumber,
		DateTime? premiereDate,
		TvFileOrganizationOptions options,
		CancellationToken cancellationToken)
	{
		var searchInfo = new MediaBrowser.Controller.Providers.EpisodeInfo
		{
			IndexNumber = episodeNumber,
			IndexNumberEnd = endingEpisodeNumber,
			MetadataCountryCode = series.GetPreferredMetadataCountryCode(),
			MetadataLanguage = series.GetPreferredMetadataLanguage(),
			ParentIndexNumber = seasonNumber,
			SeriesProviderIds = series.ProviderIds,
			PremiereDate = premiereDate
		};
		RemoteSearchResult? remoteSearchResult = null;
		IReadOnlyList<string> providerNames = LibraryProviderResolver.GetMetadataProviders(_libraryManager, options.DefaultSeriesLibraryPath, nameof(Episode), SeriesSearchProviders);
		foreach (string providerName in providerNames)
		{
			remoteSearchResult = (await _providerManager
				.GetRemoteSearchResults<Episode, MediaBrowser.Controller.Providers.EpisodeInfo>(
					new RemoteSearchQuery<MediaBrowser.Controller.Providers.EpisodeInfo>
					{
						SearchInfo = searchInfo,
						SearchProviderName = providerName
					},
					cancellationToken)
				.ConfigureAwait(continueOnCapturedContext: false))
				.FirstOrDefault();
			if (remoteSearchResult != null)
			{
				break;
			}
		}

		if (remoteSearchResult == null)
		{
			if (options.PreserveOriginalFilename && seasonNumber.HasValue && episodeNumber.HasValue)
			{
				_logger.LogWarning(
					"No provider metadata found for {SeriesName} S{SeasonNumber}E{EpisodeNumber}; continuing because original filename preservation is enabled",
					series.Name,
					seasonNumber,
					episodeNumber);
				return new Episode
				{
					ParentIndexNumber = seasonNumber,
					SeriesId = series.Id,
					IndexNumber = episodeNumber,
					IndexNumberEnd = endingEpisodeNumber,
					ProviderIds = new Dictionary<string, string>(),
					Name = $"Episode {episodeNumber.Value.ToString(CultureInfo.InvariantCulture)}"
				};
			}

			string message = $"No provider metadata found for {series.Name} season {seasonNumber} episode {episodeNumber}";
			_logger.LogWarning(
				"No provider metadata found for {SeriesName} S{SeasonNumber}E{EpisodeNumber}",
				series.Name,
				seasonNumber,
				episodeNumber);
			throw new OrganizationException(message);
		}

		seasonNumber ??= remoteSearchResult.ParentIndexNumber;
		episodeNumber ??= remoteSearchResult.IndexNumber;
		endingEpisodeNumber ??= remoteSearchResult.IndexNumberEnd;
		return new Episode
		{
			ParentIndexNumber = seasonNumber,
			SeriesId = series.Id,
			IndexNumber = episodeNumber,
			IndexNumberEnd = endingEpisodeNumber,
			ProviderIds = remoteSearchResult.ProviderIds,
			Name = remoteSearchResult.Name
		};
	}

	private string GetSeasonFolderPath(Series series, int seasonNumber, TvFileOrganizationOptions options)
	{
		string path = series.Path;
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new OrganizationException("The selected series does not have a valid library path.");
		}
		if (OrganizationOptionResolver.ShouldUseSeriesRoot(options, ContainsEpisodesWithoutSeasonFolders(series)))
		{
			return path;
		}
		if (seasonNumber == 0)
		{
			return Path.Combine(path, _fileSystem.GetValidFilename(options.SeasonZeroFolderName));
		}
		string filename = EpisodeNameFormatter.FormatSeasonFolder(options.SeasonFolderPattern, seasonNumber);
		return Path.Combine(path, _fileSystem.GetValidFilename(filename));
	}

	private string GetSeasonTargetPath(Series series, int seasonNumber, TvFileOrganizationOptions options)
	{
		Season? season = series.GetRecursiveChildren()
			.OfType<Season>()
			.FirstOrDefault(candidate => candidate.IndexNumber == seasonNumber && candidate.LocationType == LocationType.FileSystem);
		bool usesSeriesRoot = !string.IsNullOrWhiteSpace(season?.Path)
			&& !string.IsNullOrWhiteSpace(series.Path)
			&& PathSafety.AreSame(season.Path, series.Path);
		if (!string.IsNullOrWhiteSpace(season?.Path) && _fileSystem.DirectoryExists(season.Path) && !(options.AlwaysCreateSeasonFolders && usesSeriesRoot))
		{
			return season.Path;
		}
		return GetSeasonFolderPath(series, seasonNumber, options);
	}

	private IReadOnlyList<FileOrganizationBundleItem> GetSeasonBundleItems(IReadOnlyList<FileSystemMetadata> files, Series series, TvFileOrganizationOptions options)
	{
		var items = new List<FileOrganizationBundleItem>();
		var addedSourcePaths = new HashSet<string>(PathSafety.PathComparer);
		var resolver = new EpisodeResolver(_namingOptions);
		List<FileSystemMetadata> sortedFiles = files.OrderBy(file => file.FullName, PathSafety.PathComparer).ToList();
		SubtitleAssociationMap subtitleAssociations = SubtitleAssociationMap.Create(
			sortedFiles.Where(file => SafeFileTransfer.IsLikelyVideoFile(file.FullName, _namingOptions)).Select(file => file.FullName),
			sortedFiles.Where(file => SafeFileTransfer.IsSubtitleFile(file.FullName)).Select(file => file.FullName),
			_namingOptions);
		foreach (FileSystemMetadata file in sortedFiles)
		{
			if (!SafeFileTransfer.IsLikelyVideoFile(file.FullName, _namingOptions) || !VideoResolver.IsVideoFile(file.FullName, _namingOptions) || file.Length < (long)options.MinFileSizeMb * 1024 * 1024)
			{
				continue;
			}
			Emby.Naming.TV.EpisodeInfo episodeInfo = resolver.Resolve(file.FullName, isDirectory: false) ?? new Emby.Naming.TV.EpisodeInfo(string.Empty);
			string? targetPath = GetSeasonBundleTargetPath(file.FullName, series, episodeInfo, options);
			if (string.IsNullOrWhiteSpace(targetPath))
			{
				continue;
			}
			items.Add(new FileOrganizationBundleItem
			{
				SourcePath = file.FullName,
				TargetPath = targetPath,
				SeasonNumber = episodeInfo.SeasonNumber
			});
			addedSourcePaths.Add(file.FullName);
			AddSubtitleBundleItems(file.FullName, targetPath, episodeInfo.SeasonNumber, subtitleAssociations, items, addedSourcePaths);
		}
		return items;
	}

	private string? GetSeasonBundleTargetPath(string sourcePath, Series series, Emby.Naming.TV.EpisodeInfo episodeInfo, TvFileOrganizationOptions options)
	{
		if (!episodeInfo.SeasonNumber.HasValue || !episodeInfo.EpisodeNumber.HasValue)
		{
			return null;
		}

		string pattern = OrganizationOptionResolver.GetEpisodePattern(options, episodeInfo.EndingEpisodeNumber.HasValue);
		if (string.IsNullOrWhiteSpace(pattern))
		{
			return null;
		}

		string episodeTitle = OrganizationOptionResolver.PatternRequiresEpisodeTitle(pattern)
			? $"Episode {episodeInfo.EpisodeNumber.Value.ToString(CultureInfo.InvariantCulture)}"
			: string.Empty;
		string filename = EpisodeNameFormatter.FormatEpisodeFile(
			pattern,
			sourcePath,
			_fileSystem.GetValidFilename(series.Name).Trim(),
			episodeTitle,
			episodeInfo.SeasonNumber.Value,
			episodeInfo.EpisodeNumber.Value,
			episodeInfo.EndingEpisodeNumber);
		return Path.Combine(GetSeasonTargetPath(series, episodeInfo.SeasonNumber.Value, options), _fileSystem.GetValidFilename(filename).Trim());
	}

	private static void AddSubtitleBundleItems(string sourceVideoPath, string targetVideoPath, int? seasonNumber, SubtitleAssociationMap subtitleAssociations, List<FileOrganizationBundleItem> items, HashSet<string> addedSourcePaths)
	{
		foreach (string subtitlePath in subtitleAssociations.GetSubtitlePaths(sourceVideoPath))
		{
			if (!addedSourcePaths.Add(subtitlePath))
			{
				continue;
			}
			items.Add(new FileOrganizationBundleItem
			{
				SourcePath = subtitlePath,
				TargetPath = SafeFileTransfer.GetSubtitleTargetPath(subtitlePath, targetVideoPath, sourceVideoPath),
				SeasonNumber = seasonNumber
			});
		}
	}

	internal static SeasonDirectoryInfo? TryResolveSeasonDirectoryInfo(IEnumerable<string> paths, NamingOptions namingOptions)
	{
		ArgumentNullException.ThrowIfNull(paths);
		ArgumentNullException.ThrowIfNull(namingOptions);
		var resolver = new EpisodeResolver(namingOptions);
		List<string> videoPaths = paths
			.Where(path => VideoResolver.IsVideoFile(path, namingOptions))
			.OrderBy(path => path, PathSafety.PathComparer)
			.ToList();
		if (videoPaths.Count < 2)
		{
			return null;
		}
		Emby.Naming.TV.EpisodeInfo first = resolver.Resolve(videoPaths[0], isDirectory: false) ?? new Emby.Naming.TV.EpisodeInfo(string.Empty);
		Emby.Naming.TV.EpisodeInfo middle = resolver.Resolve(videoPaths[videoPaths.Count / 2], isDirectory: false) ?? new Emby.Naming.TV.EpisodeInfo(string.Empty);
		Emby.Naming.TV.EpisodeInfo last = resolver.Resolve(videoPaths[videoPaths.Count - 1], isDirectory: false) ?? new Emby.Naming.TV.EpisodeInfo(string.Empty);
		if (!IsSameSeriesSeason(first, middle) || !IsSameSeriesSeason(first, last))
		{
			return null;
		}
		string? seriesName = first.SeriesName;
		int? seasonNumber = first.SeasonNumber;
		int? firstEpisodeNumber = first.EpisodeNumber;
		int? lastEpisodeNumber = last.EndingEpisodeNumber ?? last.EpisodeNumber;
		if (string.IsNullOrWhiteSpace(seriesName) || !seasonNumber.HasValue || !firstEpisodeNumber.HasValue || !lastEpisodeNumber.HasValue)
		{
			return null;
		}
		return new SeasonDirectoryInfo(seriesName, seasonNumber.Value, firstEpisodeNumber.Value, lastEpisodeNumber.Value);
	}

	private static bool IsSameSeriesSeason(Emby.Naming.TV.EpisodeInfo first, Emby.Naming.TV.EpisodeInfo second)
	{
		return !string.IsNullOrWhiteSpace(first.SeriesName)
			&& !string.IsNullOrWhiteSpace(second.SeriesName)
			&& string.Equals(first.SeriesName, second.SeriesName, StringComparison.OrdinalIgnoreCase)
			&& first.SeasonNumber.HasValue
			&& first.SeasonNumber == second.SeasonNumber;
	}

	internal readonly struct SeasonDirectoryInfo
	{
		public SeasonDirectoryInfo(string seriesName, int seasonNumber, int firstEpisodeNumber, int lastEpisodeNumber)
		{
			SeriesName = seriesName;
			SeasonNumber = seasonNumber;
			FirstEpisodeNumber = firstEpisodeNumber;
			LastEpisodeNumber = lastEpisodeNumber;
		}

		public string SeriesName { get; }

		public int SeasonNumber { get; }

		public int FirstEpisodeNumber { get; }

		public int LastEpisodeNumber { get; }
	}

	private bool ContainsEpisodesWithoutSeasonFolders(Series series)
	{
		foreach (BaseItem child in series.Children)
		{
			if (child is Video)
			{
				return true;
			}
		}
		return false;
	}

	private bool HasExistingSeasonFolders(Series series)
	{
		return series.GetRecursiveChildren()
			.OfType<Season>()
			.Any(season => season.LocationType == LocationType.FileSystem
				&& !string.IsNullOrWhiteSpace(season.Path)
				&& _fileSystem.DirectoryExists(season.Path)
				&& !PathSafety.AreSame(season.Path, series.Path));
	}

	private void SetEpisodeFileName(
		string sourcePath,
		Series series,
		Season season,
		Episode episode,
		TvFileOrganizationOptions options)
	{
		if (string.IsNullOrWhiteSpace(series.Name))
		{
			throw new OrganizationException("The selected series does not have a valid name.");
		}

		if (string.IsNullOrWhiteSpace(season.Path))
		{
			throw new OrganizationException("The destination season does not have a valid library path.");
		}

		if (!episode.IndexNumber.HasValue || !season.IndexNumber.HasValue)
		{
			throw new OrganizationException("The season and episode numbers are required to build the destination filename.");
		}

		string pattern = OrganizationOptionResolver.GetEpisodePattern(options, episode.IndexNumberEnd.HasValue);
		if (string.IsNullOrWhiteSpace(pattern))
		{
			throw new OrganizationException("The configured episode name pattern is empty.");
		}

		string episodeTitle;
		if (string.IsNullOrWhiteSpace(episode.Name))
		{
			if (OrganizationOptionResolver.PatternRequiresEpisodeTitle(pattern))
			{
				throw new OrganizationException("No episode title was returned by the metadata provider.");
			}

			episodeTitle = string.Empty;
		}
		else
		{
			episodeTitle = _fileSystem.GetValidFilename(episode.Name).Trim();
		}

		string seriesName = _fileSystem.GetValidFilename(series.Name).Trim();
		string filename = EpisodeNameFormatter.FormatEpisodeFile(
			pattern,
			sourcePath,
			seriesName,
			episodeTitle,
			season.IndexNumber.Value,
			episode.IndexNumber.Value,
			episode.IndexNumberEnd);
		episode.Path = Path.Combine(season.Path, _fileSystem.GetValidFilename(filename).Trim());
	}

	private bool IsSameEpisode(string sourcePath, string newPath)
	{
		try
		{
			FileSystemMetadata fileInfo = _fileSystem.GetFileInfo(sourcePath);
			FileSystemMetadata fileInfo2 = _fileSystem.GetFileInfo(newPath);
			if (fileInfo.Length == fileInfo2.Length)
			{
				return true;
			}
		}
		catch (FileNotFoundException)
		{
			return false;
		}
		catch (IOException)
		{
			return false;
		}
		return false;
	}
}
