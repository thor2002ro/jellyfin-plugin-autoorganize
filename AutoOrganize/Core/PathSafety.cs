using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AutoOrganize.Core;

internal static class PathSafety
{
	public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	public static StringComparison PathComparison { get; } = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

	public static string Normalize(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path, "path");
		if (!Path.IsPathFullyQualified(path))
		{
			throw new ArgumentException("The path must be fully qualified.", "path");
		}
		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
	}

	public static bool TryNormalize(string? path, out string normalizedPath)
	{
		normalizedPath = string.Empty;
		if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
		{
			return false;
		}
		try
		{
			normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
			return true;
		}
		catch (Exception ex) when (((ex is ArgumentException || ex is IOException || ex is NotSupportedException) ? 1 : 0) != 0)
		{
			return false;
		}
	}

	public static bool AreSame(string first, string second)
	{
		return PathComparer.Equals(Normalize(first), Normalize(second));
	}

	public static bool IsSameOrSubPath(string parentPath, string candidatePath)
	{
		if (!TryNormalize(parentPath, out string normalizedPath) || !TryNormalize(candidatePath, out string normalizedPath2))
		{
			return false;
		}
		return IsSameOrSubPathNormalized(normalizedPath, normalizedPath2);
	}

	public static bool PathsOverlap(string first, string second)
	{
		if (!IsSameOrSubPath(first, second))
		{
			return IsSameOrSubPath(second, first);
		}
		return true;
	}

	public static string GetAuthorizedLibraryRoot(string? requestedRoot, IEnumerable<string> configuredRoots)
	{
		ArgumentNullException.ThrowIfNull(configuredRoots, "configuredRoots");
		if (!TryNormalize(requestedRoot, out string normalizedPath))
		{
			throw new OrganizationException("Target folder '" + requestedRoot + "' is not a valid absolute path.");
		}
		foreach (string configuredRoot in configuredRoots)
		{
			if (TryNormalize(configuredRoot, out string normalizedPath2) && PathComparer.Equals(normalizedPath2, normalizedPath))
			{
				return normalizedPath2;
			}
		}
		throw new OrganizationException("Target folder '" + requestedRoot + "' is not a configured Jellyfin library location.");
	}

	public static void EnsureWithinLibraryRoots(string targetPath, IEnumerable<string> configuredRoots)
	{
		if (!IsSafelyWithinAnyRoot(targetPath, configuredRoots))
		{
			throw new OrganizationException("Target path '" + targetPath + "' is outside the configured Jellyfin library locations or traverses a symbolic link.");
		}
	}

	public static bool IsWithinAnyRoot(string path, IEnumerable<string> roots)
	{
		ArgumentNullException.ThrowIfNull(roots, "roots");
		if (!TryNormalize(path, out string candidate))
		{
			return false;
		}
		string normalizedPath;
		return roots.Any((string root) => TryNormalize(root, out normalizedPath) && IsSameOrSubPathNormalized(normalizedPath, candidate));
	}

	public static bool IsSafelyWithinAnyRoot(string path, IEnumerable<string> roots)
	{
		ArgumentNullException.ThrowIfNull(roots, "roots");
		if (!TryNormalize(path, out string normalizedPath))
		{
			return false;
		}
		foreach (string root in roots)
		{
			if (TryNormalize(root, out string normalizedPath2) && IsSameOrSubPathNormalized(normalizedPath2, normalizedPath) && !TraversesSymbolicLink(normalizedPath2, normalizedPath))
			{
				return true;
			}
		}
		return false;
	}

	public static bool TraversesSymbolicLink(string rootPath, string candidatePath)
	{
		if (!TryNormalize(rootPath, out string normalizedPath) || !TryNormalize(candidatePath, out string normalizedPath2) || !IsSameOrSubPathNormalized(normalizedPath, normalizedPath2))
		{
			return true;
		}
		string relativePath = Path.GetRelativePath(normalizedPath, normalizedPath2);
		if (relativePath == ".")
		{
			return false;
		}
		string text = normalizedPath;
		string[] array = relativePath.Split(new char[2]
		{
			Path.DirectorySeparatorChar,
			Path.AltDirectorySeparatorChar
		}, StringSplitOptions.RemoveEmptyEntries);
		foreach (string path in array)
		{
			text = Path.Combine(text, path);
			try
			{
				if ((File.GetAttributes(text) & FileAttributes.ReparsePoint) != FileAttributes.None)
				{
					return true;
				}
			}
			catch (Exception ex) when (((ex is FileNotFoundException || ex is DirectoryNotFoundException) ? 1 : 0) != 0)
			{
				return false;
			}
			catch (Exception ex2) when (((ex2 is UnauthorizedAccessException || ex2 is IOException) ? 1 : 0) != 0)
			{
				return true;
			}
		}
		return false;
	}

	private static bool IsSameOrSubPathNormalized(string parent, string candidate)
	{
		if (PathComparer.Equals(parent, candidate))
		{
			return true;
		}
		string value = (Path.EndsInDirectorySeparator(parent) ? parent : (parent + Path.DirectorySeparatorChar));
		return candidate.StartsWith(value, PathComparison);
	}
}
