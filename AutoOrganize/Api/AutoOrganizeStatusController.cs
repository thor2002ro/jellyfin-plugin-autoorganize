using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoOrganize.Api;

public sealed class AutoOrganizeStatus
{
	public string PluginVersion { get; set; } = string.Empty;
}

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("AutoOrganize/Status")]
public sealed class AutoOrganizeStatusController : ControllerBase
{
	[HttpGet]
	public ActionResult<AutoOrganizeStatus> Get()
	{
		Assembly pluginAssembly = typeof(AutoOrganizePlugin).Assembly;
		return Ok(new AutoOrganizeStatus
		{
			PluginVersion = pluginAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
				?? pluginAssembly.GetName().Version?.ToString()
				?? "unknown"
		});
	}
}
