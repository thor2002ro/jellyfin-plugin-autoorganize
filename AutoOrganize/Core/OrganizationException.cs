using System;

namespace AutoOrganize.Core;

public class OrganizationException : Exception
{
	public OrganizationException()
	{
	}

	public OrganizationException(string msg)
		: base(msg)
	{
	}

	public OrganizationException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}
