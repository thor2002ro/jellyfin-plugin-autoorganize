using System;
using System.Collections.Generic;

namespace AutoOrganize.Model;

[Obsolete("This has been replaced by SmartMatchResult")]
public class SmartMatchInfo
{
	public string ItemName { get; set; } = string.Empty;

	public string DisplayName { get; set; } = string.Empty;

	public FileOrganizerType OrganizerType { get; set; }

	public List<string> MatchStrings { get; set; }

	public SmartMatchInfo()
	{
		MatchStrings = new List<string>();
	}
}
