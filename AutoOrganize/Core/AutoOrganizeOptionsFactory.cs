using System.Collections.Generic;
using AutoOrganize.Model;
using MediaBrowser.Common.Configuration;

namespace AutoOrganize.Core;

public class AutoOrganizeOptionsFactory : IConfigurationFactory
{
	public IEnumerable<ConfigurationStore> GetConfigurations()
	{
		return new List<ConfigurationStore>
		{
			new ConfigurationStore
			{
				Key = "autoorganize",
				ConfigurationType = typeof(AutoOrganizeOptions)
			}
		};
	}
}
