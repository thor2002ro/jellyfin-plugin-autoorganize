using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutoOrganize.Model;
using MediaBrowser.Controller;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AutoOrganize.Data;

public sealed class SqliteFileOrganizationRepository : IFileOrganizationRepository, IDisposable
{
	private const int SqliteCorrupt = 11;

	private const int SqliteNotADatabase = 26;

	private const int BusyTimeoutMilliseconds = 5000;

	private const string DatabaseDateTimeFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFFK";

	private const string FileResultColumns = "ResultId, OriginalPath, TargetPath, FileLength, OrganizationDate, Status, OrganizationType, StatusMessage, ExtractedName, ExtractedYear, ExtractedSeasonNumber, ExtractedEpisodeNumber, ExtractedEndingEpisodeNumber, DuplicatePaths, BundleItems";

	private const string SmartMatchColumns = "Id, ItemName, DisplayName, OrganizerType, MatchStrings";

	private readonly ILogger<SqliteFileOrganizationRepository> _logger;

	private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

	private readonly string _databasePath;

	private readonly string _connectionString;

	private bool _disposed;

	public SqliteFileOrganizationRepository(ILogger<SqliteFileOrganizationRepository> logger, IServerApplicationPaths applicationPaths)
		: this(logger, Path.Combine(applicationPaths.DataPath, "fileorganization.db"))
	{
	}

	public SqliteFileOrganizationRepository(ILogger<SqliteFileOrganizationRepository> logger, string databasePath)
	{
		ArgumentNullException.ThrowIfNull(logger, "logger");
		ArgumentException.ThrowIfNullOrWhiteSpace(databasePath, "databasePath");
		_logger = logger;
		_databasePath = Path.GetFullPath(databasePath);
		_connectionString = new SqliteConnectionStringBuilder
		{
			DataSource = _databasePath,
			Mode = SqliteOpenMode.ReadWriteCreate,
			Cache = SqliteCacheMode.Shared,
			Pooling = true,
			DefaultTimeout = 5
		}.ToString();
	}

	public void Initialize()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		string? directoryName = Path.GetDirectoryName(_databasePath);
		if (!string.IsNullOrEmpty(directoryName))
		{
			Directory.CreateDirectory(directoryName);
		}
		try
		{
			InitializeDatabase();
		}
		catch (SqliteException ex) when (((Func<bool>)delegate
		{
			// Could not convert BlockContainer to single expression
			int sqliteErrorCode = ex.SqliteErrorCode;
			return ((sqliteErrorCode == 11 || sqliteErrorCode == 26) ? 1 : 0) != 0;
		}).Invoke())
		{
			string text = BackupCorruptDatabase();
			_logger.LogCritical(ex, "Auto Organize database was corrupt and has been preserved at {BackupPath}; a new database will be created", text);
			SqliteConnection.ClearAllPools();
			InitializeDatabase();
		}
	}

	public void SaveResult(FileOrganizationResult result, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(result, "result");
		cancellationToken.ThrowIfCancellationRequested();
		Guid id = ParseGuid(result.Id, "result");
		ExecuteWrite(delegate(SqliteConnection connection, SqliteTransaction transaction)
		{
			using SqliteCommand sqliteCommand = connection.CreateCommand();
			sqliteCommand.Transaction = transaction;
			sqliteCommand.CommandText = "INSERT INTO FileOrganizerResults\n    (ResultId, OriginalPath, TargetPath, FileLength, OrganizationDate, Status, OrganizationType,\n     StatusMessage, ExtractedName, ExtractedYear, ExtractedSeasonNumber, ExtractedEpisodeNumber,\n     ExtractedEndingEpisodeNumber, DuplicatePaths, BundleItems)\nVALUES\n    ($ResultId, $OriginalPath, $TargetPath, $FileLength, $OrganizationDate, $Status, $OrganizationType,\n     $StatusMessage, $ExtractedName, $ExtractedYear, $ExtractedSeasonNumber, $ExtractedEpisodeNumber,\n     $ExtractedEndingEpisodeNumber, $DuplicatePaths, $BundleItems)\nON CONFLICT(ResultId) DO UPDATE SET\n    OriginalPath = excluded.OriginalPath,\n    TargetPath = excluded.TargetPath,\n    FileLength = excluded.FileLength,\n    OrganizationDate = excluded.OrganizationDate,\n    Status = excluded.Status,\n    OrganizationType = excluded.OrganizationType,\n    StatusMessage = excluded.StatusMessage,\n    ExtractedName = excluded.ExtractedName,\n    ExtractedYear = excluded.ExtractedYear,\n    ExtractedSeasonNumber = excluded.ExtractedSeasonNumber,\n    ExtractedEpisodeNumber = excluded.ExtractedEpisodeNumber,\n    ExtractedEndingEpisodeNumber = excluded.ExtractedEndingEpisodeNumber,\n    DuplicatePaths = excluded.DuplicatePaths,\n    BundleItems = excluded.BundleItems;";
			AddParameter(sqliteCommand, "$ResultId", id.ToByteArray());
			AddParameter(sqliteCommand, "$OriginalPath", result.OriginalPath);
			AddParameter(sqliteCommand, "$TargetPath", result.TargetPath);
			AddParameter(sqliteCommand, "$FileLength", result.FileSize);
			AddParameter(sqliteCommand, "$OrganizationDate", FormatDateTime(result.Date));
			AddParameter(sqliteCommand, "$Status", result.Status.ToString());
			AddParameter(sqliteCommand, "$OrganizationType", result.Type.ToString());
			AddParameter(sqliteCommand, "$StatusMessage", result.StatusMessage);
			AddParameter(sqliteCommand, "$ExtractedName", result.ExtractedName);
			AddParameter(sqliteCommand, "$ExtractedYear", result.ExtractedYear);
			AddParameter(sqliteCommand, "$ExtractedSeasonNumber", result.ExtractedSeasonNumber);
			AddParameter(sqliteCommand, "$ExtractedEpisodeNumber", result.ExtractedEpisodeNumber);
			AddParameter(sqliteCommand, "$ExtractedEndingEpisodeNumber", result.ExtractedEndingEpisodeNumber);
			AddParameter(sqliteCommand, "$DuplicatePaths", JsonSerializer.Serialize(result.DuplicatePaths ?? Array.Empty<string>()));
			AddParameter(sqliteCommand, "$BundleItems", JsonSerializer.Serialize(result.BundleItems ?? Array.Empty<FileOrganizationBundleItem>()));
			sqliteCommand.ExecuteNonQuery();
		}, cancellationToken);
	}

	public async Task Delete(string id, CancellationToken cancellationToken)
	{
		Guid resultId = ParseGuid(id, "id");
		await ExecuteWriteAsync(async delegate(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken2)
		{
			using SqliteCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = "DELETE FROM FileOrganizerResults WHERE ResultId = $ResultId;";
			AddParameter(command, "$ResultId", resultId.ToByteArray());
			await command.ExecuteNonQueryAsync(cancellationToken2).ConfigureAwait(continueOnCapturedContext: false);
		}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	public FileOrganizationResult? GetResult(string id)
	{
		Guid guid = ParseGuid(id, "id");
		using SqliteConnection sqliteConnection = OpenConnection();
		using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
		sqliteCommand.CommandText = "SELECT " + FileResultColumns + " FROM FileOrganizerResults WHERE ResultId = $ResultId LIMIT 1;";
		AddParameter(sqliteCommand, "$ResultId", guid.ToByteArray());
		using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
		FileOrganizationResult? result;
		return (sqliteDataReader.Read() && TryReadFileResult(sqliteDataReader, out result)) ? result : null;
	}

	public QueryResult<FileOrganizationResult> GetResults(FileOrganizationResultQuery query)
	{
		ArgumentNullException.ThrowIfNull(query, "query");
		var (num, num2) = ValidatePaging(query);
		using SqliteConnection sqliteConnection = OpenConnection();
		List<FileOrganizationResult> list = new List<FileOrganizationResult>();
		using (SqliteCommand sqliteCommand = sqliteConnection.CreateCommand())
		{
			sqliteCommand.CommandText = "SELECT " + FileResultColumns + " FROM FileOrganizerResults ORDER BY OrganizationDate DESC, ResultId LIMIT $Limit OFFSET $Offset;";
			AddParameter(sqliteCommand, "$Limit", num2);
			AddParameter(sqliteCommand, "$Offset", num);
			using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
			while (sqliteDataReader.Read())
			{
				if (TryReadFileResult(sqliteDataReader, out FileOrganizationResult? result))
				{
					list.Add(result);
				}
			}
		}
		return new QueryResult<FileOrganizationResult>
		{
			Items = list.ToArray(),
			TotalRecordCount = CountFileResults(sqliteConnection)
		};
	}

	public Task DeleteAll(CancellationToken cancellationToken)
	{
		return ExecuteWriteAsync(async delegate(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken2)
		{
			using SqliteCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = "DELETE FROM FileOrganizerResults;";
			await command.ExecuteNonQueryAsync(cancellationToken2).ConfigureAwait(continueOnCapturedContext: false);
		}, cancellationToken);
	}

	public Task DeleteCompleted(CancellationToken cancellationToken)
	{
		return ExecuteWriteAsync(async delegate(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken2)
		{
			using SqliteCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = "DELETE FROM FileOrganizerResults WHERE Status = $Status;";
			AddParameter(command, "$Status", FileSortingStatus.Success.ToString());
			await command.ExecuteNonQueryAsync(cancellationToken2).ConfigureAwait(continueOnCapturedContext: false);
		}, cancellationToken);
	}

	public void SaveResult(SmartMatchResult result, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(result, "result");
		ArgumentException.ThrowIfNullOrWhiteSpace(result.ItemName, "result.ItemName");
		ExecuteWrite((connection, transaction) => SaveSmartMatch(connection, transaction, result), cancellationToken);
	}

	public Task AddSmartMatchString(string itemName, string displayName, FileOrganizerType organizerType, string matchString, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(itemName);
		ArgumentException.ThrowIfNullOrWhiteSpace(matchString);
		return ExecuteWriteAsync((connection, transaction, token) =>
		{
			token.ThrowIfCancellationRequested();
			var smartMatch = new SmartMatchResult
			{
				ItemName = itemName,
				DisplayName = displayName,
				OrganizerType = organizerType
			};
			smartMatch.MatchStrings.Add(matchString);
			SaveSmartMatch(connection, transaction, smartMatch);
			return Task.CompletedTask;
		}, cancellationToken);
	}

	private void SaveSmartMatch(SqliteConnection connection, SqliteTransaction transaction, SmartMatchResult incoming)
	{
		SmartMatchResult? existing;
		using (SqliteCommand selectCommand = connection.CreateCommand())
		{
			selectCommand.Transaction = transaction;
			selectCommand.CommandText = "SELECT Id, ItemName, DisplayName, OrganizerType, MatchStrings FROM SmartMatch WHERE OrganizerType = $OrganizerType COLLATE NOCASE AND ItemName = $ItemName COLLATE NOCASE LIMIT 1;";
			AddParameter(selectCommand, "$OrganizerType", incoming.OrganizerType.ToString());
			AddParameter(selectCommand, "$ItemName", incoming.ItemName);
			using SqliteDataReader reader = selectCommand.ExecuteReader();
			existing = reader.Read() && TryReadSmartMatch(reader, out SmartMatchResult? result) ? result : null;
		}

		if (existing == null)
		{
			using SqliteCommand upsertCommand = connection.CreateCommand();
			upsertCommand.Transaction = transaction;
			upsertCommand.CommandText = "INSERT INTO SmartMatch (Id, ItemName, DisplayName, OrganizerType, MatchStrings)\nVALUES ($Id, $ItemName, $DisplayName, $OrganizerType, $MatchStrings)\nON CONFLICT(Id) DO UPDATE SET\n    ItemName = excluded.ItemName,\n    DisplayName = excluded.DisplayName,\n    OrganizerType = excluded.OrganizerType,\n    MatchStrings = excluded.MatchStrings;";
			AddParameter(upsertCommand, "$Id", incoming.Id.ToByteArray());
			AddParameter(upsertCommand, "$ItemName", incoming.ItemName);
			AddParameter(upsertCommand, "$DisplayName", incoming.DisplayName);
			AddParameter(upsertCommand, "$OrganizerType", incoming.OrganizerType.ToString());
			AddParameter(upsertCommand, "$MatchStrings", JsonSerializer.Serialize(incoming.MatchStrings));
			upsertCommand.ExecuteNonQuery();
			return;
		}

		List<string> matchStrings = (existing.Id == incoming.Id
				? incoming.MatchStrings
				: existing.MatchStrings.Concat(incoming.MatchStrings))
			.Where(value => !string.IsNullOrWhiteSpace(value))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		using (SqliteCommand deleteCommand = connection.CreateCommand())
		{
			deleteCommand.Transaction = transaction;
			deleteCommand.CommandText = "DELETE FROM SmartMatch WHERE Id = $IncomingId AND Id <> $ExistingId;";
			AddParameter(deleteCommand, "$IncomingId", incoming.Id.ToByteArray());
			AddParameter(deleteCommand, "$ExistingId", existing.Id.ToByteArray());
			deleteCommand.ExecuteNonQuery();
		}

		using SqliteCommand updateCommand = connection.CreateCommand();
		updateCommand.Transaction = transaction;
		updateCommand.CommandText = "UPDATE SmartMatch SET ItemName = $ItemName, DisplayName = $DisplayName, OrganizerType = $OrganizerType, MatchStrings = $MatchStrings WHERE Id = $Id;";
		AddParameter(updateCommand, "$ItemName", incoming.ItemName);
		AddParameter(updateCommand, "$DisplayName", string.IsNullOrWhiteSpace(incoming.DisplayName) ? existing.DisplayName : incoming.DisplayName);
		AddParameter(updateCommand, "$OrganizerType", incoming.OrganizerType.ToString());
		AddParameter(updateCommand, "$MatchStrings", JsonSerializer.Serialize(matchStrings));
		AddParameter(updateCommand, "$Id", existing.Id.ToByteArray());
		updateCommand.ExecuteNonQuery();
		incoming.Id = existing.Id;
	}

	public Task DeleteSmartMatch(string id, string matchString, CancellationToken cancellationToken)
	{
		return DeleteSmartMatchEntries(
			new[]
			{
				new NameValuePair
				{
					Name = id,
					Value = matchString
				}
			},
			cancellationToken);
	}

	public Task DeleteSmartMatchEntries(IReadOnlyList<NameValuePair> entries, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(entries);
		var validatedEntries = entries.Select(entry =>
		{
			ArgumentNullException.ThrowIfNull(entry);
			ArgumentException.ThrowIfNullOrWhiteSpace(entry.Value, "entry.Value");
			return (Id: ParseGuid(entry.Name, "entry.Name"), MatchString: entry.Value);
		}).ToArray();
		return ExecuteWriteAsync(async delegate(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
		{
			foreach (var entry in validatedEntries)
			{
				SmartMatchResult? smartMatchResult;
				using (SqliteCommand selectCommand = connection.CreateCommand())
				{
					selectCommand.Transaction = transaction;
					selectCommand.CommandText = "SELECT Id, ItemName, DisplayName, OrganizerType, MatchStrings FROM SmartMatch WHERE Id = $Id LIMIT 1;";
					AddParameter(selectCommand, "$Id", entry.Id.ToByteArray());
					using SqliteDataReader reader = await selectCommand.ExecuteReaderAsync(token).ConfigureAwait(continueOnCapturedContext: false);
					smartMatchResult = ((await reader.ReadAsync(token).ConfigureAwait(continueOnCapturedContext: false) && TryReadSmartMatch(reader, out SmartMatchResult? result)) ? result : null);
				}
				if (smartMatchResult == null)
				{
					continue;
				}
				smartMatchResult.MatchStrings.RemoveAll((string value) => string.Equals(value, entry.MatchString, StringComparison.OrdinalIgnoreCase));
				using SqliteCommand updateCommand = connection.CreateCommand();
				updateCommand.Transaction = transaction;
				if (smartMatchResult.MatchStrings.Count == 0)
				{
					updateCommand.CommandText = "DELETE FROM SmartMatch WHERE Id = $Id;";
					AddParameter(updateCommand, "$Id", entry.Id.ToByteArray());
				}
				else
				{
					updateCommand.CommandText = "UPDATE SmartMatch SET MatchStrings = $MatchStrings WHERE Id = $Id;";
					AddParameter(updateCommand, "$MatchStrings", JsonSerializer.Serialize(smartMatchResult.MatchStrings));
					AddParameter(updateCommand, "$Id", entry.Id.ToByteArray());
				}
				await updateCommand.ExecuteNonQueryAsync(token).ConfigureAwait(continueOnCapturedContext: false);
			}
		}, cancellationToken);
	}

	public void DeleteSmartMatch(string id)
	{
		Guid resultId = ParseGuid(id, "id");
		ExecuteWrite((connection, transaction) =>
		{
			using SqliteCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = "DELETE FROM SmartMatch WHERE Id = $Id;";
			AddParameter(command, "$Id", resultId.ToByteArray());
			command.ExecuteNonQuery();
		}, CancellationToken.None);
	}

	public void DeleteAllSmartMatch()
	{
		ExecuteWrite((connection, transaction) =>
		{
			using SqliteCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = "DELETE FROM SmartMatch;";
			command.ExecuteNonQuery();
		}, CancellationToken.None);
	}

	public QueryResult<SmartMatchResult> GetSmartMatch(FileOrganizationResultQuery query)
	{
		ArgumentNullException.ThrowIfNull(query, "query");
		var (num, num2) = ValidatePaging(query);
		using SqliteConnection sqliteConnection = OpenConnection();
		List<SmartMatchResult> list = new List<SmartMatchResult>();
		using (SqliteCommand sqliteCommand = sqliteConnection.CreateCommand())
		{
			sqliteCommand.CommandText = "SELECT Id, ItemName, DisplayName, OrganizerType, MatchStrings FROM SmartMatch ORDER BY ItemName COLLATE NOCASE, Id LIMIT $Limit OFFSET $Offset;";
			AddParameter(sqliteCommand, "$Limit", num2);
			AddParameter(sqliteCommand, "$Offset", num);
			using SqliteDataReader sqliteDataReader = sqliteCommand.ExecuteReader();
			while (sqliteDataReader.Read())
			{
				if (TryReadSmartMatch(sqliteDataReader, out SmartMatchResult? result))
				{
					list.Add(result);
				}
			}
		}
		return new QueryResult<SmartMatchResult>
		{
			Items = list.ToArray(),
			TotalRecordCount = CountSmartMatches(sqliteConnection)
		};
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_writeLock.Dispose();
		using SqliteConnection connection = new SqliteConnection(_connectionString);
		SqliteConnection.ClearPool(connection);
	}

	private void InitializeDatabase()
	{
		using SqliteConnection sqliteConnection = OpenConnection();
		using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
		sqliteCommand.CommandText = "PRAGMA journal_mode = WAL;\nPRAGMA synchronous = NORMAL;\nPRAGMA foreign_keys = ON;\nPRAGMA busy_timeout = 5000;\n\nCREATE TABLE IF NOT EXISTS FileOrganizerResults (\n    ResultId BLOB PRIMARY KEY,\n    OriginalPath TEXT,\n    TargetPath TEXT,\n    FileLength INTEGER NOT NULL DEFAULT 0,\n    OrganizationDate TEXT NOT NULL,\n    Status TEXT NOT NULL,\n    OrganizationType TEXT NOT NULL,\n    StatusMessage TEXT,\n    ExtractedName TEXT,\n    ExtractedYear INTEGER NULL,\n    ExtractedSeasonNumber INTEGER NULL,\n    ExtractedEpisodeNumber INTEGER NULL,\n    ExtractedEndingEpisodeNumber INTEGER NULL,\n    DuplicatePaths TEXT NULL\n);\nCREATE INDEX IF NOT EXISTS idx_FileOrganizerResults_Date\n    ON FileOrganizerResults(OrganizationDate DESC);\n\nCREATE TABLE IF NOT EXISTS SmartMatch (\n    Id BLOB PRIMARY KEY,\n    ItemName TEXT NOT NULL,\n    DisplayName TEXT,\n    OrganizerType TEXT NOT NULL,\n    MatchStrings TEXT NULL\n);\nCREATE INDEX IF NOT EXISTS idx_SmartMatch_ItemName\n    ON SmartMatch(ItemName COLLATE NOCASE);";
		sqliteCommand.ExecuteNonQuery();
		using (SqliteCommand versionCommand = sqliteConnection.CreateCommand())
		{
			versionCommand.CommandText = "PRAGMA user_version;";
			int userVersion = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
			if (!ColumnExists(sqliteConnection, "FileOrganizerResults", "BundleItems"))
			{
				versionCommand.CommandText = "ALTER TABLE FileOrganizerResults ADD COLUMN BundleItems TEXT NULL;";
				versionCommand.ExecuteNonQuery();
			}
			if (userVersion < 2)
			{
				RemoveMalformedRows(sqliteConnection);
				MergeDuplicateSmartMatches(sqliteConnection);
				versionCommand.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_SmartMatch_UniqueItem ON SmartMatch(OrganizerType COLLATE NOCASE, ItemName COLLATE NOCASE);";
				versionCommand.ExecuteNonQuery();
			}
			if (userVersion < 3)
			{
				versionCommand.CommandText = "PRAGMA user_version = 3;";
				versionCommand.ExecuteNonQuery();
			}
		}
		using SqliteCommand sqliteCommand2 = sqliteConnection.CreateCommand();
		sqliteCommand2.CommandText = "PRAGMA quick_check;";
		string? text = Convert.ToString(sqliteCommand2.ExecuteScalar(), CultureInfo.InvariantCulture);
		if (!string.Equals(text, "ok", StringComparison.OrdinalIgnoreCase))
		{
			throw new SqliteException("SQLite quick_check failed: " + text, 11);
		}
	}

	private void RemoveMalformedRows(SqliteConnection connection)
	{
		var malformedFileRows = new List<long>();
		using (SqliteCommand command = connection.CreateCommand())
		{
			command.CommandText = "SELECT " + FileResultColumns + ", rowid FROM FileOrganizerResults;";
			using SqliteDataReader reader = command.ExecuteReader();
			while (reader.Read())
			{
				if (!TryReadFileResult(reader, out _))
				{
					malformedFileRows.Add(reader.GetInt64(15));
				}
			}
		}
		var malformedSmartMatchRows = new List<long>();
		using (SqliteCommand command = connection.CreateCommand())
		{
			command.CommandText = "SELECT Id, ItemName, DisplayName, OrganizerType, MatchStrings, rowid FROM SmartMatch;";
			using SqliteDataReader reader = command.ExecuteReader();
			while (reader.Read())
			{
				if (!TryReadSmartMatch(reader, out _))
				{
					malformedSmartMatchRows.Add(reader.GetInt64(5));
				}
			}
		}
		if (malformedFileRows.Count == 0 && malformedSmartMatchRows.Count == 0)
		{
			return;
		}
		using SqliteTransaction transaction = connection.BeginTransaction();
		DeleteRows(connection, transaction, "FileOrganizerResults", malformedFileRows);
		DeleteRows(connection, transaction, "SmartMatch", malformedSmartMatchRows);
		transaction.Commit();
		_logger.LogWarning("Removed {FileResultCount} malformed Auto Organize results and {SmartMatchCount} malformed smart matches", malformedFileRows.Count, malformedSmartMatchRows.Count);
	}

	private static void DeleteRows(SqliteConnection connection, SqliteTransaction transaction, string table, IEnumerable<long> rowIds)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = $"DELETE FROM {table} WHERE rowid = $RowId;";
		SqliteParameter parameter = command.Parameters.Add("$RowId", SqliteType.Integer);
		foreach (long rowId in rowIds)
		{
			parameter.Value = rowId;
			command.ExecuteNonQuery();
		}
	}

	private static bool ColumnExists(SqliteConnection connection, string table, string column)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "PRAGMA table_info(" + table + ");";
		using SqliteDataReader reader = command.ExecuteReader();
		while (reader.Read())
		{
			if (string.Equals(Convert.ToString(reader["name"], CultureInfo.InvariantCulture), column, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private void MergeDuplicateSmartMatches(SqliteConnection connection)
	{
		var matches = new List<SmartMatchResult>();
		using (SqliteCommand command = connection.CreateCommand())
		{
			command.CommandText = "SELECT Id, ItemName, DisplayName, OrganizerType, MatchStrings FROM SmartMatch ORDER BY Id;";
			using SqliteDataReader reader = command.ExecuteReader();
			while (reader.Read())
			{
				if (TryReadSmartMatch(reader, out SmartMatchResult? result))
				{
					matches.Add(result);
				}
			}
		}
		var duplicateGroups = matches
			.GroupBy(match => $"{match.OrganizerType}\0{match.ItemName}", StringComparer.OrdinalIgnoreCase)
			.Where(group => group.Count() > 1)
			.ToList();
		if (duplicateGroups.Count == 0)
		{
			return;
		}
		using SqliteTransaction transaction = connection.BeginTransaction();
		foreach (var group in duplicateGroups)
		{
			SmartMatchResult primary = group.First();
			foreach (string matchString in group.SelectMany(match => match.MatchStrings).Distinct(StringComparer.OrdinalIgnoreCase))
			{
				if (!primary.MatchStrings.Contains(matchString, StringComparer.OrdinalIgnoreCase))
				{
					primary.MatchStrings.Add(matchString);
				}
			}
			using (SqliteCommand updateCommand = connection.CreateCommand())
			{
				updateCommand.Transaction = transaction;
				updateCommand.CommandText = "UPDATE SmartMatch SET MatchStrings = $MatchStrings WHERE Id = $Id;";
				AddParameter(updateCommand, "$MatchStrings", JsonSerializer.Serialize(primary.MatchStrings));
				AddParameter(updateCommand, "$Id", primary.Id.ToByteArray());
				updateCommand.ExecuteNonQuery();
			}
			foreach (SmartMatchResult duplicate in group.Skip(1))
			{
				using SqliteCommand deleteCommand = connection.CreateCommand();
				deleteCommand.Transaction = transaction;
				deleteCommand.CommandText = "DELETE FROM SmartMatch WHERE Id = $Id;";
				AddParameter(deleteCommand, "$Id", duplicate.Id.ToByteArray());
				deleteCommand.ExecuteNonQuery();
			}
		}
		transaction.Commit();
		_logger.LogInformation("Merged {DuplicateGroupCount} duplicate Auto Organize smart-match groups", duplicateGroups.Count);
	}

	private SqliteConnection OpenConnection()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		SqliteConnection sqliteConnection = new SqliteConnection(_connectionString);
		sqliteConnection.Open();
		using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
		sqliteCommand.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
		sqliteCommand.ExecuteNonQuery();
		return sqliteConnection;
	}

	private void ExecuteWrite(Action<SqliteConnection, SqliteTransaction> action, CancellationToken cancellationToken)
	{
		_writeLock.Wait(cancellationToken);
		try
		{
			using SqliteConnection sqliteConnection = OpenConnection();
			using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
			action(sqliteConnection, sqliteTransaction);
			sqliteTransaction.Commit();
		}
		finally
		{
			_writeLock.Release();
		}
	}

	private async Task ExecuteWriteAsync(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> action, CancellationToken cancellationToken)
	{
		await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			using SqliteConnection connection = OpenConnection();
			using SqliteTransaction transaction = (SqliteTransaction)(await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false));
			await action(connection, transaction, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			_writeLock.Release();
		}
	}

	private bool TryReadFileResult(SqliteDataReader reader, [NotNullWhen(true)] out FileOrganizationResult? result)
	{
		try
		{
			result = ReadFileResult(reader);
			return true;
		}
		catch (Exception ex) when (((ex is ArgumentException || ex is FormatException || ex is InvalidCastException || ex is OverflowException) ? 1 : 0) != 0)
		{
			_logger.LogWarning(ex, "Skipping a malformed Auto Organize result row");
			result = null;
			return false;
		}
	}

	private bool TryReadSmartMatch(SqliteDataReader reader, [NotNullWhen(true)] out SmartMatchResult? result)
	{
		try
		{
			result = ReadSmartMatch(reader);
			return true;
		}
		catch (Exception ex) when (((ex is ArgumentException || ex is FormatException || ex is InvalidCastException || ex is OverflowException) ? 1 : 0) != 0)
		{
			_logger.LogWarning(ex, "Skipping a malformed Auto Organize smart-match row");
			result = null;
			return false;
		}
	}

	private FileOrganizationResult ReadFileResult(SqliteDataReader reader)
	{
		string? nullableString = GetNullableString(reader, 1);
		if (string.IsNullOrWhiteSpace(nullableString))
		{
			throw new FormatException("The organization result does not contain an original path.");
		}
		return new FileOrganizationResult
		{
			Id = ReadGuid(reader, 0).ToString("N", CultureInfo.InvariantCulture),
			OriginalPath = nullableString,
			OriginalFileName = (string.IsNullOrEmpty(nullableString) ? string.Empty : Path.GetFileName(nullableString)),
			TargetPath = GetNullableString(reader, 2),
			FileSize = (reader.IsDBNull(3) ? 0 : reader.GetInt64(3)),
			Date = ReadDateTime(reader, 4),
			Status = ParseEnum(reader, 5, FileSortingStatus.Failure),
			Type = ParseEnum(reader, 6, FileOrganizerType.Unknown),
			StatusMessage = GetNullableString(reader, 7),
			ExtractedName = GetNullableString(reader, 8),
			ExtractedYear = GetNullableInt32(reader, 9),
			ExtractedSeasonNumber = GetNullableInt32(reader, 10),
			ExtractedEpisodeNumber = GetNullableInt32(reader, 11),
			ExtractedEndingEpisodeNumber = GetNullableInt32(reader, 12),
			DuplicatePaths = ReadDuplicatePaths(GetNullableString(reader, 13)),
			BundleItems = ReadBundleItems(GetNullableString(reader, 14))
		};
	}

	private SmartMatchResult ReadSmartMatch(SqliteDataReader reader)
	{
		SmartMatchResult smartMatchResult = new SmartMatchResult
		{
			Id = ReadGuid(reader, 0),
			ItemName = (GetNullableString(reader, 1) ?? string.Empty),
			DisplayName = (GetNullableString(reader, 2) ?? string.Empty),
			OrganizerType = ParseEnum(reader, 3, FileOrganizerType.Unknown)
		};
		string? nullableString = GetNullableString(reader, 4);
		if (!string.IsNullOrWhiteSpace(nullableString))
		{
			try
			{
				List<string>? list = JsonSerializer.Deserialize<List<string>>(nullableString);
				if (list != null)
				{
					smartMatchResult.MatchStrings.AddRange(list.Where((string value) => !string.IsNullOrWhiteSpace(value)));
				}
			}
			catch (JsonException exception)
			{
				_logger.LogWarning(exception, "Ignoring malformed smart-match JSON for {SmartMatchId}", smartMatchResult.Id);
			}
		}
		return smartMatchResult;
	}

	private IReadOnlyList<string> ReadDuplicatePaths(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return Array.Empty<string>();
		}
		if (value.TrimStart().StartsWith('['))
		{
			try
			{
				return JsonSerializer.Deserialize<List<string>>(value) ?? new List<string>();
			}
			catch (JsonException exception)
			{
				_logger.LogWarning(exception, "Falling back to legacy duplicate-path parsing");
			}
		}
		return value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
	}

	private IReadOnlyList<FileOrganizationBundleItem> ReadBundleItems(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return Array.Empty<FileOrganizationBundleItem>();
		}
		try
		{
			return JsonSerializer.Deserialize<List<FileOrganizationBundleItem>>(value)?
				.Where(item => item != null && !string.IsNullOrWhiteSpace(item.SourcePath))
				.ToList()
				?? new List<FileOrganizationBundleItem>();
		}
		catch (JsonException exception)
		{
			_logger.LogWarning(exception, "Ignoring malformed bundle-item JSON");
			return Array.Empty<FileOrganizationBundleItem>();
		}
	}

	private string BackupCorruptDatabase()
	{
		string text = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
		string text2 = _databasePath + ".corrupt-" + text + "-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
		SqliteConnection.ClearAllPools();
		if (File.Exists(_databasePath))
		{
			File.Move(_databasePath, text2);
		}
		MoveIfExists(_databasePath + "-wal", text2 + "-wal");
		MoveIfExists(_databasePath + "-shm", text2 + "-shm");
		return text2;
	}

	private static void MoveIfExists(string source, string destination)
	{
		if (File.Exists(source))
		{
			File.Move(source, destination);
		}
	}

	private static Guid ParseGuid(string value, string parameterName)
	{
		if (!Guid.TryParse(value, out var result))
		{
			throw new ArgumentException("The value must be a valid GUID.", parameterName);
		}
		return result;
	}

	private static (int Offset, int Limit) ValidatePaging(FileOrganizationResultQuery query)
	{
		int valueOrDefault = query.StartIndex.GetValueOrDefault();
		int num = query.Limit ?? (-1);
		if (valueOrDefault < 0)
		{
			throw new ArgumentOutOfRangeException("query", "StartIndex cannot be negative.");
		}
		if (num == 0 || num < -1)
		{
			throw new ArgumentOutOfRangeException("query", "Limit must be positive when specified.");
		}
		return (Offset: valueOrDefault, Limit: num);
	}

	private static int CountFileResults(SqliteConnection connection)
	{
		using SqliteCommand sqliteCommand = connection.CreateCommand();
		sqliteCommand.CommandText = "SELECT COUNT(*) FROM FileOrganizerResults;";
		return Convert.ToInt32(sqliteCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
	}

	private static int CountSmartMatches(SqliteConnection connection)
	{
		using SqliteCommand sqliteCommand = connection.CreateCommand();
		sqliteCommand.CommandText = "SELECT COUNT(*) FROM SmartMatch;";
		return Convert.ToInt32(sqliteCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
	}

	private static string FormatDateTime(DateTime value)
	{
		return ((value.Kind == DateTimeKind.Unspecified) ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime()).ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture);
	}

	private static Guid ReadGuid(SqliteDataReader reader, int ordinal)
	{
		object value = reader.GetValue(ordinal);
		if (value is byte[] array && array.Length == 16)
		{
			return new Guid(array);
		}
		return Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
	}

	private static DateTime ReadDateTime(SqliteDataReader reader, int ordinal)
	{
		if (reader.IsDBNull(ordinal))
		{
			return DateTime.UnixEpoch;
		}
		object value = reader.GetValue(ordinal);
		if (value is DateTime value2)
		{
			if (value2.Kind != DateTimeKind.Unspecified)
			{
				return value2.ToUniversalTime();
			}
			return DateTime.SpecifyKind(value2, DateTimeKind.Utc);
		}
		if (value is DateTimeOffset dateTimeOffset)
		{
			return dateTimeOffset.UtcDateTime;
		}
		if (value is long value3)
		{
			return ReadIntegerDateTime(value3);
		}
		string? s = Convert.ToString(value, CultureInfo.InvariantCulture);
		if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			return ReadIntegerDateTime(result);
		}
		if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var result2))
		{
			return DateTime.UnixEpoch;
		}
		return result2;
	}

	private static DateTime ReadIntegerDateTime(long value)
	{
		if (value >= DateTime.UnixEpoch.Ticks && value <= DateTime.MaxValue.Ticks)
		{
			return new DateTime(value, DateTimeKind.Utc);
		}
		try
		{
			if (value >= -62135596800L && value <= 253402300799L)
			{
				return DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime;
			}
			if (value >= -62135596800000L && value <= 253402300799999L)
			{
				return DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;
			}
		}
		catch (ArgumentOutOfRangeException)
		{
		}
		return DateTime.UnixEpoch;
	}

	private static TEnum ParseEnum<TEnum>(SqliteDataReader reader, int ordinal, TEnum fallback) where TEnum : struct, Enum
	{
		string? nullableString = GetNullableString(reader, ordinal);
		if (nullableString == null || !Enum.TryParse<TEnum>(nullableString, ignoreCase: true, out var result))
		{
			return fallback;
		}
		return result;
	}

	private static int? GetNullableInt32(SqliteDataReader reader, int ordinal)
	{
		if (!reader.IsDBNull(ordinal))
		{
			return Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
		}
		return null;
	}

	private static string? GetNullableString(SqliteDataReader reader, int ordinal)
	{
		if (!reader.IsDBNull(ordinal))
		{
			return Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
		}
		return null;
	}

	private static void AddParameter(SqliteCommand command, string name, object? value)
	{
		command.Parameters.AddWithValue(name, value ?? DBNull.Value);
	}
}
