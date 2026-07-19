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
		AutoOrganizeOptions autoOrganizeOptions = options;
		TvFileOrganizationOptions tvFileOrganizationOptions;
		if (autoOrganizeOptions.TvOptions == null)
		{
			tvFileOrganizationOptions = (autoOrganizeOptions.TvOptions = new TvFileOrganizationOptions());
		}
		autoOrganizeOptions = options;
		MovieFileOrganizationOptions movieFileOrganizationOptions;
		if (autoOrganizeOptions.MovieOptions == null)
		{
			movieFileOrganizationOptions = (autoOrganizeOptions.MovieOptions = new MovieFileOrganizationOptions());
		}
		autoOrganizeOptions = options;
		if (autoOrganizeOptions.SmartMatchInfos == null)
		{
			List<SmartMatchInfo> list = (autoOrganizeOptions.SmartMatchInfos = new List<SmartMatchInfo>());
		}
		tvFileOrganizationOptions = options.TvOptions;
		if (tvFileOrganizationOptions.WatchLocations == null)
		{
			List<string> list3 = (tvFileOrganizationOptions.WatchLocations = new List<string>());
		}
		tvFileOrganizationOptions = options.TvOptions;
		if (tvFileOrganizationOptions.LeftOverFileExtensionsToDelete == null)
		{
			List<string> list3 = (tvFileOrganizationOptions.LeftOverFileExtensionsToDelete = new List<string>());
		}
		tvFileOrganizationOptions = options.TvOptions;
		if (tvFileOrganizationOptions.EpisodeNamePattern == null)
		{
			string text = (tvFileOrganizationOptions.EpisodeNamePattern = "%sn - %sx%0e - %en.%ext");
		}
		tvFileOrganizationOptions = options.TvOptions;
		if (tvFileOrganizationOptions.MultiEpisodeNamePattern == null)
		{
			string text = (tvFileOrganizationOptions.MultiEpisodeNamePattern = "%sn - %sx%0e-x%0ed - %en.%ext");
		}
		tvFileOrganizationOptions = options.TvOptions;
		if (tvFileOrganizationOptions.SeasonFolderPattern == null)
		{
			string text = (tvFileOrganizationOptions.SeasonFolderPattern = "Season %s");
		}
		tvFileOrganizationOptions = options.TvOptions;
		if (tvFileOrganizationOptions.SeasonZeroFolderName == null)
		{
			string text = (tvFileOrganizationOptions.SeasonZeroFolderName = "Season 0");
		}
		tvFileOrganizationOptions = options.TvOptions;
		if (tvFileOrganizationOptions.SeriesFolderPattern == null)
		{
			string text = (tvFileOrganizationOptions.SeriesFolderPattern = "%fn");
		}
		movieFileOrganizationOptions = options.MovieOptions;
		if (movieFileOrganizationOptions.WatchLocations == null)
		{
			List<string> list3 = (movieFileOrganizationOptions.WatchLocations = new List<string>());
		}
		movieFileOrganizationOptions = options.MovieOptions;
		if (movieFileOrganizationOptions.LeftOverFileExtensionsToDelete == null)
		{
			List<string> list3 = (movieFileOrganizationOptions.LeftOverFileExtensionsToDelete = new List<string>());
		}
		movieFileOrganizationOptions = options.MovieOptions;
		if (movieFileOrganizationOptions.MoviePattern == null)
		{
			string text = (movieFileOrganizationOptions.MoviePattern = "%fn.%ext");
		}
		movieFileOrganizationOptions = options.MovieOptions;
		if (movieFileOrganizationOptions.MovieFolderPattern == null)
		{
			string text = (movieFileOrganizationOptions.MovieFolderPattern = "%mn (%my)");
		}
		return options;
	}
}

#pragma warning restore CS0618
