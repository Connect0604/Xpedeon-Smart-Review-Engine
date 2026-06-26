using Microsoft.Data.SqlClient;
using MigrationDashboard.Web.Models;

namespace MigrationDashboard.Web.Services;

public sealed class SqlMigrationDashboardRepository(IConfiguration configuration) : IMigrationDashboardRepository
{
    private const string ConnectionStringName = "MigrationDashboard";
    private readonly string _connectionString = configuration.GetConnectionString(ConnectionStringName)
        ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

    public async Task<EditorAppUser?> GetActiveEditorAsync(string userName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT UserId, UserName, PasswordHash, IsActive, CreatedDate, LastLoginDate, LastLogoutDate
            FROM MIG.APP_USER
            WHERE UserName = @UserName
              AND IsActive = 1;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@UserName", userName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new EditorAppUser(
            reader.GetInt32(reader.GetOrdinal("UserId")),
            reader.GetString(reader.GetOrdinal("UserName")),
            reader.GetString(reader.GetOrdinal("PasswordHash")),
            reader.GetBoolean(reader.GetOrdinal("IsActive")),
            reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
            reader.IsDBNull(reader.GetOrdinal("LastLoginDate")) ? null : reader.GetDateTime(reader.GetOrdinal("LastLoginDate")),
            reader.IsDBNull(reader.GetOrdinal("LastLogoutDate")) ? null : reader.GetDateTime(reader.GetOrdinal("LastLogoutDate")));
    }

    public async Task RecordLoginAsync(int userId, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE MIG.APP_USER
            SET LastLoginDate = SYSUTCDATETIME()
            WHERE UserId = @UserId;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@UserId", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordLogoutAsync(string userName, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE MIG.APP_USER
            SET LastLogoutDate = SYSUTCDATETIME()
            WHERE UserName = @UserName;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@UserName", userName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordDisconnectAsync(string userName, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE MIG.APP_USER
            SET LastLogoutDate = SYSUTCDATETIME()
            WHERE UserName = @UserName;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@UserName", userName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MigrationFormListItem>> GetFormsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT FormId, FormName, ProcessCode, StepCode, HandoffDate, Status
            FROM MIG.FORM
            ORDER BY FormName;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<MigrationFormListItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new MigrationFormListItem(
                reader.GetInt32(reader.GetOrdinal("FormId")),
                reader.GetString(reader.GetOrdinal("FormName")),
                reader.IsDBNull(reader.GetOrdinal("ProcessCode")) ? null : reader.GetString(reader.GetOrdinal("ProcessCode")),
                reader.IsDBNull(reader.GetOrdinal("StepCode")) ? null : reader.GetString(reader.GetOrdinal("StepCode")),
                reader.IsDBNull(reader.GetOrdinal("HandoffDate"))
                    ? null
                    : DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("HandoffDate"))),
                reader.GetString(reader.GetOrdinal("Status"))));
        }

        return results;
    }

    public async Task<IReadOnlySet<int>> SearchFormIdsByObjectAsync(string searchTerm, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DISTINCT FormId
            FROM MIG.OBJECT_OWNERSHIP
            WHERE ObjectName LIKE @SearchPattern
               OR ObjectType LIKE @SearchPattern
               OR Layer LIKE @SearchPattern
               OR OwnershipCategory LIKE @SearchPattern
               OR ISNULL(Remarks, '') LIKE @SearchPattern;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@SearchPattern", BuildLikePattern(searchTerm));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new HashSet<int>();

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetInt32(reader.GetOrdinal("FormId")));
        }

        return results;
    }

    public async Task<MigrationFormDetail?> GetFormDetailAsync(int formId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT FormId, FormName, ProcessCode, StepCode, HandoffDate, Remarks, Status, ownership_updated
            FROM MIG.FORM
            WHERE FormId = @FormId;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@FormId", formId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MigrationFormDetail(
            reader.GetInt32(reader.GetOrdinal("FormId")),
            reader.GetString(reader.GetOrdinal("FormName")),
            reader.IsDBNull(reader.GetOrdinal("ProcessCode")) ? null : reader.GetString(reader.GetOrdinal("ProcessCode")),
            reader.IsDBNull(reader.GetOrdinal("StepCode")) ? null : reader.GetString(reader.GetOrdinal("StepCode")),
            reader.IsDBNull(reader.GetOrdinal("HandoffDate"))
                ? null
                : DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("HandoffDate"))),
            reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
            reader.GetString(reader.GetOrdinal("Status")),
            reader.IsDBNull(reader.GetOrdinal("ownership_updated")) ? null : reader.GetString(reader.GetOrdinal("ownership_updated")));
    }

    public async Task<IReadOnlyList<OwnershipObjectRow>> GetOwnershipRowsAsync(int formId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT Layer, ObjectName, ObjectType, FormId, OwnershipCategory, Remarks, CreatedBy, CreatedDate, ModifiedBy, ModifiedDate
            FROM MIG.OBJECT_OWNERSHIP
            WHERE FormId = @FormId
            ORDER BY Layer, ObjectName;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@FormId", formId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<OwnershipObjectRow>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var key = new OwnershipObjectKey(
                reader.GetInt32(reader.GetOrdinal("FormId")),
                reader.GetString(reader.GetOrdinal("Layer")),
                reader.GetString(reader.GetOrdinal("ObjectName")),
                reader.GetString(reader.GetOrdinal("ObjectType")));

            results.Add(new OwnershipObjectRow(
                key,
                key.FormId,
                key.Layer,
                key.ObjectName,
                key.ObjectType,
                reader.GetString(reader.GetOrdinal("OwnershipCategory")),
                reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                reader.IsDBNull(reader.GetOrdinal("CreatedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy")),
                reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate"))));
        }

        return results;
    }

    public async Task<IReadOnlyList<ChangeEventRow>> GetChangeEventsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EventId, BuildId, BuildNumber, CommitId, BranchName, ChangedBy, EventTimestamp, FormsAffected, ObjectsAffected, AlertSentTo
            FROM MIG.CHANGE_EVENT
            ORDER BY EventTimestamp DESC, EventId DESC;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<ChangeEventRow>();

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ChangeEventRow(
                reader.GetInt32(reader.GetOrdinal("EventId")),
                reader.IsDBNull(reader.GetOrdinal("BuildId")) ? null : reader.GetString(reader.GetOrdinal("BuildId")),
                reader.IsDBNull(reader.GetOrdinal("BuildNumber")) ? null : reader.GetString(reader.GetOrdinal("BuildNumber")),
                reader.IsDBNull(reader.GetOrdinal("CommitId")) ? null : reader.GetString(reader.GetOrdinal("CommitId")),
                reader.IsDBNull(reader.GetOrdinal("BranchName")) ? null : reader.GetString(reader.GetOrdinal("BranchName")),
                reader.IsDBNull(reader.GetOrdinal("ChangedBy")) ? null : reader.GetString(reader.GetOrdinal("ChangedBy")),
                reader.GetDateTime(reader.GetOrdinal("EventTimestamp")),
                reader.GetInt32(reader.GetOrdinal("FormsAffected")),
                reader.GetInt32(reader.GetOrdinal("ObjectsAffected")),
                reader.IsDBNull(reader.GetOrdinal("AlertSentTo")) ? null : reader.GetString(reader.GetOrdinal("AlertSentTo"))));
        }

        return results;
    }

    public async Task<IReadOnlyList<ChangeEventDetailRow>> GetChangeEventDetailsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DetailId, EventId, FormId, ObjectName, ObjectOwnershipId, ChangedFilePath, OwnershipCategory, Layer, ObjectType, ReviewStatus, EventTimestamp, ModifiedBy, ModifiedDate, Remarks
            FROM MIG.CHANGE_EVENT_DETAIL
            ORDER BY EventTimestamp DESC, DetailId DESC;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<ChangeEventDetailRow>();

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ChangeEventDetailRow(
                reader.GetInt32(reader.GetOrdinal("DetailId")),
                reader.GetInt32(reader.GetOrdinal("EventId")),
                reader.GetInt32(reader.GetOrdinal("FormId")),
                reader.GetString(reader.GetOrdinal("ObjectName")),
                reader.IsDBNull(reader.GetOrdinal("ObjectOwnershipId")) ? null : reader.GetInt32(reader.GetOrdinal("ObjectOwnershipId")),
                reader.GetString(reader.GetOrdinal("ChangedFilePath")),
                reader.GetString(reader.GetOrdinal("OwnershipCategory")),
                reader.IsDBNull(reader.GetOrdinal("Layer")) ? null : reader.GetString(reader.GetOrdinal("Layer")),
                reader.IsDBNull(reader.GetOrdinal("ObjectType")) ? null : reader.GetString(reader.GetOrdinal("ObjectType")),
                reader.GetString(reader.GetOrdinal("ReviewStatus")),
                reader.GetDateTime(reader.GetOrdinal("EventTimestamp")),
                reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy")),
                reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks"))));
        }

        return results;
    }

    public async Task<IReadOnlyList<AuditLogRow>> GetAuditLogsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            WITH AuditSource AS (
                SELECT
                    a.AuditId,
                    a.EntityName,
                    a.EntityKey,
                    a.ActionName,
                    a.FieldName,
                    a.OldValue,
                    a.NewValue,
                    a.ChangedBy,
                    a.ChangedDate,
                    CASE
                        WHEN a.EntityName = 'OBJECT_OWNERSHIP'
                            THEN TRY_CONVERT(int, SUBSTRING(
                                a.EntityKey,
                                CHARINDEX('FormId=', a.EntityKey) + LEN('FormId='),
                                CHARINDEX('|', a.EntityKey + '|', CHARINDEX('FormId=', a.EntityKey)) - (CHARINDEX('FormId=', a.EntityKey) + LEN('FormId='))))
                        ELSE NULL
                    END AS OwnershipFormId,
                    CASE
                        WHEN a.EntityName = 'CHANGE_EVENT_DETAIL'
                            THEN TRY_CONVERT(int, SUBSTRING(
                                a.EntityKey,
                                CHARINDEX('DetailId=', a.EntityKey) + LEN('DetailId='),
                                LEN(a.EntityKey)))
                        ELSE NULL
                    END AS ChangeEventDetailId
                FROM MIG.APP_AUDIT a
            )
            SELECT
                src.AuditId,
                src.EntityName,
                src.EntityKey,
                src.ActionName,
                src.FieldName,
                src.OldValue,
                src.NewValue,
                src.ChangedBy,
                src.ChangedDate,
                form.FormId,
                form.FormName,
                form.ProcessCode,
                form.StepCode
            FROM AuditSource src
            LEFT JOIN MIG.CHANGE_EVENT_DETAIL detail
                ON src.EntityName = 'CHANGE_EVENT_DETAIL'
               AND detail.DetailId = src.ChangeEventDetailId
            LEFT JOIN MIG.FORM form
                ON form.FormId = COALESCE(src.OwnershipFormId, detail.FormId)
            ORDER BY src.ChangedDate DESC, src.AuditId DESC;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<AuditLogRow>();

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AuditLogRow(
                reader.GetInt64(reader.GetOrdinal("AuditId")),
                reader.GetString(reader.GetOrdinal("EntityName")),
                reader.GetString(reader.GetOrdinal("EntityKey")),
                reader.GetString(reader.GetOrdinal("ActionName")),
                reader.IsDBNull(reader.GetOrdinal("FieldName")) ? null : reader.GetString(reader.GetOrdinal("FieldName")),
                reader.IsDBNull(reader.GetOrdinal("OldValue")) ? null : reader.GetString(reader.GetOrdinal("OldValue")),
                reader.IsDBNull(reader.GetOrdinal("NewValue")) ? null : reader.GetString(reader.GetOrdinal("NewValue")),
                reader.GetString(reader.GetOrdinal("ChangedBy")),
                reader.GetDateTime(reader.GetOrdinal("ChangedDate")),
                reader.IsDBNull(reader.GetOrdinal("FormId")) ? null : reader.GetInt32(reader.GetOrdinal("FormId")),
                reader.IsDBNull(reader.GetOrdinal("FormName")) ? null : reader.GetString(reader.GetOrdinal("FormName")),
                reader.IsDBNull(reader.GetOrdinal("ProcessCode")) ? null : reader.GetString(reader.GetOrdinal("ProcessCode")),
                reader.IsDBNull(reader.GetOrdinal("StepCode")) ? null : reader.GetString(reader.GetOrdinal("StepCode"))));
        }

        return results;
    }

    public async Task UpdateOwnershipAsync(UpdateOwnershipRowRequest request, string modifiedBy, CancellationToken cancellationToken)
    {
        const string selectSql = """
            SELECT OwnershipCategory, Remarks
            FROM MIG.OBJECT_OWNERSHIP
            WHERE FormId = @FormId
              AND Layer = @Layer
              AND ObjectName = @ObjectName
              AND ObjectType = @ObjectType;
            """;

        const string updateSql = """
            UPDATE MIG.OBJECT_OWNERSHIP
            SET OwnershipCategory = @OwnershipCategory,
                Remarks = @Remarks,
                ModifiedBy = @ModifiedBy,
                ModifiedDate = SYSUTCDATETIME()
            WHERE FormId = @FormId
              AND Layer = @Layer
              AND ObjectName = @ObjectName
              AND ObjectType = @ObjectType;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var selectCommand = BuildOwnershipKeyCommand(selectSql, connection);
        selectCommand.Parameters.AddWithValue("@FormId", request.Key.FormId);
        selectCommand.Parameters.AddWithValue("@Layer", request.Key.Layer);
        selectCommand.Parameters.AddWithValue("@ObjectName", request.Key.ObjectName);
        selectCommand.Parameters.AddWithValue("@ObjectType", request.Key.ObjectType);

        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Ownership row could not be updated because it was not found.");
        }

        var existingOwnership = reader.GetString(reader.GetOrdinal("OwnershipCategory"));
        var existingRemarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks"));
        await reader.CloseAsync();

        var auditEntries = new List<AuditEntry>();
        if (!string.Equals(existingOwnership, request.OwnershipCategory, StringComparison.Ordinal))
        {
            auditEntries.Add(new AuditEntry(
                "OBJECT_OWNERSHIP",
                BuildOwnershipEntityKey(request.Key),
                "UPDATE",
                "OwnershipCategory",
                existingOwnership,
                request.OwnershipCategory,
                modifiedBy));
        }

        if (!string.Equals(existingRemarks, request.Remarks, StringComparison.Ordinal))
        {
            auditEntries.Add(new AuditEntry(
                "OBJECT_OWNERSHIP",
                BuildOwnershipEntityKey(request.Key),
                "UPDATE",
                "Remarks",
                existingRemarks,
                request.Remarks,
                modifiedBy));
        }

        if (auditEntries.Count == 0)
        {
            return;
        }

        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var updateCommand = BuildOwnershipKeyCommand(updateSql, connection, transaction);
        updateCommand.Parameters.AddWithValue("@OwnershipCategory", request.OwnershipCategory);
        updateCommand.Parameters.AddWithValue("@Remarks", (object?)request.Remarks ?? DBNull.Value);
        updateCommand.Parameters.AddWithValue("@ModifiedBy", modifiedBy);
        updateCommand.Parameters.AddWithValue("@FormId", request.Key.FormId);
        updateCommand.Parameters.AddWithValue("@Layer", request.Key.Layer);
        updateCommand.Parameters.AddWithValue("@ObjectName", request.Key.ObjectName);
        updateCommand.Parameters.AddWithValue("@ObjectType", request.Key.ObjectType);

        var affectedRows = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            throw new InvalidOperationException("Ownership row could not be updated because it was not found.");
        }

        await InsertAuditEntriesAsync(connection, transaction, auditEntries, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateReviewStatusAsync(UpdateReviewStatusRequest request, string modifiedBy, CancellationToken cancellationToken)
    {
        const string selectSql = """
            SELECT ReviewStatus, Remarks
            FROM MIG.CHANGE_EVENT_DETAIL
            WHERE DetailId = @DetailId;
            """;

        const string updateSql = """
            UPDATE MIG.CHANGE_EVENT_DETAIL
            SET ReviewStatus = @ReviewStatus,
                Remarks = @Remarks,
                ModifiedBy = @ModifiedBy,
                ModifiedDate = SYSUTCDATETIME()
            WHERE DetailId = @DetailId;
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var selectCommand = new SqlCommand(selectSql, connection);
        selectCommand.Parameters.AddWithValue("@DetailId", request.DetailId);
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Event detail row could not be updated because it was not found.");
        }

        var existingStatus = reader.GetString(reader.GetOrdinal("ReviewStatus"));
        var existingRemarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks"));
        await reader.CloseAsync();

        var auditEntries = new List<AuditEntry>();
        if (!string.Equals(existingStatus, request.ReviewStatus, StringComparison.Ordinal))
        {
            auditEntries.Add(new AuditEntry(
                "CHANGE_EVENT_DETAIL",
                $"DetailId={request.DetailId}",
                "UPDATE",
                "ReviewStatus",
                existingStatus,
                request.ReviewStatus,
                modifiedBy));
        }

        if (!string.Equals(existingRemarks, request.Remarks, StringComparison.Ordinal))
        {
            auditEntries.Add(new AuditEntry(
                "CHANGE_EVENT_DETAIL",
                $"DetailId={request.DetailId}",
                "UPDATE",
                "Remarks",
                existingRemarks,
                request.Remarks,
                modifiedBy));
        }

        if (auditEntries.Count == 0)
        {
            return;
        }

        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var command = new SqlCommand(updateSql, connection, transaction);
        command.Parameters.AddWithValue("@ReviewStatus", request.ReviewStatus);
        command.Parameters.AddWithValue("@Remarks", (object?)request.Remarks ?? DBNull.Value);
        command.Parameters.AddWithValue("@ModifiedBy", modifiedBy);
        command.Parameters.AddWithValue("@DetailId", request.DetailId);

        var affectedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affectedRows == 0)
        {
            throw new InvalidOperationException("Event detail row could not be updated because it was not found.");
        }

        await InsertAuditEntriesAsync(connection, transaction, auditEntries, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static string BuildLikePattern(string searchTerm)
    {
        return $"%{searchTerm.Trim()}%";
    }

    private static string BuildOwnershipEntityKey(OwnershipObjectKey key)
    {
        return $"FormId={key.FormId}|Layer={key.Layer}|ObjectName={key.ObjectName}|ObjectType={key.ObjectType}";
    }

    private static SqlCommand BuildOwnershipKeyCommand(string sql, SqlConnection connection, SqlTransaction? transaction = null)
    {
        return transaction is null
            ? new SqlCommand(sql, connection)
            : new SqlCommand(sql, connection, transaction);
    }

    private static async Task InsertAuditEntriesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<AuditEntry> auditEntries,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO MIG.APP_AUDIT (EntityName, EntityKey, ActionName, FieldName, OldValue, NewValue, ChangedBy, ChangedDate, SessionId)
            VALUES (@EntityName, @EntityKey, @ActionName, @FieldName, @OldValue, @NewValue, @ChangedBy, SYSUTCDATETIME(), @SessionId);
            """;

        foreach (var entry in auditEntries)
        {
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@EntityName", entry.EntityName);
            command.Parameters.AddWithValue("@EntityKey", entry.EntityKey);
            command.Parameters.AddWithValue("@ActionName", entry.ActionName);
            command.Parameters.AddWithValue("@FieldName", (object?)entry.FieldName ?? DBNull.Value);
            command.Parameters.AddWithValue("@OldValue", (object?)entry.OldValue ?? DBNull.Value);
            command.Parameters.AddWithValue("@NewValue", (object?)entry.NewValue ?? DBNull.Value);
            command.Parameters.AddWithValue("@ChangedBy", entry.ChangedBy);
            command.Parameters.AddWithValue("@SessionId", DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private sealed record AuditEntry(
        string EntityName,
        string EntityKey,
        string ActionName,
        string? FieldName,
        string? OldValue,
        string? NewValue,
        string ChangedBy);
}
