using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoOrganize.Data;
using AutoOrganize.Model;
using Emby.Naming.Common;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace AutoOrganize.Core;

public class FileOrganizationService : IFileOrganizationService
{
	private readonly ITaskManager _taskManager;

	private readonly IFileOrganizationRepository _repo;

	private readonly ILoggerFactory _loggerFactory;

	private readonly ILogger<FileOrganizationService> _logger;

	private readonly ILibraryMonitor _libraryMonitor;

	private readonly ILibraryManager _libraryManager;

	private readonly IServerConfigurationManager _config;

	private readonly IFileSystem _fileSystem;

	private readonly IProviderManager _providerManager;

	private readonly ConcurrentDictionary<string, bool> _inProgressItemIds = new ConcurrentDictionary<string, bool>();

	private readonly NamingOptions _namingOptions;

	public FileOrganizationService(ITaskManager taskManager, IFileOrganizationRepository repo, ILoggerFactory loggerFactory, ILibraryMonitor libraryMonitor, ILibraryManager libraryManager, IServerConfigurationManager config, IFileSystem fileSystem, IProviderManager providerManager)
	{
		_taskManager = taskManager;
		_repo = repo;
		_loggerFactory = loggerFactory;
		_logger = loggerFactory.CreateLogger<FileOrganizationService>();
		_libraryMonitor = libraryMonitor;
		_libraryManager = libraryManager;
		_config = config;
		_fileSystem = fileSystem;
		_providerManager = providerManager;
		_namingOptions = new NamingOptions();
	}

	public void SaveResult(FileOrganizationResult result, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(result, "result");
		ArgumentException.ThrowIfNullOrWhiteSpace(result.OriginalPath, "result.OriginalPath");
		result.Id = result.OriginalPath.GetMD5().ToString("N", CultureInfo.InvariantCulture);
		_repo.SaveResult(result, cancellationToken);
	}

	public void SaveResult(SmartMatchResult result, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(result, "result");
		_repo.SaveResult(result, cancellationToken);
	}

	public void BeginProcessNewFiles()
	{
		_taskManager.CancelIfRunningAndQueue<OrganizerScheduledTask>();
	}

	public Task AddSmartMatchString(string itemName, string displayName, FileOrganizerType organizerType, string matchString, CancellationToken cancellationToken)
	{
		return _repo.AddSmartMatchString(itemName, displayName, organizerType, matchString, cancellationToken);
	}

	public QueryResult<FileOrganizationResult> GetResults(FileOrganizationResultQuery query)
	{
		ArgumentNullException.ThrowIfNull(query, "query");
		QueryResult<FileOrganizationResult> results = _repo.GetResults(query);
		foreach (FileOrganizationResult item in results.Items)
		{
			item.IsInProgress = _inProgressItemIds.ContainsKey(item.Id);
		}
		return results;
	}

	public FileOrganizationResult? GetResult(string id)
	{
		if (!Guid.TryParse(id, out var _))
		{
			return null;
		}
		FileOrganizationResult? result2 = _repo.GetResult(id);
		if (result2 != null)
		{
			result2.IsInProgress = _inProgressItemIds.ContainsKey(result2.Id);
		}
		return result2;
	}

	public FileOrganizationResult? GetResultBySourcePath(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path, "path");
		string id = path.GetMD5().ToString("N", CultureInfo.InvariantCulture);
		return GetResult(id);
	}

	public async Task DeleteOriginalFile(string resultId, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		FileOrganizationResult result = _repo.GetResult(resultId) ?? throw new OrganizationException("Organization result '" + resultId + "' was not found.");
		EnsureSourcePathIsAuthorized(result.OriginalPath);
		_logger.LogInformation("Requested to delete {OriginalPath}", result.OriginalPath);
		if (!AddToInProgressList(result, fullClientRefresh: false))
		{
			throw new OrganizationException("Path is currently processed otherwise. Please try again later.");
		}
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (_fileSystem.FileExists(result.OriginalPath))
			{
				_fileSystem.DeleteFile(result.OriginalPath);
			}
			await _repo.Delete(resultId, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			RemoveFromInprogressList(result);
		}
	}

	public async Task DeleteResult(string resultId, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (_repo.GetResult(resultId) == null)
		{
			throw new OrganizationException("Organization result '" + resultId + "' was not found.");
		}
		await _repo.Delete(resultId, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	public async Task PerformOrganization(string resultId, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		FileOrganizationResult fileOrganizationResult = _repo.GetResult(resultId) ?? throw new OrganizationException("Organization result '" + resultId + "' was not found.");
		EnsureSourcePathIsAuthorized(fileOrganizationResult.OriginalPath);
		AutoOrganizeOptions autoOrganizeOptions = _config.GetAutoOrganizeOptions();
		FileOrganizationResult fileOrganizationResult2 = fileOrganizationResult.Type switch
		{
			FileOrganizerType.Episode when SafeFileTransfer.IsSubtitleFile(fileOrganizationResult.OriginalPath) => await new SubtitleFileOrganizer(this, _fileSystem, _loggerFactory.CreateLogger<SubtitleFileOrganizer>(), _libraryManager, _libraryMonitor, _namingOptions).ApproveEpisodeSubtitleFile(fileOrganizationResult.OriginalPath, autoOrganizeOptions.TvOptions, cancellationToken).ConfigureAwait(continueOnCapturedContext: false),
			FileOrganizerType.Episode => await new EpisodeFileOrganizer(this, _fileSystem, _loggerFactory.CreateLogger<EpisodeFileOrganizer>(), _libraryManager, _libraryMonitor, _providerManager, _namingOptions).OrganizeEpisodeFile(fileOrganizationResult.OriginalPath, autoOrganizeOptions.TvOptions, cancellationToken).ConfigureAwait(continueOnCapturedContext: false),
			FileOrganizerType.Movie when SafeFileTransfer.IsSubtitleFile(fileOrganizationResult.OriginalPath) => await new SubtitleFileOrganizer(this, _fileSystem, _loggerFactory.CreateLogger<SubtitleFileOrganizer>(), _libraryManager, _libraryMonitor, _namingOptions).ApproveMovieSubtitleFile(fileOrganizationResult.OriginalPath, autoOrganizeOptions.MovieOptions, cancellationToken).ConfigureAwait(continueOnCapturedContext: false),
			FileOrganizerType.Movie => await new MovieFileOrganizer(this, _fileSystem, _loggerFactory.CreateLogger<MovieFileOrganizer>(), _libraryManager, _libraryMonitor, _providerManager, _namingOptions).OrganizeMovieFile(fileOrganizationResult.OriginalPath, autoOrganizeOptions.MovieOptions, autoOrganizeOptions.MovieOptions.OverwriteExistingFiles, cancellationToken).ConfigureAwait(continueOnCapturedContext: false),
			_ => throw new OrganizationException("No organizer exist for the type " + fileOrganizationResult.Type), 
		};
		if (fileOrganizationResult2.Status != FileSortingStatus.Success)
		{
			throw new OrganizationException(fileOrganizationResult2.StatusMessage ?? "The media file could not be organized.");
		}
		QueueLibraryScanIfNeeded(fileOrganizationResult.Type switch
		{
			FileOrganizerType.Episode => autoOrganizeOptions.TvOptions.QueueLibraryScan,
			FileOrganizerType.Movie => autoOrganizeOptions.MovieOptions.QueueLibraryScan,
			_ => false
		});
	}

	public async Task ClearLog(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await _repo.DeleteAll(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	public async Task ClearCompleted(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await _repo.DeleteCompleted(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	public async Task PerformOrganization(EpisodeFileOrganizationRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request, "request");
		EpisodeFileOrganizer episodeFileOrganizer = new EpisodeFileOrganizer(this, _fileSystem, _loggerFactory.CreateLogger<EpisodeFileOrganizer>(), _libraryManager, _libraryMonitor, _providerManager, _namingOptions);
		AutoOrganizeOptions autoOrganizeOptions = _config.GetAutoOrganizeOptions();
		FileOrganizationResult fileOrganizationResult = await episodeFileOrganizer.OrganizeWithCorrection(request, autoOrganizeOptions.TvOptions, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (fileOrganizationResult.Status != FileSortingStatus.Success)
		{
			throw new OrganizationException(fileOrganizationResult.StatusMessage ?? "The episode file could not be organized.");
		}
		QueueLibraryScanIfNeeded(autoOrganizeOptions.TvOptions.QueueLibraryScan);
	}

	public async Task PerformOrganization(MovieFileOrganizationRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request, "request");
		MovieFileOrganizer movieFileOrganizer = new MovieFileOrganizer(this, _fileSystem, _loggerFactory.CreateLogger<MovieFileOrganizer>(), _libraryManager, _libraryMonitor, _providerManager, _namingOptions);
		AutoOrganizeOptions autoOrganizeOptions = _config.GetAutoOrganizeOptions();
		FileOrganizationResult fileOrganizationResult = await movieFileOrganizer.OrganizeWithCorrection(request, autoOrganizeOptions.MovieOptions, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (fileOrganizationResult.Status != FileSortingStatus.Success)
		{
			throw new OrganizationException(fileOrganizationResult.StatusMessage ?? "The movie file could not be organized.");
		}
		QueueLibraryScanIfNeeded(autoOrganizeOptions.MovieOptions.QueueLibraryScan);
	}

	public QueryResult<SmartMatchResult> GetSmartMatchInfos(FileOrganizationResultQuery query)
	{
		return _repo.GetSmartMatch(query);
	}

	public QueryResult<SmartMatchResult> GetSmartMatchInfos()
	{
		return _repo.GetSmartMatch(new FileOrganizationResultQuery());
	}

	public Task DeleteSmartMatchEntries(IReadOnlyList<NameValuePair> entries, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(entries);
		return _repo.DeleteSmartMatchEntries(entries, cancellationToken);
	}

	public Task DeleteSmartMatchEntry(string id, string matchString, CancellationToken cancellationToken)
	{
		return _repo.DeleteSmartMatch(id, matchString, cancellationToken);
	}

	public bool AddToInProgressList(FileOrganizationResult result, bool fullClientRefresh)
	{
		ArgumentNullException.ThrowIfNull(result, "result");
		ArgumentException.ThrowIfNullOrWhiteSpace(result.OriginalPath, "result.OriginalPath");
		if (string.IsNullOrWhiteSpace(result.Id))
		{
			result.Id = result.OriginalPath.GetMD5().ToString("N", CultureInfo.InvariantCulture);
		}
		if (!_inProgressItemIds.TryAdd(result.Id, value: false))
		{
			return false;
		}
		result.IsInProgress = true;
		return true;
	}

	public bool RemoveFromInprogressList(FileOrganizationResult result)
	{
		ArgumentNullException.ThrowIfNull(result, "result");
		bool value;
		bool result2 = !string.IsNullOrEmpty(result.Id) && _inProgressItemIds.TryRemove(result.Id, out value);
		result.IsInProgress = false;
		return result2;
	}

	private void EnsureSourcePathIsAuthorized(string sourcePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath, "sourcePath");
		AutoOrganizeOptions autoOrganizeOptions = _config.GetAutoOrganizeOptions();
		List<string> list = new List<string>();
		IEnumerable<string>? enumerable = autoOrganizeOptions.TvOptions?.WatchLocations;
		list.AddRange(enumerable ?? Enumerable.Empty<string>());
		enumerable = autoOrganizeOptions.MovieOptions?.WatchLocations;
		list.AddRange(enumerable ?? Enumerable.Empty<string>());
		if (!PathSafety.IsSafelyWithinAnyRoot(sourcePath, list))
		{
			throw new OrganizationException("Source path '" + sourcePath + "' is outside the configured Auto Organize watch folders or traverses a symbolic link.");
		}
	}

	private void QueueLibraryScanIfNeeded(bool queueLibraryScan)
	{
		if (queueLibraryScan && !_libraryManager.IsScanRunning)
		{
			_libraryManager.QueueLibraryScan();
		}
	}
}
