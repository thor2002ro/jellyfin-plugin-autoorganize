using System;
using System.Collections.Generic;
using AutoOrganize.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace AutoOrganize;

public class AutoOrganizePlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
	public override Guid Id => new Guid("70b7b43b-471b-4159-b4be-56750c795499");

	public override string Name => "Auto Organize";

	public override string Description => "Automatically organize new media";

	public AutoOrganizePlugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer)
		: base(appPaths, xmlSerializer)
	{
	}

	public IEnumerable<PluginPageInfo> GetPages()
	{
		return new PluginPageInfo[10]
		{
			new PluginPageInfo
			{
				Name = "AutoOrganizeLog",
				EmbeddedResourcePath = GetType().Namespace + ".Web.autoorganizelog.html",
				EnableInMainMenu = true
			},
			new PluginPageInfo
			{
				Name = "AutoOrganizeSmart",
				EmbeddedResourcePath = GetType().Namespace + ".Web.autoorganizesmart.html"
			},
			new PluginPageInfo
			{
				Name = "AutoOrganizeTv",
				EmbeddedResourcePath = GetType().Namespace + ".Web.autoorganizetv.html"
			},
			new PluginPageInfo
			{
				Name = "AutoOrganizeMovie",
				EmbeddedResourcePath = GetType().Namespace + ".Web.autoorganizemovie.html"
			},
			new PluginPageInfo
			{
				Name = "AutoOrganizeLogJs",
				EmbeddedResourcePath = GetType().Namespace + ".Web.autoorganizelog.js"
			},
			new PluginPageInfo
			{
				Name = "AutoOrganizeSmartJs",
				EmbeddedResourcePath = GetType().Namespace + ".Web.autoorganizesmart.js"
			},
			new PluginPageInfo
			{
				Name = "AutoOrganizeTvJs",
				EmbeddedResourcePath = GetType().Namespace + ".Web.autoorganizetv.js"
			},
			new PluginPageInfo
			{
				Name = "AutoOrganizeMovieJs",
				EmbeddedResourcePath = GetType().Namespace + ".Web.autoorganizemovie.js"
			},
			new PluginPageInfo
			{
				Name = "FileOrganizerJs",
				EmbeddedResourcePath = GetType().Namespace + ".Web.fileorganizer.js"
			},
			new PluginPageInfo
			{
				Name = "FileOrganizerHtml",
				EmbeddedResourcePath = GetType().Namespace + ".Web.fileorganizer.template.html"
			}
		};
	}
}
