using System.Collections.Generic;

namespace AutoOrganize.Model;

public class TvFileOrganizationOptions
{
    public bool IsEnabled { get; set; }

    public int MinFileSizeMb { get; set; }

    public List<string> LeftOverFileExtensionsToDelete { get; set; }

    public List<string> WatchLocations { get; set; }

    public string SeasonFolderPattern { get; set; }

    public string SeasonZeroFolderName { get; set; }

    public string EpisodeNamePattern { get; set; }

    public string MultiEpisodeNamePattern { get; set; }

    public bool PreserveOriginalFilename { get; set; }

    public bool AlwaysCreateSeasonFolders { get; set; }

    public bool OverwriteExistingEpisodes { get; set; }

    public bool DeleteEmptyFolders { get; set; }

    public bool ExtendedClean { get; set; }

    public bool CopyOriginalFile { get; set; }

    public bool AutoDetectSeries { get; set; }

    public string? DefaultSeriesLibraryPath { get; set; }

    public string SeriesFolderPattern { get; set; }

    public bool QueueLibraryScan { get; set; }

    public bool RequireApproval { get; set; }

    public TvFileOrganizationOptions()
    {
        MinFileSizeMb = 50;
        LeftOverFileExtensionsToDelete = new List<string> { "nfo" };
        WatchLocations = new List<string>();
        EpisodeNamePattern = "%sn - %sx%0e - %en.%ext";
        MultiEpisodeNamePattern = "%sn - %sx%0e-x%0ed - %en.%ext";
        SeasonFolderPattern = "Season %s";
        SeasonZeroFolderName = "Season 0";
        SeriesFolderPattern = "%fn";
        PreserveOriginalFilename = true;
        AlwaysCreateSeasonFolders = true;
        DeleteEmptyFolders = true;
        CopyOriginalFile = false;
        AutoDetectSeries = true;
        QueueLibraryScan = false;
        ExtendedClean = false;
        RequireApproval = true;
    }
}
