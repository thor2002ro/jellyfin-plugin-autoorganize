using System;
using System.Collections.Generic;

namespace AutoOrganize.Model;

public class AutoOrganizeOptions
{
	public TvFileOrganizationOptions TvOptions { get; set; }

	public MovieFileOrganizationOptions MovieOptions { get; set; }

	[Obsolete("This configuration is now stored in the SQLite database.")]
	public List<SmartMatchInfo> SmartMatchInfos { get; set; } = new List<SmartMatchInfo>();

	public bool Converted { get; set; }

	public AutoOrganizeOptions()
	{
		TvOptions = new TvFileOrganizationOptions();
		MovieOptions = new MovieFileOrganizationOptions();
		Converted = false;
	}
}
