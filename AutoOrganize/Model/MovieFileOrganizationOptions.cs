using System.Collections.Generic;

namespace AutoOrganize.Model;

public class MovieFileOrganizationOptions
{
    public bool IsEnabled { get; set; }

    public int MinFileSizeMb { get; set; }

    public List<string> LeftOverFileExtensionsToDelete { get; set; }

    public List<string> WatchLocations { get; set; }

    public string MoviePattern { get; set; }

    public bool PreserveOriginalFilename { get; set; }

    public bool OverwriteExistingFiles { get; set; }

    public bool DeleteEmptyFolders { get; set; }

    public bool ExtendedClean { get; set; }

    public bool CopyOriginalFile { get; set; }

    public bool AutoDetectMovie { get; set; }

    public string? DefaultMovieLibraryPath { get; set; }

    public bool MovieFolder { get; set; }

    public string MovieFolderPattern { get; set; }

    public bool QueueLibraryScan { get; set; }

    public bool RequireApproval { get; set; }

    public MovieFileOrganizationOptions()
    {
        MinFileSizeMb = 50;
        LeftOverFileExtensionsToDelete = new List<string>();
        MoviePattern = "%fn.%ext";
        PreserveOriginalFilename = false;
        WatchLocations = new List<string>();
        CopyOriginalFile = false;
        AutoDetectMovie = true;
        MovieFolder = false;
        MovieFolderPattern = "%mn (%my)";
        QueueLibraryScan = false;
        ExtendedClean = false;
    }
}
