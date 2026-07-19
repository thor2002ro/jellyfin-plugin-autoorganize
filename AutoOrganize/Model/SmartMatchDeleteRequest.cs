using System.Collections.Generic;
using MediaBrowser.Model.Dto;

namespace AutoOrganize.Model;

public sealed class SmartMatchDeleteRequest
{
	public IReadOnlyList<NameValuePair> Entries { get; set; } = new List<NameValuePair>();
}
