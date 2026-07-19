using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoOrganize.Core;

public sealed class AutoOrganizeInitializationService : IHostedService
{
	private readonly IServerConfigurationManager _configurationManager;

	private readonly IFileOrganizationService _fileOrganizationService;

	private readonly ILogger<AutoOrganizeInitializationService> _logger;

	public AutoOrganizeInitializationService(IServerConfigurationManager configurationManager, IFileOrganizationService fileOrganizationService, ILogger<AutoOrganizeInitializationService> logger)
	{
		_configurationManager = configurationManager;
		_fileOrganizationService = fileOrganizationService;
		_logger = logger;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		try
		{
			_configurationManager.ConvertSmartMatchInfo(_fileOrganizationService);
			_logger.LogInformation("Auto Organize initialization completed");
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Auto Organize could not migrate legacy smart-match configuration; the server will continue and migration will be retried next start");
		}
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		return Task.CompletedTask;
	}
}
