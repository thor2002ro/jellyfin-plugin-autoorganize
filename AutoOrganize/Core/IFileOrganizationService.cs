using System.Threading;
using System.Threading.Tasks;
using AutoOrganize.Model;
using MediaBrowser.Model.Querying;

namespace AutoOrganize.Core;

public interface IFileOrganizationService
{
	void BeginProcessNewFiles();

	Task DeleteOriginalFile(string resultId, CancellationToken cancellationToken);

	Task ClearLog(CancellationToken cancellationToken);

	Task ClearCompleted(CancellationToken cancellationToken);

	Task PerformOrganization(string resultId, CancellationToken cancellationToken);

	Task PerformOrganization(EpisodeFileOrganizationRequest request, CancellationToken cancellationToken);

	Task PerformOrganization(MovieFileOrganizationRequest request, CancellationToken cancellationToken);

	QueryResult<FileOrganizationResult> GetResults(FileOrganizationResultQuery query);

	FileOrganizationResult? GetResult(string id);

	FileOrganizationResult? GetResultBySourcePath(string path);

	void SaveResult(FileOrganizationResult result, CancellationToken cancellationToken);

	void SaveResult(SmartMatchResult result, CancellationToken cancellationToken);

	QueryResult<SmartMatchResult> GetSmartMatchInfos(FileOrganizationResultQuery query);

	QueryResult<SmartMatchResult> GetSmartMatchInfos();

	Task DeleteSmartMatchEntry(string id, string matchString, CancellationToken cancellationToken);

	bool AddToInProgressList(FileOrganizationResult result, bool fullClientRefresh);

	bool RemoveFromInprogressList(FileOrganizationResult result);
}
