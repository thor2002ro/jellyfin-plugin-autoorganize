using System;
using System.Threading;
using System.Threading.Tasks;
using AutoOrganize.Model;
using Emby.Naming.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace AutoOrganize.Core;

public class TvFolderOrganizer
{
	private readonly FolderOrganizer _organizer;

	public TvFolderOrganizer(ILibraryManager libraryManager, ILoggerFactory loggerFactory, IFileSystem fileSystem, ILibraryMonitor libraryMonitor, IFileOrganizationService organizationService, IProviderManager providerManager, NamingOptions namingOptions)
	{
		_organizer = new FolderOrganizer(libraryManager, loggerFactory, fileSystem, libraryMonitor, organizationService, providerManager, namingOptions);
	}

	public Task Organize(TvFileOrganizationOptions options, IProgress<double> progress, CancellationToken cancellationToken)
	{
		return _organizer.Organize(options, progress, cancellationToken);
	}
}
