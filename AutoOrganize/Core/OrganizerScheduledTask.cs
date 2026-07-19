using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoOrganize.Model;
using Emby.Naming.Common;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace AutoOrganize.Core;

public class OrganizerScheduledTask : IScheduledTask, IConfigurableScheduledTask
{
	private sealed class MappedProgress : IProgress<double>
	{
		private readonly IProgress<double> _inner;

		private readonly double _start;

		private readonly double _range;

		public MappedProgress(IProgress<double> inner, double start, double end)
		{
			_inner = inner;
			_start = start;
			_range = end - start;
		}

		public void Report(double value)
		{
			_inner.Report(_start + Math.Clamp(value, 0.0, 100.0) / 100.0 * _range);
		}
	}

	private readonly ILibraryMonitor _libraryMonitor;

	private readonly ILibraryManager _libraryManager;

	private readonly ILoggerFactory _loggerFactory;

	private readonly ILogger<OrganizerScheduledTask> _logger;

	private readonly IFileSystem _fileSystem;

	private readonly IServerConfigurationManager _config;

	private readonly IProviderManager _providerManager;

	private readonly NamingOptions _namingOptions;

	private readonly IFileOrganizationService _fileOrganizationService;

	public string Key => "AutoOrganize";

	public string Name => "Organize new media files";

	public string Description => "Processes new files available in the configured watch folder.";

	public string Category => "Library";

	public bool IsHidden => !IsEnabled;

	public bool IsEnabled
	{
		get
		{
			AutoOrganizeOptions autoOrganizeOptions = _config.GetAutoOrganizeOptions();
			if (!autoOrganizeOptions.TvOptions.IsEnabled)
			{
				return autoOrganizeOptions.MovieOptions.IsEnabled;
			}
			return true;
		}
	}

	public bool IsLogged => false;

	public OrganizerScheduledTask(ILibraryMonitor libraryMonitor, ILibraryManager libraryManager, ILoggerFactory loggerFactory, IFileSystem fileSystem, IServerConfigurationManager config, IProviderManager providerManager, IFileOrganizationService fileOrganizationService)
	{
		_libraryMonitor = libraryMonitor;
		_libraryManager = libraryManager;
		_loggerFactory = loggerFactory;
		_logger = loggerFactory.CreateLogger<OrganizerScheduledTask>();
		_fileSystem = fileSystem;
		_config = config;
		_providerManager = providerManager;
		_fileOrganizationService = fileOrganizationService;
		_namingOptions = new NamingOptions();
	}

	public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
	{
		bool queueTv = false;
		bool queueMovie = false;
		AutoOrganizeOptions options = _config.GetAutoOrganizeOptions();
		bool isEnabled = options.TvOptions.IsEnabled;
		bool organizeMovies = options.MovieOptions.IsEnabled;
		IProgress<double> progress2;
		if (!(isEnabled && organizeMovies))
		{
			progress2 = progress;
		}
		else
		{
			IProgress<double> progress3 = new MappedProgress(progress, 0.0, 50.0);
			progress2 = progress3;
		}
		IProgress<double> progress4 = progress2;
		IProgress<double> progress5;
		if (!(isEnabled && organizeMovies))
		{
			progress5 = progress;
		}
		else
		{
			IProgress<double> progress3 = new MappedProgress(progress, 50.0, 100.0);
			progress5 = progress3;
		}
		IProgress<double> movieProgress = progress5;
		if (isEnabled)
		{
			queueTv = options.TvOptions.QueueLibraryScan;
			await new TvFolderOrganizer(_libraryManager, _loggerFactory, _fileSystem, _libraryMonitor, _fileOrganizationService, _providerManager, _namingOptions).Organize(options.TvOptions, progress4, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		if (organizeMovies)
		{
			queueMovie = options.MovieOptions.QueueLibraryScan;
			await new MovieFolderOrganizer(_libraryManager, _loggerFactory, _fileSystem, _libraryMonitor, _fileOrganizationService, _providerManager, _namingOptions).Organize(options.MovieOptions, movieProgress, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		progress.Report(100.0);
		if ((queueTv || queueMovie) && !_libraryManager.IsScanRunning)
		{
			_libraryManager.QueueLibraryScan();
		}
	}

	public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
	{
		return new TaskTriggerInfo[1]
		{
			new TaskTriggerInfo
			{
				Type = TaskTriggerInfoType.IntervalTrigger,
				IntervalTicks = TimeSpan.FromMinutes(5L).Ticks
			}
		};
	}
}
