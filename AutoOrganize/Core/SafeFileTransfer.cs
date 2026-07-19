using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AutoOrganize.Core;

internal static class SafeFileTransfer
{
	private const int CopyBufferSize = 131072;

	public static async Task TransferAsync(string sourcePath, string targetPath, bool copySource, bool overwrite, CancellationToken cancellationToken)
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
