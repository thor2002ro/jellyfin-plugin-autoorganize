using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AutoOrganize.Model;
using MediaBrowser.Common.Configuration;

namespace AutoOrganize.Core;

#pragma warning disable CS0618 // Required to migrate legacy smart-match configuration.

public static class ConfigurationExtensions
{
	public const string AutoOrganizeOptionsKey = "autoorganize";

	public static void ConvertSmartMatchInfo(this IConfigurationManager manager, IFileOrganizationService service)
	{
		ArgumentNullException.ThrowIfNull(manager, "manager");
		ArgumentNullException.ThrowIfNull(service, "service");
		AutoOrganizeOptions autoOrganizeOptions = manager.GetAutoOrganizeOptions();
		if (autoOrganizeOptions.Converted)
		{
			return;
		}
		List<SmartMatchResult> list = service.GetSmartMatchInfos().Items.ToList();
		foreach (SmartMatchInfo legacyMatch in autoOrganizeOptions.SmartMatchInfos ?? new List<SmartMatchInfo>())
		{
			if (legacyMatch == null || string.IsNullOrWhiteSpace(legacyMatch.ItemName))
			{
				continue;
			}
			SmartMatchResult? smartMatchResult = list.FirstOrDefault((SmartMatchResult match) => match.OrganizerType == legacyMatch.OrganizerType && string.Equals(match.ItemName, legacyMatch.ItemName, StringComparison.OrdinalIgnoreCase));
			if (smartMatchResult == null)
			{
				smartMatchResult = new SmartMatchResult
				{
					DisplayName = (string.IsNullOrWhiteSpace(legacyMatch.DisplayName) ? legacyMatch.ItemName : legacyMatch.DisplayName),
					ItemName = legacyMatch.ItemName,
					OrganizerType = legacyMatch.OrganizerType
				};
				list.Add(smartMatchResult);
			}
			foreach (string item in legacyMatch.MatchStrings ?? new List<string>())
			{
				if (!string.IsNullOrWhiteSpace(item) && !smartMatchResult.MatchStrings.Contains<string>(item, StringComparer.OrdinalIgnoreCase))
				{
					smartMatchResult.MatchStrings.Add(item);
				}
			}
			service.SaveResult(smartMatchResult, CancellationToken.None);
		}
		autoOrganizeOptions.Converted = true;
		manager.SaveAutoOrganizeOptions(autoOrganizeOptions);
	}

	public static AutoOrganizeOptions GetAutoOrganizeOptions(this IConfigurationManager manager)
	{
		ArgumentNullException.ThrowIfNull(manager, "manager");
		return Normalize(manager.GetConfiguration<AutoOrganizeOptions>("autoorganize") ?? new AutoOrganizeOptions());
	}

	public static void SaveAutoOrganizeOptions(this IConfigurationManager manager, AutoOrganizeOptions options)
	{
		ArgumentNullException.ThrowIfNull(manager, "manager");
		ArgumentNullException.ThrowIfNull(options, "options");
		manager.SaveConfiguration("autoorganize", Normalize(options));
	}

	private static AutoOrganizeOptions Normalize(AutoOrganizeOptions options)
	{
		options.TvOptions ??= new TvFileOrganizationOptions();
		options.MovieOptions ??= new MovieFileOrganizationOptions();
		options.SmartMatchInfos ??= new List<SmartMatchInfo>();
		options.TvOptions.WatchLocations ??= new List<string>();
		options.TvOptions.LeftOverFileExtensionsToDelete ??= new List<string>();
		options.TvOptions.EpisodeNamePattern ??= "%sn - %sx%0e - %en.%ext";
		options.TvOptions.MultiEpisodeNamePattern ??= "%sn - %sx%0e-x%0ed - %en.%ext";
		options.TvOptions.SeasonFolderPattern ??= "Season %s";
		options.TvOptions.SeasonZeroFolderName ??= "Season 0";
		options.TvOptions.SeriesFolderPattern ??= "%fn";
		options.MovieOptions.WatchLocations ??= new List<string>();
		options.MovieOptions.LeftOverFileExtensionsToDelete ??= new List<string>();
		options.MovieOptions.MoviePattern ??= "%fn.%ext";
		options.MovieOptions.MovieFolderPattern ??= "%mn (%my)";
		return options;
	}
}

#pragma warning restore CS0618
