using System;
using System.Threading;
using System.Threading.Tasks;
using AutoOrganize.Core;
using AutoOrganize.Model;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoOrganize.Api;

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Library/FileOrganizations")]
[Produces("application/json", new string[] { })]
public class FileOrganizationController : ControllerBase
{
	private readonly IFileOrganizationService _fileOrganizationService;

	public FileOrganizationController(IFileOrganizationService fileOrganizationService)
	{
		_fileOrganizationService = fileOrganizationService;
	}

	[HttpGet]
	[ProducesResponseType(200)]
	public ActionResult<QueryResult<FileOrganizationResult>> Get([FromQuery] int? startIndex, [FromQuery] int? limit)
	{
		if (!TryCreateQuery(startIndex, limit, out FileOrganizationResultQuery query, out string error))
		{
			return BadRequest(error);
		}
		return _fileOrganizationService.GetResults(query);
	}

	[HttpDelete("{id}/File")]
	[ProducesResponseType(204)]
	[ProducesResponseType(404)]
	public async Task<ActionResult> Delete([FromRoute] string id, CancellationToken cancellationToken)
	{
		if (_fileOrganizationService.GetResult(id) == null)
		{
			return NotFound();
		}
		try
		{
			await _fileOrganizationService.DeleteOriginalFile(id, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return NoContent();
		}
		catch (OrganizationException exception)
		{
			return OrganizationProblem(exception);
		}
	}

	[HttpDelete("{id}")]
	[ProducesResponseType(204)]
	[ProducesResponseType(404)]
	public async Task<ActionResult> Reject([FromRoute] string id, CancellationToken cancellationToken)
	{
		if (_fileOrganizationService.GetResult(id) == null)
		{
			return NotFound();
		}
		await _fileOrganizationService.DeleteResult(id, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		return NoContent();
	}

	[HttpDelete]
	[ProducesResponseType(204)]
	public async Task<ActionResult> ClearActivityLog(CancellationToken cancellationToken)
	{
		await _fileOrganizationService.ClearLog(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		return NoContent();
	}

	[HttpDelete("Completed")]
	[ProducesResponseType(204)]
	public async Task<ActionResult> ClearCompletedActivityLog(CancellationToken cancellationToken)
	{
		await _fileOrganizationService.ClearCompleted(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		return NoContent();
	}

	[HttpPost("{id}/Organize")]
	[ProducesResponseType(204)]
	[ProducesResponseType(404)]
	public async Task<ActionResult> PerformOrganization([FromRoute] string id, CancellationToken cancellationToken)
	{
		if (_fileOrganizationService.GetResult(id) == null)
		{
			return NotFound();
		}
		try
		{
			await _fileOrganizationService.PerformOrganization(id, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return NoContent();
		}
		catch (OrganizationException exception)
		{
			return OrganizationProblem(exception);
		}
	}

	[HttpPost("{id}/Episode/Organize")]
	[ProducesResponseType(204)]
	[ProducesResponseType(404)]
	public async Task<ActionResult> OrganizeEpisode([FromRoute] string id, [FromBody] EpisodeFileOrganizationRequest request, CancellationToken cancellationToken)
	{
		if (_fileOrganizationService.GetResult(id) == null)
		{
			return NotFound();
		}
		if (request == null)
		{
			return BadRequest("A correction request is required.");
		}
		string? text = ValidateEpisodeRequest(request);
		if (text != null)
		{
			return BadRequest(text);
		}
		request.ResultId = id;
		try
		{
			await _fileOrganizationService.PerformOrganization(request, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return NoContent();
		}
		catch (OrganizationException exception)
		{
			return OrganizationProblem(exception);
		}
	}

	[HttpPost("{id}/Movie/Organize")]
	[ProducesResponseType(204)]
	[ProducesResponseType(404)]
	public async Task<ActionResult> OrganizeMovie([FromRoute] string id, [FromBody] MovieFileOrganizationRequest request, CancellationToken cancellationToken)
	{
		if (_fileOrganizationService.GetResult(id) == null)
		{
			return NotFound();
		}
		if (request == null)
		{
			return BadRequest("A correction request is required.");
		}
		string? text = ValidateMovieRequest(request);
		if (text != null)
		{
			return BadRequest(text);
		}
		request.ResultId = id;
		try
		{
			await _fileOrganizationService.PerformOrganization(request, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return NoContent();
		}
		catch (OrganizationException exception)
		{
			return OrganizationProblem(exception);
		}
	}

	[HttpGet("SmartMatches")]
	[ProducesResponseType(200)]
	public ActionResult<QueryResult<SmartMatchResult>> GetSmartMatchInfos([FromQuery] int? startIndex, [FromQuery] int? limit)
	{
		if (!TryCreateQuery(startIndex, limit, out FileOrganizationResultQuery query, out string error))
		{
			return BadRequest(error);
		}
		return _fileOrganizationService.GetSmartMatchInfos(query);
	}

	[HttpPost("SmartMatches/Delete")]
	[ProducesResponseType(204)]
	[ProducesResponseType(400)]
	public async Task<ActionResult> DeleteSmartWatchEntry([FromBody] SmartMatchDeleteRequest request, CancellationToken cancellationToken)
	{
		if (request?.Entries == null || request.Entries.Count == 0)
		{
			return BadRequest("At least one smart-match entry is required.");
		}
		foreach (NameValuePair entry in request.Entries)
		{
			if (entry == null || !Guid.TryParse(entry.Name, out _) || string.IsNullOrWhiteSpace(entry.Value))
			{
				return BadRequest("Each smart-match entry must include a valid id and value.");
			}
		}
		await _fileOrganizationService.DeleteSmartMatchEntries(request.Entries, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		return NoContent();
	}

	private static bool TryCreateQuery(int? startIndex, int? limit, out FileOrganizationResultQuery query, out string error)
	{
		query = new FileOrganizationResultQuery();
		error = string.Empty;
		if (startIndex < 0)
		{
			error = "StartIndex cannot be negative.";
			return false;
		}
		if (limit <= 0 || limit > 1000)
		{
			error = "Limit must be between 1 and 1000 when specified.";
			return false;
		}
		query = new FileOrganizationResultQuery
		{
			Limit = limit,
			StartIndex = startIndex
		};
		return true;
	}

	private static string? ValidateEpisodeRequest(EpisodeFileOrganizationRequest request)
	{
		if (request.SeasonNumber < 0)
		{
			return "SeasonNumber cannot be negative.";
		}
		if (request.EpisodeNumber <= 0)
		{
			return "EpisodeNumber must be positive.";
		}
		if (request.EndingEpisodeNumber.HasValue && request.EndingEpisodeNumber.Value < request.EpisodeNumber)
		{
			return "EndingEpisodeNumber cannot be less than EpisodeNumber.";
		}
		if (string.IsNullOrWhiteSpace(request.SeriesId) && (string.IsNullOrWhiteSpace(request.NewSeriesName) || string.IsNullOrWhiteSpace(request.TargetFolder)))
		{
			return "A series id or both a new-series name and target folder are required.";
		}
		return null;
	}

	private static string? ValidateMovieRequest(MovieFileOrganizationRequest request)
	{
		if (string.IsNullOrWhiteSpace(request.MovieId) && (string.IsNullOrWhiteSpace(request.NewMovieName) || string.IsNullOrWhiteSpace(request.TargetFolder)))
		{
			return "A movie id or both a new-movie name and target folder are required.";
		}
		return null;
	}

	private ObjectResult OrganizationProblem(OrganizationException exception)
	{
		return Problem(exception.Message, null, 409, "The file could not be organized safely.");
	}
}
