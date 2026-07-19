using System;
using System.Collections.Generic;

namespace AutoOrganize.Model;

public class FileOrganizationResult
{
	public string Id { get; set; } = string.Empty;

	public string OriginalPath { get; set; } = string.Empty;

	public string OriginalFileName { get; set; } = string.Empty;

	public string? ExtractedName { get; set; }

	public int? ExtractedYear { get; set; }

	public int? ExtractedSeasonNumber { get; set; }

	public int? ExtractedEpisodeNumber { get; set; }

	public int? ExtractedEndingEpisodeNumber { get; set; }

	public string? TargetPath { get; set; }

	public DateTime Date { get; set; }

	public string? StatusMessage { get; set; }

	public FileSortingStatus Status { get; set; }

	public FileOrganizerType Type { get; set; }

	public IReadOnlyList<string> DuplicatePaths { get; set; }

	public IReadOnlyList<FileOrganizationBundleItem> BundleItems { get; set; }

	public long FileSize { get; set; }

	public bool IsInProgress { get; set; }

	public FileOrganizationResult()
	{
		DuplicatePaths = new List<string>();
		BundleItems = new List<FileOrganizationBundleItem>();
	}
}

public class FileOrganizationBundleItem
{
	public string SourcePath { get; set; } = string.Empty;

	public string TargetPath { get; set; } = string.Empty;

	public int? SeasonNumber { get; set; }
}
