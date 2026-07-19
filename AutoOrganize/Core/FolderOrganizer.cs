using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoOrganize.Model;
using Emby.Naming.Common;
using Emby.Naming.Video;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace AutoOrganize.Core;

public sealed class FolderOrganizer
{
	private readonly ILibraryMonitor _libraryMonitor;
	private readonly ILibraryManager _libraryManager;
	private readonly ILoggerFactory _loggerFactory;
	private readonly ILogger<FolderOrganizer> _logger;
	private readonly IFileSystem _fileSystem;
	private readonly IFileOrganizationService _organizationService;
	private readonly IProviderManager _providerManager;
	private readonly NamingOptions _namingOptions;

	public FolderOrganizer(ILibraryManager libraryManager, ILoggerFactory loggerFactory, IFileSystem fileSystem, ILibraryMonitor libraryMonitor, IFileOrganizationService organizationService, IProviderManager providerManager, NamingOptions namingOptions)
	{
		_libraryManager = libraryManager;
		_loggerFactory = loggerFactory;
		_logger = loggerFactory.CreateLogger<FolderOrganizer>();
		_fileSystem = fileSystem;
		_libraryMonitor = libraryMonitor;
		_organizationService = organizationService;
		_providerManager = providerManager;
		_namingOptions = namingOptions;
	}

	public Task Organize(TvFileOrganizationOptions options, IProgress<double> progress, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(options);
		var organizer = new EpisodeFileOrganizer(_organizationService, _fileSystem, _loggerFactory.CreateLogger<EpisodeFileOrganizer>(), _libraryManager, _libraryMonitor, _providerManager, _namingOptions);
		var subtitleOrganizer = new SubtitleFileOrganizer(_organizationService, _fileSystem, _loggerFactory.CreateLogger<SubtitleFileOrganizer>(), _libraryManager, _libraryMonitor, _namingOptions);
		return Organize(
			"TV",
			options.WatchLocations,
			options.MinFileSizeMb,
			options.DeleteEmptyFolders,
			options.ExtendedClean,
			options.LeftOverFileExtensionsToDelete,
			(path, token) => SafeFileTransfer.IsSubtitleFile(path)
				? subtitleOrganizer.OrganizeEpisodeSubtitleFile(path, options, token)
				: organizer.OrganizeEpisodeFile(path, options, options.RequireApproval, token),
			options.RequireApproval
				? (directory, files, token) => organizer.DetectSeasonDirectory(directory, files, options, token)
				: null,
			progress,
			cancellationToken);
	}

	public Task Organize(MovieFileOrganizationOptions options, IProgress<double> progress, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(options);
		var organizer = new MovieFileOrganizer(_organizationService, _fileSystem, _loggerFactory.CreateLogger<MovieFileOrganizer>(), _libraryManager, _libraryMonitor, _providerManager, _namingOptions);
		var subtitleOrganizer = new SubtitleFileOrganizer(_organizationService, _fileSystem, _loggerFactory.CreateLogger<SubtitleFileOrganizer>(), _libraryManager, _libraryMonitor, _namingOptions);
		return Organize(
			"movie",
			options.WatchLocations,
			options.MinFileSizeMb,
			options.DeleteEmptyFolders,
			options.ExtendedClean,
			options.LeftOverFileExtensionsToDelete,
			(path, token) => SafeFileTransfer.IsSubtitleFile(path)
				? subtitleOrganizer.OrganizeMovieSubtitleFile(path, options, token)
				: organizer.OrganizeMovieFile(path, options, options.OverwriteExistingFiles, options.RequireApproval, token),
			null,
			progress,
			cancellationToken);
	}

	public Task<FileOrganizationResult> OrganizeTvSeasonDirectory(string path, TvFileOrganizationOptions options, CancellationToken cancellationToken)
	{
		return OrganizeTvSeasonDirectory(path, options, approvedBundleItems: null, cancellationToken);
	}

	public async Task<FileOrganizationResult> OrganizeTvSeasonDirectory(string path, TvFileOrganizationOptions options, IReadOnlyList<FileOrganizationBundleItem>? approvedBundleItems, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(options);
		if (options.MinFileSizeMb < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(options.MinFileSizeMb), "Minimum file size cannot be negative.");
		}
		var organizer = new EpisodeFileOrganizer(_organizationService, _fileSystem, _loggerFactory.CreateLogger<EpisodeFileOrganizer>(), _libraryManager, _libraryMonitor, _providerManager, _namingOptions);
		var subtitleOrganizer = new SubtitleFileOrganizer(_organizationService, _fileSystem, _loggerFactory.CreateLogger<SubtitleFileOrganizer>(), _libraryManager, _libraryMonitor, _namingOptions);
		long minimumFileSize = (long)options.MinFileSizeMb * 1024 * 1024;
		if (approvedBundleItems != null)
		{
			return await OrganizeApprovedBundle(path, approvedBundleItems, options, cancellationToken).ConfigureAwait(false);
		}
		List<FileSystemMetadata> foundFiles = GetFilesToOrganize(path, recursive: false);
		var videoBaseNames = new HashSet<string>(
			foundFiles
				.Where(IsVideoFile)
				.Select(file => Path.Combine(Path.GetDirectoryName(file.FullName) ?? string.Empty, Path.GetFileNameWithoutExtension(file.FullName))),
			PathSafety.PathComparer);
		List<FileSystemMetadata> eligibleFiles = foundFiles
			.Where(file => CanOrganize(file, minimumFileSize, videoBaseNames))
			.OrderBy(file => file.FullName, PathSafety.PathComparer)
			.ToList();
		var result = new FileOrganizationResult
		{
			Date = DateTime.UtcNow,
			OriginalPath = path,
			OriginalFileName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
			Type = FileOrganizerType.Episode,
			FileSize = eligibleFiles.Sum(file => file.Length)
		};
		int succeeded = 0;
		int skipped = 0;
		int failed = 0;
		foreach (FileSystemMetadata file in eligibleFiles)
		{
			FileOrganizationResult item = SafeFileTransfer.IsSubtitleFile(file.FullName)
				? await subtitleOrganizer.ApproveEpisodeSubtitleFile(file.FullName, options, cancellationToken).ConfigureAwait(false)
				: await organizer.OrganizeEpisodeFile(file.FullName, options, requireApproval: false, cancellationToken).ConfigureAwait(false);
			result.TargetPath ??= string.IsNullOrWhiteSpace(item.TargetPath) ? null : Path.GetDirectoryName(item.TargetPath);
			result.ExtractedName ??= item.ExtractedName;
			result.ExtractedYear ??= item.ExtractedYear;
			result.ExtractedSeasonNumber ??= item.ExtractedSeasonNumber;
			switch (item.Status)
			{
				case FileSortingStatus.Success:
					succeeded++;
					break;
				case FileSortingStatus.SkippedExisting:
					skipped++;
					break;
				default:
					failed++;
					break;
			}
		}
		result.Status = failed == 0 ? FileSortingStatus.Success : FileSortingStatus.Failure;
		result.StatusMessage = failed == 0
			? $"Approved bundle. Organized {succeeded}, skipped {skipped}."
			: $"Approved bundle with {failed} failure(s). Organized {succeeded}, skipped {skipped}.";
		_organizationService.SaveResult(result, cancellationToken);
		return result;
	}

	private async Task Organize(
		string mediaType,
		IEnumerable<string>? configuredWatchLocations,
		int minFileSizeMb,
		bool deleteEmptyFolders,
		bool extendedClean,
		IEnumerable<string>? configuredDeleteExtensions,
		Func<string, CancellationToken, Task<FileOrganizationResult>> organizeFile,
		Func<string, IReadOnlyList<FileSystemMetadata>, CancellationToken, Task<FileOrganizationResult?>>? detectDirectory,
		IProgress<double> progress,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(progress);
		if (minFileSizeMb < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(minFileSizeMb), "Minimum file size cannot be negative.");
		}
		long minimumFileSize = (long)minFileSizeMb * 1024 * 1024;
		List<string> configuredLocations = (configuredWatchLocations ?? Array.Empty<string>())
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.ToList();
		List<string> libraryFolderPaths = (from path in _libraryManager.GetVirtualFolders().SelectMany(folder => folder.Locations ?? Array.Empty<string>())
			where !string.IsNullOrWhiteSpace(path)
			select path).ToList();
		var checkedLocations = configuredLocations
			.Select(path => new { Path = path, Error = GetWatchLocationError(path, mediaType, libraryFolderPaths) })
			.ToList();
		List<string> skippedLocations = checkedLocations
			.Select(location => location.Error)
			.Where(message => message != null)
			.Cast<string>()
			.ToList();
		List<string> watchLocations = checkedLocations
			.Where(location => location.Error == null)
			.Select(location => PathSafety.Normalize(location.Path))
			.Distinct(PathSafety.PathComparer)
			.ToList();
		FileOrganizationResult scanLog = CreateScanLog(mediaType, $"Scan started. Configured {configuredLocations.Count}; valid {watchLocations.Count}; minimum size {minFileSizeMb} MB.", FileSortingStatus.Success);
		_organizationService.SaveResult(scanLog, cancellationToken);
		AddPluginLogLine($"{mediaType} scan started. Configured={configuredLocations.Count}; valid={watchLocations.Count}; minSizeMb={minFileSizeMb}.");
		foreach (string skippedLocation in skippedLocations)
		{
			AddPluginLogLine($"{mediaType} watch folder skipped: {skippedLocation}");
		}
		List<FileSystemMetadata> foundFiles = watchLocations.SelectMany(GetFilesToOrganize).ToList();
		var videoBaseNames = new HashSet<string>(
			foundFiles
				.Where(IsVideoFile)
				.Select(file => Path.Combine(Path.GetDirectoryName(file.FullName) ?? string.Empty, Path.GetFileNameWithoutExtension(file.FullName))),
			PathSafety.PathComparer);
		List<FileSystemMetadata> eligibleFiles = (from file in foundFiles.OrderBy(_fileSystem.GetCreationTimeUtc)
			where CanOrganize(file, minimumFileSize, videoBaseNames)
			select file).ToList();
		AddPluginLogLine($"{mediaType} scan found {foundFiles.Count} file(s), {eligibleFiles.Count} eligible media/subtitle file(s).");
		int succeeded = 0;
		int detected = 0;
		int failed = 0;
		int skipped = 0;
		var processedFolders = new HashSet<string>(PathSafety.PathComparer);
		progress.Report(1);
		if (detectDirectory != null)
		{
			var bundledFiles = new HashSet<string>(PathSafety.PathComparer);
			var detectedResults = new List<FileOrganizationResult>();
			var directoryGroups = foundFiles
				.GroupBy(file => Path.GetDirectoryName(file.FullName) ?? string.Empty)
				.Where(group => !string.IsNullOrWhiteSpace(group.Key))
				.ToList();
			for (int groupIndex = 0; groupIndex < directoryGroups.Count; groupIndex++)
			{
				var group = directoryGroups[groupIndex];
				cancellationToken.ThrowIfCancellationRequested();
				List<FileSystemMetadata> files = group.OrderBy(file => file.FullName, PathSafety.PathComparer).ToList();
				FileOrganizationResult? result = await detectDirectory(group.Key, files, cancellationToken).ConfigureAwait(false);
				progress.Report(1 + 9.0 * (groupIndex + 1) / directoryGroups.Count);
				if (result == null)
				{
					continue;
				}
				detectedResults.Add(result);
				foreach (FileOrganizationBundleItem item in result.BundleItems)
				{
					bundledFiles.Add(item.SourcePath);
				}
				AddPluginLogLine($"{mediaType} season detected: {group.Key} -> {result.TargetPath ?? "(not resolved)"} {result.StatusMessage}");
			}
			foreach (FileOrganizationResult result in MergeSeasonBundles(detectedResults))
			{
				_organizationService.SaveResult(result, cancellationToken);
				await DeleteBundledFileResults(result, detectedResults, cancellationToken).ConfigureAwait(false);
				detected++;
				AddPluginLogLine($"{mediaType} bundle saved: {result.OriginalPath} -> {result.TargetPath ?? "(not resolved)"} {result.StatusMessage}");
			}
			eligibleFiles = eligibleFiles.Where(file => !bundledFiles.Contains(file.FullName)).ToList();
		}
		progress.Report(10);
		for (int index = 0; index < eligibleFiles.Count; index++)
		{
			FileSystemMetadata file = eligibleFiles[index];
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				FileOrganizationResult result = await organizeFile(file.FullName, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				string? directory = Path.GetDirectoryName(file.FullName);
				if (result.Status == FileSortingStatus.Success && !string.IsNullOrEmpty(directory))
				{
					processedFolders.Add(directory);
				}
				switch (result.Status)
				{
					case FileSortingStatus.Success:
						succeeded++;
						break;
					case FileSortingStatus.Detected:
						detected++;
						break;
					case FileSortingStatus.SkippedExisting:
						skipped++;
						break;
					default:
						failed++;
						break;
				}
				AddPluginLogLine($"{mediaType} file {result.Status}: {file.FullName} -> {result.TargetPath ?? "(not resolved)"} {result.StatusMessage}");
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception exception)
			{
				failed++;
				AddPluginLogLine($"{mediaType} file failed: {file.FullName} {exception.Message}");
				_logger.LogError(exception, "Error organizing {MediaType} file {Path}", mediaType, file.FullName);
			}
			progress.Report(10 + 89.0 * (index + 1) / eligibleFiles.Count);
		}
		cancellationToken.ThrowIfCancellationRequested();
		progress.Report(99);
		List<string> deleteExtensions = GetDeleteExtensions(configuredDeleteExtensions);
		Clean(processedFolders, watchLocations, deleteEmptyFolders, deleteExtensions, mediaType, cancellationToken);
		if (extendedClean)
		{
			Clean(watchLocations, watchLocations, deleteEmptyFolders, deleteExtensions, mediaType, cancellationToken);
		}
		SaveScanLog(scanLog, mediaType, configuredLocations.Count, watchLocations.Count, skippedLocations, foundFiles.Count, eligibleFiles.Count, succeeded, detected, skipped, failed, minFileSizeMb, cancellationToken);
		progress.Report(100);
	}

	private static List<string> GetDeleteExtensions(IEnumerable<string>? configuredDeleteExtensions)
	{
		return (configuredDeleteExtensions ?? Array.Empty<string>())
			.Where(extension => extension != null)
			.Select(extension => extension.Trim().TrimStart('.'))
			.Where(extension => extension.Length > 0)
			.Select(extension => "." + extension)
			.ToList();
	}

	private bool CanOrganize(FileSystemMetadata file, long minimumFileSize, HashSet<string> videoBaseNames)
	{
		try
		{
			return (SafeFileTransfer.IsSubtitleFile(file.FullName) && !HasMatchingVideoSidecar(file.FullName, videoBaseNames))
				|| (IsVideoFile(file) && file.Length >= minimumFileSize);
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Error checking media file {FileName}", file.Name);
			return false;
		}
	}

	private bool IsVideoFile(FileSystemMetadata file)
	{
		try
		{
			if (!SafeFileTransfer.IsLikelyVideoFile(file.FullName, _namingOptions))
			{
				return false;
			}
			return VideoResolver.IsVideoFile(file.FullName, _namingOptions);
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Error checking media file {FileName}", file.Name);
			return false;
		}
	}

	private static bool HasMatchingVideoSidecar(string subtitlePath, HashSet<string> videoBaseNames)
	{
		string subtitleName = Path.GetFileNameWithoutExtension(subtitlePath);
		while (!string.IsNullOrWhiteSpace(subtitleName))
		{
			if (videoBaseNames.Contains(Path.Combine(Path.GetDirectoryName(subtitlePath) ?? string.Empty, subtitleName)))
			{
				return true;
			}

			int separator = subtitleName.LastIndexOf('.');
			if (separator < 0)
			{
				return false;
			}

			subtitleName = subtitleName.Substring(0, separator);
		}

		return false;
	}

	private string? GetWatchLocationError(string path, string mediaType, IReadOnlyList<string> libraryFolderPaths)
	{
		if (!PathSafety.TryNormalize(path, out string normalizedPath) || !Directory.Exists(normalizedPath))
		{
			_logger.LogWarning("{MediaType} watch folder {WatchFolder} is not a valid existing absolute path and will be skipped", mediaType, path);
			return $"{path}: not an existing absolute path";
		}
		if (libraryFolderPaths.Any(libraryPath => PathSafety.PathsOverlap(libraryPath, normalizedPath) && !PathSafety.HasHiddenSegmentUnderRoot(libraryPath, normalizedPath)))
		{
			_logger.LogWarning("{MediaType} watch folder {WatchFolder} overlaps a Jellyfin library and will be skipped", mediaType, path);
			return $"{path}: overlaps a Jellyfin library";
		}
		return null;
	}

	private FileOrganizationResult CreateScanLog(string mediaType, string message, FileSortingStatus status)
	{
		return new FileOrganizationResult
		{
			Date = DateTime.UtcNow,
			OriginalPath = "Jellyfin logs: AutoOrganize",
			OriginalFileName = mediaType + " scan",
			ExtractedName = mediaType + " scan",
			Status = status,
			StatusMessage = message,
			Type = FileOrganizerType.Log
		};
	}

	private void SaveScanLog(FileOrganizationResult scanLog, string mediaType, int configuredCount, int watchCount, List<string> skippedLocations, int foundCount, int eligibleCount, int succeeded, int detected, int skipped, int failed, int minFileSizeMb, CancellationToken cancellationToken)
	{
		string skippedMessage = skippedLocations.Count == 0
			? string.Empty
			: " Skipped watch folders: " + string.Join("; ", skippedLocations) + ".";
		string message = $"Scanned {watchCount} of {configuredCount} configured {mediaType} watch folder(s); found {foundCount} file(s), {eligibleCount} eligible media/subtitle file(s). Videos must be at or above {minFileSizeMb} MB. Organized {succeeded}, detected {detected}, skipped {skipped}, failed {failed}.{skippedMessage}";
		AddPluginLogLine(message);
		scanLog.Date = DateTime.UtcNow;
		scanLog.Status = watchCount == 0 && configuredCount > 0 ? FileSortingStatus.Failure : FileSortingStatus.Success;
		scanLog.StatusMessage = message;
		_organizationService.SaveResult(scanLog, cancellationToken);
	}

	private void AddPluginLogLine(string message)
	{
		_logger.LogInformation("AutoOrganize: {Message}", message);
	}

	private List<FileSystemMetadata> GetFilesToOrganize(string path)
	{
		return GetFilesToOrganize(path, recursive: true);
	}

	public async Task<FileOrganizationResult> RefreshTvSeasonBundleMetadata(FileOrganizationResult current, TvFileOrganizationOptions options, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(current);
		ArgumentNullException.ThrowIfNull(options);
		var organizer = new EpisodeFileOrganizer(_organizationService, _fileSystem, _loggerFactory.CreateLogger<EpisodeFileOrganizer>(), _libraryManager, _libraryMonitor, _providerManager, _namingOptions);
		string[] directories = current.BundleItems
			.Select(item => Path.GetDirectoryName(item.SourcePath))
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Cast<string>()
			.Distinct(PathSafety.PathComparer)
			.ToArray();
		if (directories.Length == 0 && _fileSystem.DirectoryExists(current.OriginalPath))
		{
			directories = new[] { current.OriginalPath };
		}

		var detectedResults = new List<FileOrganizationResult>();
		foreach (string directory in directories)
		{
			cancellationToken.ThrowIfCancellationRequested();
			FileOrganizationResult? detected = await organizer
				.DetectSeasonDirectory(directory, GetFilesToOrganize(directory, recursive: false), options, cancellationToken)
				.ConfigureAwait(false);
			if (detected != null)
			{
				detectedResults.Add(detected);
			}
		}

		FileOrganizationResult refreshed = MergeSeasonBundles(detectedResults).FirstOrDefault()
			?? throw new OrganizationException("No matching TV season metadata was found.");
		refreshed.OriginalPath = current.OriginalPath;
		refreshed.OriginalFileName = current.OriginalFileName;
		_organizationService.SaveResult(refreshed, cancellationToken);
		return refreshed;
	}

	private async Task<FileOrganizationResult> OrganizeApprovedBundle(string rootPath, IReadOnlyList<FileOrganizationBundleItem> bundleItems, TvFileOrganizationOptions options, CancellationToken cancellationToken)
	{
		var result = new FileOrganizationResult
		{
			Date = DateTime.UtcNow,
			OriginalPath = rootPath,
			OriginalFileName = Path.GetFileName(Path.TrimEndingDirectorySeparator(rootPath)),
			Type = FileOrganizerType.Episode,
			BundleItems = bundleItems
		};
		List<FileOrganizationBundleItem> items = bundleItems
			.Where(item => !string.IsNullOrWhiteSpace(item.SourcePath) && !string.IsNullOrWhiteSpace(item.TargetPath))
			.DistinctBy(item => item.SourcePath, PathSafety.PathComparer)
			.ToList();
		if (items.Count == 0)
		{
			result.Status = FileSortingStatus.Failure;
			result.StatusMessage = "Approved bundle no longer contains any approved files.";
			_organizationService.SaveResult(result, cancellationToken);
			return result;
		}
		int succeeded = 0;
		int skipped = 0;
		int failed = 0;
		var movedSourcePaths = new List<string>();
		List<string> watchLocations = options.WatchLocations
			.Select(location => PathSafety.TryNormalize(location, out string root) ? root : null)
			.Where(root => root != null)
			.Cast<string>()
			.Distinct(PathSafety.PathComparer)
			.ToList();
		foreach (FileOrganizationBundleItem item in items)
		{
			cancellationToken.ThrowIfCancellationRequested();
			string? sourceRoot = watchLocations.FirstOrDefault(root => PathSafety.IsSameOrSubPath(root, item.SourcePath));
			if (sourceRoot == null || PathSafety.TraversesSymbolicLink(sourceRoot, item.SourcePath))
			{
				failed++;
				AddPluginLogLine($"TV bundle rejected unsafe source: {item.SourcePath}");
				continue;
			}
			try
			{
				PathSafety.EnsureWithinLibraryRoots(item.TargetPath, GetLibraryRoots());
				result.FileSize += _fileSystem.FileExists(item.SourcePath) ? _fileSystem.GetFileInfo(item.SourcePath).Length : 0;
				if (PathSafety.AreSame(item.SourcePath, item.TargetPath))
				{
					succeeded++;
					continue;
				}
				if (!options.OverwriteExistingEpisodes && _fileSystem.FileExists(item.TargetPath))
				{
					skipped++;
					continue;
				}
				_libraryMonitor.ReportFileSystemChangeBeginning(item.TargetPath);
				try
				{
					await SafeFileTransfer.TransferSingleAsync(item.SourcePath, item.TargetPath, options.CopyOriginalFile, options.OverwriteExistingEpisodes, cancellationToken).ConfigureAwait(false);
				}
				finally
				{
					_libraryMonitor.ReportFileSystemChangeComplete(item.TargetPath, refreshPath: true);
				}
				succeeded++;
				if (!options.CopyOriginalFile)
				{
					movedSourcePaths.Add(item.SourcePath);
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception exception)
			{
				failed++;
				AddPluginLogLine($"TV bundle item failed: {item.SourcePath} -> {item.TargetPath} {exception.Message}");
				_logger.LogError(exception, "Error organizing approved bundle item {SourcePath} to {TargetPath}", item.SourcePath, item.TargetPath);
			}
		}
		CleanApprovedSources(movedSourcePaths, options.WatchLocations, options.DeleteEmptyFolders, options.LeftOverFileExtensionsToDelete, "TV", cancellationToken);
		result.TargetPath = GetMergedBundleItemTargetPath(items);
		result.Status = failed == 0 ? FileSortingStatus.Success : FileSortingStatus.Failure;
		result.StatusMessage = failed == 0
			? $"Approved bundle. Organized {succeeded}, skipped {skipped}."
			: $"Approved bundle with {failed} failure(s). Organized {succeeded}, skipped {skipped}.";
		_organizationService.SaveResult(result, cancellationToken);
		return result;
	}

	public void CleanApprovedSources(IEnumerable<string> sourcePaths, IEnumerable<string>? configuredWatchLocations, bool deleteEmptyFolders, IEnumerable<string>? configuredDeleteExtensions, string mediaType, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(sourcePaths);
		List<string> deleteExtensions = GetDeleteExtensions(configuredDeleteExtensions);
		if (!deleteEmptyFolders && deleteExtensions.Count == 0)
		{
			return;
		}
		List<string> watchLocations = (configuredWatchLocations ?? Array.Empty<string>())
			.Select(location => PathSafety.TryNormalize(location, out string root) ? root : null)
			.Where(root => root != null)
			.Cast<string>()
			.Distinct(PathSafety.PathComparer)
			.ToList();
		List<string> sourceFolders = sourcePaths
			.Select(Path.GetDirectoryName)
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Cast<string>()
			.Distinct(PathSafety.PathComparer)
			.ToList();
		Clean(sourceFolders, watchLocations, deleteEmptyFolders, deleteExtensions, mediaType, cancellationToken);
	}

	private static IReadOnlyList<FileOrganizationResult> MergeSeasonBundles(IReadOnlyList<FileOrganizationResult> results)
	{
		var mergedIds = new HashSet<string>(PathSafety.PathComparer);
		var mergedResults = new List<FileOrganizationResult>();
		foreach (var group in results.GroupBy(GetShowBundleKey, StringComparer.OrdinalIgnoreCase).Where(group => group.Key != null))
		{
			List<FileOrganizationResult> seasons = group.OrderBy(result => result.ExtractedSeasonNumber).ToList();
			if (seasons.Select(result => result.ExtractedSeasonNumber).Where(season => season.HasValue).Distinct().Count() <= 1)
			{
				continue;
			}
			FileOrganizationResult first = seasons[0];
			string sourceRoot = Path.GetDirectoryName(first.OriginalPath) ?? first.OriginalPath;
			string? targetRoot = GetMergedTargetPath(seasons);
			var sourcePaths = new HashSet<string>(PathSafety.PathComparer);
			List<FileOrganizationBundleItem> items = seasons
				.SelectMany(result => result.BundleItems)
				.Where(item => !string.IsNullOrWhiteSpace(item.SourcePath) && sourcePaths.Add(item.SourcePath))
				.OrderBy(item => item.SeasonNumber)
				.ThenBy(item => item.SourcePath, PathSafety.PathComparer)
				.ToList();
			mergedResults.Add(new FileOrganizationResult
			{
				Date = DateTime.UtcNow,
				OriginalPath = sourceRoot,
				OriginalFileName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceRoot)),
				ExtractedName = first.ExtractedName,
				ExtractedYear = first.ExtractedYear,
				TargetPath = targetRoot,
				Type = first.Type,
				FileSize = seasons.Sum(result => result.FileSize),
				BundleItems = items,
				Status = FileSortingStatus.Detected,
				StatusMessage = $"Detected {seasons.Count} season(s), {items.Count} bundled file(s) as {first.ExtractedName}. Waiting for approval."
			});
			foreach (FileOrganizationResult season in seasons)
			{
				mergedIds.Add(season.OriginalPath);
			}
		}
		mergedResults.AddRange(results.Where(result => !mergedIds.Contains(result.OriginalPath)));
		return mergedResults;
	}

	private static string? GetShowBundleKey(FileOrganizationResult result)
	{
		string? root = GetBundleTargetRoot(result) ?? Path.GetDirectoryName(result.OriginalPath);
		return string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(result.ExtractedName)
			? null
			: root + "\0" + result.ExtractedName + "\0" + result.ExtractedYear?.ToString(System.Globalization.CultureInfo.InvariantCulture);
	}

	private static string? GetBundleTargetRoot(FileOrganizationResult result)
	{
		if (string.IsNullOrWhiteSpace(result.TargetPath))
		{
			return null;
		}
		return result.ExtractedSeasonNumber.HasValue
			? Path.GetDirectoryName(result.TargetPath)
			: result.TargetPath;
	}

	private static string? GetMergedTargetPath(IReadOnlyList<FileOrganizationResult> seasons)
	{
		string[] targets = seasons
			.Select(result => result.TargetPath)
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Cast<string>()
			.Distinct(PathSafety.PathComparer)
			.ToArray();
		if (targets.Length == 1)
		{
			return targets[0];
		}
		string[] parents = targets
			.Select(path => Path.GetDirectoryName(path))
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Cast<string>()
			.Distinct(PathSafety.PathComparer)
			.ToArray();
		return parents.Length == 1 ? parents[0] : null;
	}

	private static string? GetMergedBundleItemTargetPath(IReadOnlyList<FileOrganizationBundleItem> items)
	{
		string[] parentFolders = items
			.Select(item => Path.GetDirectoryName(item.TargetPath))
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Cast<string>()
			.Distinct(PathSafety.PathComparer)
			.ToArray();
		if (parentFolders.Length == 1)
		{
			return parentFolders[0];
		}
		string[] seriesFolders = parentFolders
			.Select(Path.GetDirectoryName)
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Cast<string>()
			.Distinct(PathSafety.PathComparer)
			.ToArray();
		return seriesFolders.Length == 1 ? seriesFolders[0] : null;
	}

	private List<string> GetLibraryRoots()
	{
		return (from path in _libraryManager.GetVirtualFolders().SelectMany(folder => folder.Locations ?? Array.Empty<string>())
			where !string.IsNullOrWhiteSpace(path)
			select path).ToList();
	}

	private async Task DeleteBundledFileResults(FileOrganizationResult result, IReadOnlyList<FileOrganizationResult> detectedResults, CancellationToken cancellationToken)
	{
		foreach (string sourcePath in result.BundleItems.Select(item => item.SourcePath).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(PathSafety.PathComparer))
		{
			FileOrganizationResult? existing = _organizationService.GetResultBySourcePath(sourcePath);
			if (existing != null && !existing.IsInProgress)
			{
				await _organizationService.DeleteResult(existing.Id, cancellationToken).ConfigureAwait(false);
			}
		}
		var resultSources = new HashSet<string>(result.BundleItems.Select(item => item.SourcePath), PathSafety.PathComparer);
		foreach (string sourcePath in detectedResults
			.Where(item => !PathSafety.PathComparer.Equals(item.OriginalPath, result.OriginalPath) && item.BundleItems.Any(bundleItem => resultSources.Contains(bundleItem.SourcePath)))
			.Select(item => item.OriginalPath)
			.Distinct(PathSafety.PathComparer))
		{
			FileOrganizationResult? existing = _organizationService.GetResultBySourcePath(sourcePath);
			if (existing != null && !existing.IsInProgress)
			{
				await _organizationService.DeleteResult(existing.Id, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	private List<FileSystemMetadata> GetFilesToOrganize(string path, bool recursive)
	{
		try
		{
			return _fileSystem.GetFiles(path, recursive)
				.Where(file => !PathSafety.TraversesSymbolicLink(path, file.FullName))
				.ToList();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_logger.LogError(exception, "Error getting files from {Path}", path);
			return new List<FileSystemMetadata>();
		}
	}

	private void Clean(IEnumerable<string> paths, List<string> watchLocations, bool deleteEmptyFolders, List<string> deleteExtensions, string mediaType, CancellationToken cancellationToken)
	{
		foreach (string path in paths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!watchLocations.Any(root => PathSafety.IsSameOrSubPath(root, path)))
			{
				_logger.LogWarning("Refusing to clean path outside configured {MediaType} watch folders: {Path}", mediaType, path);
				continue;
			}
			if (deleteExtensions.Count > 0)
			{
				DeleteLeftOverFiles(path, deleteExtensions, watchLocations, cancellationToken);
			}
			if (deleteEmptyFolders)
			{
				DeleteEmptyFolders(path, watchLocations, mediaType, cancellationToken);
			}
		}
	}

	private void DeleteLeftOverFiles(string path, IEnumerable<string> extensions, List<string> watchLocations, CancellationToken cancellationToken)
	{
		List<string> files;
		try
		{
			files = _fileSystem.GetFilePaths(path, extensions.ToArray(), enableCaseSensitiveExtensions: false, recursive: true).ToList();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_logger.LogError(exception, "Error enumerating leftover files in {Path}", path);
			return;
		}
		foreach (string file in files)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				string? root = watchLocations.FirstOrDefault(candidate => PathSafety.IsSameOrSubPath(candidate, file));
				if (root != null && !PathSafety.TraversesSymbolicLink(root, file))
				{
					_fileSystem.DeleteFile(file);
				}
				else
				{
					_logger.LogWarning("Refusing to delete leftover file through a symbolic link: {Path}", file);
				}
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Error deleting file {Path}", file);
			}
		}
	}

	private void DeleteEmptyFolders(string path, List<string> watchLocations, string mediaType, CancellationToken cancellationToken)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			foreach (string directory in _fileSystem.GetDirectoryPaths(path))
			{
				string? root = watchLocations.FirstOrDefault(candidate => PathSafety.IsSameOrSubPath(candidate, directory));
				if (root != null && !PathSafety.TraversesSymbolicLink(root, directory))
				{
					DeleteEmptyFolders(directory, watchLocations, mediaType, cancellationToken);
				}
				else
				{
					_logger.LogWarning("Refusing to clean directory through a symbolic link: {Path}", directory);
				}
			}
			if (!_fileSystem.GetFileSystemEntryPaths(path).Any() && !watchLocations.Contains(path, PathSafety.PathComparer))
			{
				_logger.LogDebug("Deleting empty {MediaType} directory {Directory}", mediaType, path);
				Directory.Delete(path, recursive: false);
			}
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
		{
			_logger.LogError(exception, "Failed to delete empty {MediaType} directory {Directory}", mediaType, path);
		}
	}
}
