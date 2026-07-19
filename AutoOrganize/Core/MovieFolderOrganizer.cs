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

public class MovieFolderOrganizer
{
	private readonly ILibraryMonitor _libraryMonitor;

	private readonly ILibraryManager _libraryManager;

	private readonly ILoggerFactory _loggerFactory;

	private readonly ILogger<MovieFolderOrganizer> _logger;

	private readonly IFileSystem _fileSystem;

	private readonly IFileOrganizationService _organizationService;

	private readonly IProviderManager _providerManager;

	private readonly NamingOptions _namingOptions;

	public MovieFolderOrganizer(ILibraryManager libraryManager, ILoggerFactory loggerFactory, IFileSystem fileSystem, ILibraryMonitor libraryMonitor, IFileOrganizationService organizationService, IProviderManager providerManager, NamingOptions namingOptions)
	{
		_libraryManager = libraryManager;
		_loggerFactory = loggerFactory;
		_logger = loggerFactory.CreateLogger<MovieFolderOrganizer>();
		_fileSystem = fileSystem;
		_libraryMonitor = libraryMonitor;
		_organizationService = organizationService;
		_providerManager = providerManager;
		_namingOptions = namingOptions;
	}

	private bool CanOrganize(FileSystemMetadata fileInfo, MovieFileOrganizationOptions options)
	{
		checked
		{
			long num = unchecked((long)options.MinFileSizeMb) * 1024L * 1024;
			try
			{
				return VideoResolver.IsVideoFile(fileInfo.FullName, _namingOptions) && fileInfo.Length >= num;
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Error organizing file {FileName}", fileInfo.Name);
			}
			return false;
		}
	}

	private bool IsValidWatchLocation(string path, List<string> libraryFolderPaths)
	{
		if (!PathSafety.TryNormalize(path, out string normalizedPath) || !Directory.Exists(normalizedPath))
		{
			_logger.LogWarning("Movie watch folder {WatchFolder} is not a valid existing absolute path and will be skipped", path);
			return false;
		}
		if (IsPathAlreadyInMediaLibrary(normalizedPath, libraryFolderPaths))
		{
			_logger.LogWarning("Movie watch folder {WatchFolder} overlaps a Jellyfin library and will be skipped", path);
			return false;
		}
		return true;
	}

	private bool IsPathAlreadyInMediaLibrary(string path, List<string> libraryFolderPaths)
	{
		return libraryFolderPaths.Any((string libraryPath) => PathSafety.PathsOverlap(libraryPath, path));
	}

	public async Task Organize(MovieFileOrganizationOptions options, IProgress<double> progress, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(options, "options");
		ArgumentNullException.ThrowIfNull(progress, "progress");
		if (options.MinFileSizeMb < 0)
		{
			throw new ArgumentOutOfRangeException("options", "Minimum file size cannot be negative.");
		}
		List<string> libraryFolderPaths = (from path in _libraryManager.GetVirtualFolders().SelectMany((VirtualFolderInfo folder) => folder.Locations ?? Array.Empty<string>())
			where !string.IsNullOrWhiteSpace(path)
			select path).ToList();
		List<string> watchLocations = (options.WatchLocations ?? new List<string>()).Where((string i) => IsValidWatchLocation(i, libraryFolderPaths)).Select(PathSafety.Normalize).Distinct<string>(PathSafety.PathComparer)
			.ToList();
		List<FileSystemMetadata> eligibleFiles = (from i in watchLocations.SelectMany(GetFilesToOrganize).OrderBy(_fileSystem.GetCreationTimeUtc)
			where CanOrganize(i, options)
			select i).ToList();
		HashSet<string> processedFolders = new HashSet<string>(PathSafety.PathComparer);
		progress.Report(10.0);
		if (eligibleFiles.Count > 0)
		{
			int numComplete = 0;
			MovieFileOrganizer organizer = new MovieFileOrganizer(_organizationService, _fileSystem, _loggerFactory.CreateLogger<MovieFileOrganizer>(), _libraryManager, _libraryMonitor, _providerManager, _namingOptions);
			foreach (FileSystemMetadata file in eligibleFiles)
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					FileOrganizationResult obj = await organizer.OrganizeMovieFile(file.FullName, options, options.OverwriteExistingFiles, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
					string? directoryName = Path.GetDirectoryName(file.FullName);
					if (obj.Status == FileSortingStatus.Success && !string.IsNullOrEmpty(directoryName))
					{
						processedFolders.Add(directoryName);
					}
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception exception)
				{
					_logger.LogError(exception, "Error organizing movie {Path}", file.FullName);
				}
				numComplete++;
				double num = numComplete;
				num /= (double)eligibleFiles.Count;
				progress.Report(10.0 + 89.0 * num);
			}
		}
		cancellationToken.ThrowIfCancellationRequested();
		progress.Report(99.0);
		List<string> deleteExtensions = (from i in options.LeftOverFileExtensionsToDelete ?? new List<string>()
			where i != null
			select i.Trim().TrimStart('.') into i
			where !string.IsNullOrEmpty(i)
			select "." + i).ToList();
		Clean(processedFolders, watchLocations, options.DeleteEmptyFolders, deleteExtensions, cancellationToken);
		if (options.ExtendedClean)
		{
			Clean(watchLocations, watchLocations, options.DeleteEmptyFolders, deleteExtensions, cancellationToken);
		}
		progress.Report(100.0);
	}

	private void Clean(IEnumerable<string> paths, List<string> watchLocations, bool deleteEmptyFolders, List<string> deleteExtensions, CancellationToken cancellationToken)
	{
		foreach (string path in paths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!watchLocations.Any((string root) => PathSafety.IsSameOrSubPath(root, path)))
			{
				_logger.LogWarning("Refusing to clean path outside configured movie watch folders: {Path}", path);
				continue;
			}
			if (deleteExtensions.Count > 0)
			{
				DeleteLeftOverFiles(path, deleteExtensions, watchLocations, cancellationToken);
			}
			if (deleteEmptyFolders)
			{
				DeleteEmptyFolders(path, watchLocations, cancellationToken);
			}
		}
	}

	private List<FileSystemMetadata> GetFilesToOrganize(string path)
	{
		try
		{
			return (from file in _fileSystem.GetFiles(path, recursive: true)
				where !PathSafety.TraversesSymbolicLink(path, file.FullName)
				select file).ToList();
		}
		catch (IOException exception)
		{
			_logger.LogError(exception, "Error getting files from {Path}", path);
			return new List<FileSystemMetadata>();
		}
	}

	private void DeleteLeftOverFiles(string path, IEnumerable<string> extensions, List<string> watchLocations, CancellationToken cancellationToken)
	{
		List<string> list;
		try
		{
			list = _fileSystem.GetFilePaths(path, extensions.ToArray(), enableCaseSensitiveExtensions: false, recursive: true).ToList();
		}
		catch (Exception ex) when (((ex is IOException || ex is UnauthorizedAccessException) ? 1 : 0) != 0)
		{
			_logger.LogError(ex, "Error enumerating leftover files in {Path}", path);
			return;
		}
		foreach (string file in list)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				string? text = watchLocations.FirstOrDefault((string root) => PathSafety.IsSameOrSubPath(root, file));
				if (text != null && !PathSafety.TraversesSymbolicLink(text, file))
				{
					_fileSystem.DeleteFile(file);
					continue;
				}
				_logger.LogWarning("Refusing to delete leftover file through a symbolic link: {Path}", file);
			}
			catch (Exception exception)
			{
				_logger.LogError(exception, "Error deleting file {Path}", file);
			}
		}
	}

	private void DeleteEmptyFolders(string path, List<string> ignorePaths, CancellationToken cancellationToken)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			foreach (string d in _fileSystem.GetDirectoryPaths(path))
			{
				string? text = ignorePaths.FirstOrDefault((string root) => PathSafety.IsSameOrSubPath(root, d));
				if (text != null && !PathSafety.TraversesSymbolicLink(text, d))
				{
					DeleteEmptyFolders(d, ignorePaths, cancellationToken);
					continue;
				}
				_logger.LogWarning("Refusing to clean directory through a symbolic link: {Path}", d);
			}
			if (!_fileSystem.GetFileSystemEntryPaths(path).Any() && !IsWatchFolder(path, ignorePaths))
			{
				_logger.LogDebug("Deleting empty directory {Path}", path);
				Directory.Delete(path, recursive: false);
			}
		}
		catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
		{
			_logger.LogError(ex, "Failed to delete empty movie directory {Directory}", path);
		}
	}

	private bool IsWatchFolder(string path, IEnumerable<string> watchLocations)
	{
		return watchLocations.Contains<string>(path, PathSafety.PathComparer);
	}
}
