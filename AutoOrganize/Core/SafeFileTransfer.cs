using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lingua;

namespace AutoOrganize.Core;

internal static class SafeFileTransfer
{
	private const int CopyBufferSize = 131072;
	private static readonly HashSet<string> SubtitleExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".srt",
		".ass",
		".ssa",
		".sub",
		".idx",
		".vtt",
		".smi",
		".sami",
		".sup"
	};
	private static readonly HashSet<string> SubtitleSuffixFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"forced",
		"default",
		"sdh",
		"cc"
	};
	public static bool IsSubtitleFile(string path)
	{
		return SubtitleExtensions.Contains(Path.GetExtension(path));
	}

	public static string GetSubtitleTargetPath(string sourceSubtitlePath, string targetMediaPath, string? sourceMediaPath = null)
	{
		string? targetDirectory = Path.GetDirectoryName(targetMediaPath);
		if (string.IsNullOrWhiteSpace(targetDirectory))
		{
			throw new OrganizationException("The target path does not have a parent directory.");
		}

		return Path.Combine(
			targetDirectory,
			Path.GetFileNameWithoutExtension(targetMediaPath) + GetSubtitleSuffix(sourceSubtitlePath, sourceMediaPath) + Path.GetExtension(sourceSubtitlePath));
	}

	public static async Task TransferAsync(string sourcePath, string targetPath, bool copySource, bool overwrite, CancellationToken cancellationToken)
	{
		List<(string Source, string Target)> subtitleSidecars = GetSubtitleSidecars(sourcePath, targetPath);
		EnsureCanTransferSidecars(subtitleSidecars, overwrite);
		await TransferFileAsync(sourcePath, targetPath, copySource, overwrite, cancellationToken).ConfigureAwait(false);
		foreach ((string source, string target) in subtitleSidecars)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!PathSafety.PathComparer.Equals(PathSafety.Normalize(source), PathSafety.Normalize(target)))
			{
				await TransferFileAsync(source, target, copySource, overwrite, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	public static Task TransferSingleAsync(string sourcePath, string targetPath, bool copySource, bool overwrite, CancellationToken cancellationToken)
	{
		return TransferFileAsync(sourcePath, targetPath, copySource, overwrite, cancellationToken);
	}

	private static async Task TransferFileAsync(string sourcePath, string targetPath, bool copySource, bool overwrite, CancellationToken cancellationToken)
	{
		string source = PathSafety.Normalize(sourcePath);
		string text = PathSafety.Normalize(targetPath);
		if (PathSafety.PathComparer.Equals(source, text))
		{
			throw new OrganizationException("The source and target paths resolve to the same file.");
		}
		cancellationToken.ThrowIfCancellationRequested();
		if (!File.Exists(source))
		{
			throw new FileNotFoundException("The source file does not exist.", source);
		}
		Directory.CreateDirectory(Path.GetDirectoryName(text) ?? throw new OrganizationException("The target path does not have a parent directory."));
		if (!copySource && !overwrite && !File.Exists(text))
		{
			try
			{
				File.Move(source, text);
				return;
			}
			catch (IOException) when (File.Exists(source) && !File.Exists(text))
			{
			}
		}
		await CopyAndCommitAsync(source, text, overwrite, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		if (!copySource)
		{
			try
			{
				File.Delete(source);
			}
			catch (Exception ex2) when (((ex2 is IOException || ex2 is UnauthorizedAccessException) ? 1 : 0) != 0)
			{
				throw new OrganizationException("The target file was written successfully, but the source file could not be deleted: " + ex2.Message, ex2);
			}
		}
	}

	private static List<(string Source, string Target)> GetSubtitleSidecars(string sourcePath, string targetPath)
	{
		string? sourceDirectory = Path.GetDirectoryName(sourcePath);
		string? targetDirectory = Path.GetDirectoryName(targetPath);
		if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(targetDirectory))
		{
			return new List<(string Source, string Target)>();
		}

		string sourceName = Path.GetFileNameWithoutExtension(sourcePath);
		var sidecars = new List<(string Source, string Target)>();
		foreach (string sidecar in Directory.EnumerateFiles(sourceDirectory))
		{
			if (!IsSubtitleFile(sidecar) || !IsSidecarFor(sidecar, sourceName))
			{
				continue;
			}

			sidecars.Add((sidecar, GetSubtitleTargetPath(sidecar, targetPath, sourcePath)));
		}

		return sidecars;
	}

	internal static bool IsSidecarFor(string sidecarPath, string sourceName)
	{
		string sidecarName = Path.GetFileNameWithoutExtension(sidecarPath);
		return PathSafety.PathComparer.Equals(sidecarName, sourceName)
			|| sidecarName.StartsWith(sourceName + ".", PathSafety.PathComparison);
	}

	private static void EnsureCanTransferSidecars(List<(string Source, string Target)> subtitleSidecars, bool overwrite)
	{
		var targetPaths = new HashSet<string>(PathSafety.PathComparer);
		foreach ((string source, string target) in subtitleSidecars)
		{
			if (PathSafety.PathComparer.Equals(PathSafety.Normalize(source), PathSafety.Normalize(target)))
			{
				continue;
			}

			if (!targetPaths.Add(PathSafety.Normalize(target)))
			{
				throw new IOException($"Multiple subtitle sidecars target the same destination: {target}");
			}
		}

		if (overwrite)
		{
			return;
		}

		foreach ((string source, string target) in subtitleSidecars)
		{
			if (!PathSafety.PathComparer.Equals(PathSafety.Normalize(source), PathSafety.Normalize(target)) && File.Exists(target))
			{
				throw new IOException($"The destination subtitle file already exists: {target}");
			}
		}
	}

	private static string GetSubtitleSuffix(string sourceSubtitlePath, string? sourceMediaPath)
	{
		if (!IsExactSubtitleForMedia(sourceSubtitlePath, sourceMediaPath))
		{
			string name = Path.GetFileNameWithoutExtension(sourceSubtitlePath);
			string[] parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
			int index = parts.Length - 1;
			while (index > 0 && SubtitleSuffixFlags.Contains(parts[index]))
			{
				index--;
			}

			string suffix = parts.Length > 1 ? parts[index] : string.Empty;
			if (TryGetThreeLetterLanguageCode(suffix, out string languageCode))
			{
				return "." + languageCode;
			}
		}

		string language = DetectSubtitleLanguage(sourceSubtitlePath);
		return string.IsNullOrWhiteSpace(language)
			? string.Empty
			: "." + language;
	}

	private static bool IsExactSubtitleForMedia(string sourceSubtitlePath, string? sourceMediaPath)
	{
		if (string.IsNullOrWhiteSpace(sourceMediaPath))
		{
			return false;
		}

		return PathSafety.PathComparer.Equals(
			Path.GetFileNameWithoutExtension(sourceSubtitlePath),
			Path.GetFileNameWithoutExtension(sourceMediaPath));
	}

	internal static string DetectSubtitleLanguage(string sourceSubtitlePath)
	{
		if (!Path.GetExtension(sourceSubtitlePath).Equals(".srt", StringComparison.OrdinalIgnoreCase))
		{
			return string.Empty;
		}

		try
		{
			var text = new StringBuilder();
			foreach (string line in File.ReadLines(sourceSubtitlePath).Take(200))
			{
				text.AppendLine(line);
			}

			LanguageDetector detector = CreateSubtitleLanguageDetector();
			try
			{
				Language language = detector.DetectLanguageOf(text.ToString());
				if (language == Language.Unknown || language.IsoCode6391() == IsoCode6391.None)
				{
					return string.Empty;
				}

				return language.IsoCode6393().ToString().ToLowerInvariant();
			}
			finally
			{
				detector.UnloadLanguageModels();
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or AggregateException)
		{
			return string.Empty;
		}
	}

	private static LanguageDetector CreateSubtitleLanguageDetector()
	{
		return LanguageDetectorBuilder
			.FromAllSpokenLanguages()
			.WithLowAccuracyMode()
			.WithMinimumRelativeDistance(0.1)
			.WithLanguageModelsDirectory(GetBundledLanguageModelsDirectory())
			.Build();
	}

	private static string GetBundledLanguageModelsDirectory()
	{
		string? assemblyDirectory = Path.GetDirectoryName(typeof(SafeFileTransfer).Assembly.Location);
		return Path.Combine(
			string.IsNullOrWhiteSpace(assemblyDirectory) ? AppContext.BaseDirectory : assemblyDirectory,
			"Lingua",
			"LanguageModels");
	}

	private static bool TryGetThreeLetterLanguageCode(string value, out string languageCode)
	{
		languageCode = string.Empty;
		if (!value.All(char.IsLetter))
		{
			return false;
		}

		Language language;
		if (value.Length == 2)
		{
			if (!Enum.TryParse(value, ignoreCase: true, out IsoCode6391 isoCode) || isoCode == IsoCode6391.None)
			{
				return false;
			}

			language = LanguageInfo.GetByIsoCode6391(isoCode);
		}
		else if (value.Length == 3)
		{
			if (!Enum.TryParse(value, ignoreCase: true, out IsoCode6393 isoCode) || isoCode == IsoCode6393.None)
			{
				return false;
			}

			language = LanguageInfo.GetByIsoCode6393(isoCode);
		}
		else
		{
			return false;
		}

		languageCode = language.IsoCode6393().ToString().ToLowerInvariant();
		return languageCode.Length == 3;
	}

	private static async Task CopyAndCommitAsync(string source, string target, bool overwrite, CancellationToken cancellationToken)
	{
		string path = Path.GetDirectoryName(target) ?? throw new OrganizationException("The target path does not have a parent directory.");
		string temporaryPath = Path.Combine(path, $".{Path.GetFileName(target)}.autoorganize-{Guid.NewGuid():N}.tmp");
		try
		{
			using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan | FileOptions.Asynchronous))
			{
				using FileStream output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.WriteThrough | FileOptions.SequentialScan | FileOptions.Asynchronous);
				await input.CopyToAsync(output, 131072, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				await output.FlushAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				long length = output.Length;
				if (length != input.Length)
				{
					throw new IOException($"The staged copy length ({length}) does not match the source length ({input.Length}).");
				}
			}
			cancellationToken.ThrowIfCancellationRequested();
			File.Move(temporaryPath, target, overwrite);
		}
		finally
		{
			TryDelete(temporaryPath);
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception ex) when (((ex is IOException || ex is UnauthorizedAccessException) ? 1 : 0) != 0)
		{
		}
	}
}
