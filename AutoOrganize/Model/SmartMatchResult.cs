using System;
using System.Collections.Generic;

namespace AutoOrganize.Model;

public class SmartMatchResult
{
	public Guid Id { get; set; }

	public string ItemName { get; set; } = string.Empty;

	public string DisplayName { get; set; } = string.Empty;

	public FileOrganizerType OrganizerType { get; set; }

	public List<string> MatchStrings { get; }

	public SmartMatchResult()
	{
		Id = Guid.NewGuid();
		MatchStrings = new List<string>();
	}
}
