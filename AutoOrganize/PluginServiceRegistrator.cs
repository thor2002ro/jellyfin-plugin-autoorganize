using System;
using AutoOrganize.Core;
using AutoOrganize.Data;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace AutoOrganize;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
	public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
	{
		serviceCollection.AddSingleton((Func<IServiceProvider, IFileOrganizationRepository>)delegate(IServiceProvider serviceProvider)
		{
			SqliteFileOrganizationRepository sqliteFileOrganizationRepository = ActivatorUtilities.CreateInstance<SqliteFileOrganizationRepository>(serviceProvider, Array.Empty<object>());
			sqliteFileOrganizationRepository.Initialize();
			return sqliteFileOrganizationRepository;
		});
		serviceCollection.AddSingleton<IFileOrganizationService, FileOrganizationService>();
		serviceCollection.AddHostedService<AutoOrganizeInitializationService>();
	}
}
