using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoOrganize.Model;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;

namespace AutoOrganize.Data;

public interface IFileOrganizationRepository
{
	void SaveResult(FileOrganizationResult result, CancellationToken cancellationToken);

	Task Delete(string id, CancellationToken cancellationToken);

	FileOrganizationResult? GetResult(string id);

	QueryResult<FileOrganizationResult> GetResults(FileOrganizationResultQuery query);

	Task DeleteAll(CancellationToken cancellationToken);

	Task DeleteCompleted(CancellationToken cancellationToken);

	void SaveResult(SmartMatchResult result, CancellationToken cancellationToken);

	Task AddSmartMatchString(string itemName, string displayName, FileOrganizerType organizerType, string matchString, CancellationToken cancellationToken);

	Task DeleteSmartMatch(string id, string matchString, CancellationToken cancellationToken);

	Task DeleteSmartMatchEntries(IReadOnlyList<NameValuePair> entries, CancellationToken cancellationToken);

	void DeleteSmartMatch(string id);

	void DeleteAllSmartMatch();

	QueryResult<SmartMatchResult> GetSmartMatch(FileOrganizationResultQuery query);
}
