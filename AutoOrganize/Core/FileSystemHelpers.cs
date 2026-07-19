using System;
using System.IO;

namespace AutoOrganize.Core;

internal static class FileSystemHelpers
{
	public static bool IsFileReady(string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			return false;
		}
		try
		{
			using FileStream fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.RandomAccess);
			return fileStream.Length >= 0;
		}
		catch (Exception ex) when (((ex is IOException || ex is UnauthorizedAccessException) ? 1 : 0) != 0)
		{
			return false;
		}
	}
}
