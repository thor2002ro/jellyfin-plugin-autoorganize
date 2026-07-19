using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace AutoOrganize.Core;

internal static class LibraryProviderResolver
{
	public static IReadOnlyList<string> GetMetadataProviders(ILibraryManager libraryManager, string? requestedRoot, string itemType, IEnumerable<string> fallbackProviders)
	{
		ArgumentNullException.ThrowIfNull(libraryManager);
		return GetMetadataProviders(libraryManager.GetVirtualFolders(), requestedRoot, itemType, fallbackProviders);
	}

	internal static IReadOnlyList<string> GetMetadataProviders(IEnumerable<VirtualFolderInfo> folders, string? requestedRoot, string itemType, IEnumerable<string> fallbackProviders)
	{
		ArgumentNullException.ThrowIfNull(folders);
		ArgumentException.ThrowIfNullOrWhiteSpace(itemType);
		List<VirtualFolderInfo> folderList = folders.ToList();
		List<string> roots = folderList
			.SelectMany(folder => folder.Locations ?? Array.Empty<string>())
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.ToList();
		string root = PathSafety.GetAuthorizedLibraryRoot(requestedRoot, roots);
		VirtualFolderInfo? folder = folderList.FirstOrDefault(candidate => (candidate.Locations ?? Array.Empty<string>())
			.Any(location => PathSafety.PathsOverlap(location, root)));
		LibraryOptions? options = folder?.LibraryOptions;
		TypeOptions? typeOptions = options?.TypeOptions?.FirstOrDefault(type =>
			string.Equals(type.Type, itemType, StringComparison.OrdinalIgnoreCase));
		if (typeOptions?.MetadataFetchers is { Length: 0 })
		{
			return Array.Empty<string>();
		}

		string[] enabled = typeOptions?.MetadataFetchers ?? Array.Empty<string>();
		string[] order = typeOptions?.MetadataFetcherOrder ?? Array.Empty<string>();
		List<string> providers = order
			.Where(provider => enabled.Contains(provider, StringComparer.OrdinalIgnoreCase))
			.Concat(enabled.Where(provider => !order.Contains(provider, StringComparer.OrdinalIgnoreCase)))
			.Where(provider => !string.IsNullOrWhiteSpace(provider))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		if (providers.Count > 0)
		{
			return providers;
		}

		return fallbackProviders
			.Where(provider => !string.IsNullOrWhiteSpace(provider))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
	}
}
