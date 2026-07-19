using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoOrganize.Model;
using Emby.Naming.Common;
using Emby.Naming.Video;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace AutoOrganize.Core;

public class MovieFileOrganizer
{
	private static readonly object MovieCreationLock = new object();

	private readonly ILibraryMonitor _libraryMonitor;

	private readonly ILibraryManager _libraryManager;

	private readonly ILogger<MovieFileOrganizer> _logger;

	private readonly IFileSystem _fileSystem;

	private readonly IFileOrganizationService _organizationService;

	private readonly IProviderManager _providerManager;

	private readonly NamingOptions _namingOptions;

	private FileOrganizerType CurrentFileOrganizerType => FileOrganizerType.Movie;

	public MovieFileOrganizer(IFileOrganizationService organizationService, IFileSystem fileSystem, ILogger<MovieFileOrganizer> logger, ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, IProviderManager providerManager, NamingOptions namingOptions)
	{
		_organizationService = organizationService;
		_fileSystem = fileSystem;
		_logger = logger;
		_libraryManager = libraryManager;
		_libraryMonitor = libraryMonitor;
		_providerManager = providerManager;
		_namingOptions = namingOptions;
	}

	public async Task<FileOrganizationResult> OrganizeMovieFile(string path, MovieFileOrganizationOptions options, bool overwriteExisting, CancellationToken cancellationToken)
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
			FileSize = _fileSystem.GetFileInfo(path).Length
		};
		try
		{
			if (!FileSystemHelpers.IsFileReady(path))
			{
				result.Status = FileSortingStatus.Failure;
				result.StatusMessage = "Path is locked by other processes. Please try again later.";
				_logger.LogInformation("Auto-organize Path is locked by other processes. Please try again later.");
				_organizationService.SaveResult(result, cancellationToken);
				return result;
			}
			VideoFileInfo? videoFileInfo = VideoResolver.Resolve(path, isDirectory: false, _namingOptions);
			if (!string.IsNullOrEmpty(videoFileInfo?.Name))
			{
				int? year = videoFileInfo.Year;
				_logger.LogDebug("Extracted information from {Path}. Movie {MovieName}, Year {MovieYear}", path, videoFileInfo.Name, year);
				await OrganizeMovie(path, videoFileInfo.Name, year, options, overwriteExisting, result, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			}
			else
			{
				string statusMessage = "Unable to determine movie name from " + path;
				result.Status = FileSortingStatus.Failure;
				result.StatusMessage = statusMessage;
				_logger.LogWarning("Unable to determine movie name from {Path}", path);
			}
			FileOrganizationResult? resultBySourcePath = _organizationService.GetResultBySourcePath(path);
			if (resultBySourcePath != null && (result.Type == FileOrganizerType.Unknown || (resultBySourcePath.Status == result.Status && resultBySourcePath.StatusMessage == result.StatusMessage && result.Status != FileSortingStatus.Success)))
			{
				return resultBySourcePath;
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex2)
		{
			result.Status = FileSortingStatus.Failure;
			result.StatusMessage = ex2.Message;
			_logger.LogError(ex2, "Error organizing file {Path}", path);
		}
		_organizationService.SaveResult(result, cancellationToken);
		return result;
	}

	private Movie CreateNewMovie(MovieFileOrganizationRequest request, FileOrganizationResult result, MovieFileOrganizationOptions options)
	{
		ArgumentNullException.ThrowIfNull(request, "request");
		ArgumentException.ThrowIfNullOrWhiteSpace(request.NewMovieName, "request.NewMovieName");
		string authorizedLibraryRoot = GetAuthorizedLibraryRoot(request.TargetFolder);
		_logger.LogInformation(
			"Creating or locating movie {MovieName} in library root {TargetRoot}",
			request.NewMovieName,
			authorizedLibraryRoot);
		lock (MovieCreationLock)
		{
			Movie? movie = GetMatchingMovie(request.NewMovieName, request.NewMovieYear, authorizedLibraryRoot, result);
			if (movie == null)
			{
				movie = new Movie
				{
					Id = Guid.NewGuid(),
					Name = request.NewMovieName,
					ProductionYear = request.NewMovieYear,
					IsInMixedFolder = !options.MovieFolder,
					ProviderIds = (request.NewMovieProviderIds ?? new Dictionary<string, string>()).ToDictionary<KeyValuePair<string, string>, string, string>((KeyValuePair<string, string> x) => x.Key, (KeyValuePair<string, string> x) => x.Value)
				};
				string moviePath = GetMoviePath(result.OriginalPath, movie, options);
				if (string.IsNullOrEmpty(moviePath))
				{
					throw new OrganizationException("Unable to sort " + result.OriginalPath + " because target path could not be determined.");
				}
				movie.Path = Path.Combine(authorizedLibraryRoot, moviePath);
				PathSafety.EnsureWithinLibraryRoots(movie.Path, GetLibraryRoots());
			}
			return movie;
		}
	}

	public async Task<FileOrganizationResult> OrganizeWithCorrection(MovieFileOrganizationRequest request, MovieFileOrganizationOptions options, CancellationToken cancellationToken)
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
			Movie movie;
			if (OrganizationOptionResolver.ShouldCreateNewMovie(request))
			{
				movie = CreateNewMovie(request, result, options);
			}
			else
			{
				movie = (_libraryManager.GetItemById(request.MovieId!) as Movie)
					?? throw new OrganizationException($"Movie '{request.MovieId}' was not found.");
			}

			result.Type = CurrentFileOrganizerType;
			await OrganizeMovie(
				result.OriginalPath,
				movie,
				options,
				overwriteExisting: true,
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
			_logger.LogError(exception, "Error organizing corrected movie {OriginalPath}", result.OriginalPath);
			_organizationService.SaveResult(result, CancellationToken.None);
		}

		return result;
	}

	private async Task OrganizeMovie(string sourcePath, string movieName, int? movieYear, MovieFileOrganizationOptions options, bool overwriteExisting, FileOrganizationResult result, CancellationToken cancellationToken)
	{
		Movie? movie = GetMatchingMovie(movieName, movieYear, string.Empty, result);
		if (movie == null)
		{
			movie = await AutoDetectMovie(movieName, movieYear, result, options, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (movie == null)
			{
				string statusMessage = "Unable to find movie in library matching name " + movieName;
				result.Status = FileSortingStatus.Failure;
				result.StatusMessage = statusMessage;
				_logger.LogWarning("Unable to find movie in library matching name {MovieName}", movieName);
				return;
			}
		}
		result.Type = CurrentFileOrganizerType;
		await OrganizeMovie(sourcePath, movie, options, overwriteExisting, result, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private async Task OrganizeMovie(string sourcePath, Movie movie, MovieFileOrganizationOptions options, bool overwriteExisting, FileOrganizationResult result, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Sorting file {SourcePath} into movie {MoviePath}", sourcePath, movie.Path);
		bool flag = string.IsNullOrWhiteSpace(result.Id);
		if (flag)
		{
			_organizationService.SaveResult(result, cancellationToken);
		}
		if (!_organizationService.AddToInProgressList(result, flag))
		{
			throw new OrganizationException("File is currently processed otherwise. Please try again later.");
		}
		try
		{
			string path = movie.Path;
			if (string.IsNullOrWhiteSpace(path))
			{
				throw new OrganizationException("Unable to sort " + sourcePath + " because target path could not be determined.");
			}
			_logger.LogInformation("Sorting file {SourcePath} to new path {NewPath}", sourcePath, path);
			result.TargetPath = path;
			PathSafety.EnsureWithinLibraryRoots(result.TargetPath, GetLibraryRoots());
			bool flag2 = File.Exists(result.TargetPath);
			if (!overwriteExisting)
			{
				if (options.CopyOriginalFile && flag2 && IsSameMovie(sourcePath, path))
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
					_logger.LogInformation("File {SourcePath} already exists as {NewPath}, stopping organization", sourcePath, path);
					result.Status = FileSortingStatus.SkippedExisting;
					result.StatusMessage = statusMessage2;
					result.TargetPath = path;
					return;
				}
			}
			await PerformFileSortingAsync(options, overwriteExisting, result, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
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
			_logger.LogError(ex3, "Caught a generic exception while organizing {SourcePath}", sourcePath);
		}
		finally
		{
			_organizationService.RemoveFromInprogressList(result);
		}
	}

	private async Task PerformFileSortingAsync(MovieFileOrganizationOptions options, bool overwriteExisting, FileOrganizationResult result, CancellationToken cancellationToken)
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
			await SafeFileTransfer.TransferAsync(result.OriginalPath, targetPath, options.CopyOriginalFile, overwriteExisting, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			result.Status = FileSortingStatus.Success;
			result.StatusMessage = string.Empty;
		}
		finally
		{
			_libraryMonitor.ReportFileSystemChangeComplete(targetPath, refreshPath: true);
		}
	}

	private async Task<Movie?> AutoDetectMovie(string movieName, int? movieYear, FileOrganizationResult result, MovieFileOrganizationOptions options, CancellationToken cancellationToken)
	{
		if (!options.AutoDetectMovie)
		{
			return null;
		}

		ItemLookupInfo parsedName = _libraryManager.ParseName(movieName);
		int? yearInName = parsedName.Year ?? movieYear;
		string nameWithoutYear = string.IsNullOrWhiteSpace(parsedName.Name)
			? movieName
			: parsedName.Name;
		IReadOnlyList<RemoteSearchResult> searchResults = Array.Empty<RemoteSearchResult>();
		string successfulSearchName = nameWithoutYear;

		foreach (string searchName in NameUtils.GetRemoteSearchCandidates(nameWithoutYear))
		{
			var searchInfo = new MovieInfo
			{
				Name = searchName,
				Year = yearInName
			};
			var query = new RemoteSearchQuery<MovieInfo>
			{
				SearchInfo = searchInfo
			};
			searchResults = (await _providerManager
				.GetRemoteSearchResults<Movie, MovieInfo>(query, cancellationToken)
				.ConfigureAwait(false))
				.ToList();

			if (searchResults.Count > 0)
			{
				successfulSearchName = searchName;
				break;
			}
		}

		if (searchResults.Count == 0)
		{
			return null;
		}

		if (!string.Equals(successfulSearchName, nameWithoutYear, StringComparison.Ordinal))
		{
			_logger.LogDebug(
				"Remote movie search for {OriginalName} succeeded after retrying with normalized title {NormalizedName}",
				nameWithoutYear,
				successfulSearchName);
		}

		RemoteSearchResult? finalResult = NameUtils.SelectBestRemoteResult(searchResults, nameWithoutYear, yearInName);
		if (finalResult == null)
		{
			return null;
		}

		var request = new MovieFileOrganizationRequest
		{
			NewMovieName = finalResult.Name,
			NewMovieProviderIds = finalResult.ProviderIds,
			NewMovieYear = finalResult.ProductionYear,
			TargetFolder = options.DefaultMovieLibraryPath
		};
		return CreateNewMovie(request, result, options);
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

	private Movie? GetMatchingMovie(string movieName, int? movieYear, string? targetFolder, FileOrganizationResult result)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(movieName, "movieName");
		ArgumentNullException.ThrowIfNull(result, "result");
		ItemLookupInfo itemLookupInfo = _libraryManager.ParseName(movieName);
		int? yearInName = itemLookupInfo.Year;
		string nameWithoutYear = itemLookupInfo.Name;
		if (string.IsNullOrWhiteSpace(nameWithoutYear))
		{
			nameWithoutYear = movieName;
		}
		if (!yearInName.HasValue)
		{
			yearInName = movieYear;
		}
		result.ExtractedName = nameWithoutYear;
		result.ExtractedYear = yearInName;
		return (from Movie i in _libraryManager.GetItemList(new InternalItemsQuery
			{
				IncludeItemTypes = new BaseItemKind[1] { BaseItemKind.Movie },
				Recursive = true,
				DtoOptions = new DtoOptions(allFields: true)
			})
			select NameUtils.GetMatchScore(nameWithoutYear, yearInName, i) into i
			where i.Item2 > 0
			orderby i.Item2 descending
			select i.Item1).FirstOrDefault((Movie m) => IsInRequestedTargetFolder(m.Path, targetFolder) && string.Equals(Path.GetExtension(m.Path), Path.GetExtension(result.OriginalPath), StringComparison.OrdinalIgnoreCase));
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

	private string GetMoviePath(string sourcePath, Movie movie, MovieFileOrganizationOptions options)
	{
		string path = string.Empty;
		if (options.MovieFolder)
		{
			path = Path.Combine(path, GetMovieFolder(sourcePath, movie, options));
		}
		path = Path.Combine(path, GetMovieFileName(sourcePath, movie, options));
		if (string.IsNullOrEmpty(path))
		{
			return string.Empty;
		}
		return path;
	}

	private string GetMovieFileName(string sourcePath, Movie movie, MovieFileOrganizationOptions options)
	{
		return GetMovieNameInternal(sourcePath, movie, OrganizationOptionResolver.GetMoviePattern(options));
	}

	private string GetMovieFolder(string sourcePath, Movie movie, MovieFileOrganizationOptions options)
	{
		return GetMovieNameInternal(sourcePath, movie, options.MovieFolderPattern);
	}

	private string GetMovieNameInternal(string sourcePath, Movie movie, string pattern)
	{
		if (string.IsNullOrWhiteSpace(movie.Name))
		{
			throw new OrganizationException("The selected movie does not have a valid name.");
		}

		if (string.IsNullOrWhiteSpace(pattern))
		{
			throw new OrganizationException("The configured movie name pattern is empty.");
		}

		string rawName = _fileSystem.GetValidFilename(movie.Name).Trim();
		int? productionYear = movie.ProductionYear;
		bool patternContainsYear = pattern.Contains("%my", StringComparison.Ordinal);
		string tokenName = patternContainsYear
			? NameUtils.RemoveTerminalYear(rawName, productionYear)
			: rawName;
		string extension = (Path.GetExtension(sourcePath) ?? string.Empty).TrimStart('.');
		string filename = pattern
			.Replace("%mn", tokenName, StringComparison.Ordinal)
			.Replace("%m.n", tokenName.Replace(' ', '.'), StringComparison.Ordinal)
			.Replace("%m_n", tokenName.Replace(' ', '_'), StringComparison.Ordinal)
			.Replace("%my", productionYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.Ordinal)
			.Replace("%ext", extension, StringComparison.Ordinal)
			.Replace("%fn", Path.GetFileNameWithoutExtension(sourcePath), StringComparison.Ordinal);
		return _fileSystem.GetValidFilename(filename).Trim();
	}

	private bool IsSameMovie(string sourcePath, string newPath)
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
