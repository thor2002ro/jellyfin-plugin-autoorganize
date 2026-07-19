using System.Threading;
using System.Threading.Tasks;
using AutoOrganize.Model;
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

	void DeleteSmartMatch(string id);

	Task DeleteSmartMatch(string id, string matchString, CancellationToken cancellationToken);

	void DeleteAllSmartMatch();

	QueryResult<SmartMatchResult> GetSmartMatch(FileOrganizationResultQuery query);
}
