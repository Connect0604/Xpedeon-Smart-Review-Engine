using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using SmartReviewSystem.Models.Ai;

namespace SmartReviewSystem.Services.Orchestration;

public sealed class PocoDbComparisonService : IPocoDbComparisonService
{
    private readonly HttpClient _http;
    private readonly List<SectionPromptConfig> _sectionPrompts;

    public PocoDbComparisonService(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        _sectionPrompts = configuration
            .GetSection("Validation:MasterPrompt:Sections")
            .Get<List<SectionPromptConfig>>() ?? new List<SectionPromptConfig>();
    }

    public bool CanHandle(string heading, SectionPromptStep step) =>
        heading.Contains("Database / EF Entities", StringComparison.OrdinalIgnoreCase) &&
        step.Label.Equals("POCO vs DB Table Check", StringComparison.OrdinalIgnoreCase);

    public async Task<string> ExecuteAsync(string heading, string content, CancellationToken ct = default)
    {
        var section = _sectionPrompts.FirstOrDefault(s =>
            heading.Contains(s.Name, StringComparison.OrdinalIgnoreCase));
        if (section?.RemoteMCPServer is null || string.IsNullOrWhiteSpace(section.Tool))
        {
            return BuildErrorJson("ERROR_DB_SCHEMA_FETCH", "RemoteMCPServer/Tool is not configured for this section.");
        }

        var entities = ExtractPocoEntities(content);
        if (entities.Count == 0)
        {
            return BuildErrorJson("SKIPPED_NO_TABLE_ATTRIBUTE", "No [Table(\"...\")] attribute found in this section content.");
        }

        var rows = new List<ComparisonRow>();
        foreach (var entity in entities)
        {
            if (entity.Fields.Count == 0)
            {
                rows.Add(BuildErrorRow(entity.FileName, entity.TableName, "ERROR_POCO_PARSE", "No POCO properties could be parsed for this class."));
                continue;
            }

            var schemaResult = await FetchTableDefinitionAsync(section.RemoteMCPServer.Url, section.Tool!, entity.TableName, ct);
            if (!schemaResult.Success)
            {
                rows.Add(BuildErrorRow(entity.FileName, entity.TableName, "ERROR_DB_SCHEMA_FETCH", schemaResult.Error ?? "Unknown schema fetch error."));
                continue;
            }

            var dbColumns = ExtractDbColumns(schemaResult.PayloadText);
            if (dbColumns.Count == 0)
            {
                var snippet = (schemaResult.PayloadText ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
                if (snippet.Length > 500)
                {
                    snippet = snippet[..500] + "...";
                }
                rows.Add(BuildErrorRow(entity.FileName, entity.TableName, "ERROR_DB_SCHEMA_FETCH", $"Schema returned, but no database columns were parsed. payload_snippet={snippet}"));
                continue;
            }

            rows.AddRange(BuildComparisonRows(entity.TableName, entity.FileName, dbColumns, entity.Fields));
        }

        var payload = new
        {
            comparison_table = rows.Select(r => new Dictionary<string, string>
            {
                ["file_name"] = r.FileName,
                ["table_name"] = r.TableName,
                ["db_field_name"] = r.DbFieldName,
                ["db_data_type"] = r.DbDataType,
                ["db_data_length"] = r.DbDataLength,
                ["poco_property_name"] = r.PocoPropertyName,
                ["poco_mapped_field_name"] = r.PocoMappedFieldName,
                ["poco_data_type"] = r.PocoDataType,
                ["poco_data_length"] = r.PocoDataLength,
                ["in_db"] = r.InDb ? "true" : "false",
                ["in_poco"] = r.InPoco ? "true" : "false",
                ["status"] = r.Status,
                ["actual_reason"] = r.ActualReason,
                ["rectification_prompt"] = r.RectificationPrompt,
                ["error_message"] = r.ErrorMessage
            }).ToList()
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string BuildErrorJson(string status, string errorMessage, string tableName = "-")
    {
        var payload = new
        {
            comparison_table = new[]
            {
                new Dictionary<string, string>
                {
                    ["table_name"] = tableName,
                    ["file_name"] = "-",
                    ["db_field_name"] = "-",
                    ["db_data_type"] = "-",
                    ["db_data_length"] = "-",
                    ["poco_property_name"] = "-",
                    ["poco_mapped_field_name"] = "-",
                    ["poco_data_type"] = "-",
                    ["poco_data_length"] = "-",
                    ["in_db"] = "false",
                    ["in_poco"] = "false",
                    ["status"] = status,
                    ["error_message"] = errorMessage
                }
            }
        };
        return JsonSerializer.Serialize(payload);
    }

    private static ComparisonRow BuildErrorRow(string fileName, string tableName, string status, string errorMessage) =>
        new(fileName, tableName, "-", "-", "-", "-", "-", "-", "-", false, false, status, errorMessage, "Validation runtime error", "Fix runtime/configuration issue and rerun comparison.");

    private static List<PocoField> ExtractPocoFields(string content)
    {
        var result = new List<PocoField>();
        var lines = content.Replace("\r\n", "\n").Split('\n');
        string? pendingColumn = null;
        string? pendingTypeName = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var columnMatch = Regex.Match(line, @"^\[Column\(""(?<col>[^""]+)""", RegexOptions.IgnoreCase);
            if (columnMatch.Success)
            {
                pendingColumn = columnMatch.Groups["col"].Value.Trim();
                var typeNameMatch = Regex.Match(line, @"TypeName\s*=\s*""(?<type>[^""]+)""", RegexOptions.IgnoreCase);
                pendingTypeName = typeNameMatch.Success ? typeNameMatch.Groups["type"].Value.Trim() : null;
                continue;
            }

            var propertyMatch = Regex.Match(line, @"^public\s+(?<type>[^\s]+)\s+(?<prop>[A-Za-z_][A-Za-z0-9_]*)\s*\{", RegexOptions.IgnoreCase);
            if (!propertyMatch.Success)
            {
                continue;
            }

            var propName = propertyMatch.Groups["prop"].Value.Trim();
            var propType = propertyMatch.Groups["type"].Value.Trim();
            var mapped = string.IsNullOrWhiteSpace(pendingColumn) ? propName : pendingColumn;
            var pocoType = !string.IsNullOrWhiteSpace(pendingTypeName) ? pendingTypeName! : MapClrTypeToDbLikeType(propType);
            var (dt, dl) = ParseTypeAndLength(pocoType);
            result.Add(new PocoField(propName, mapped, dt, dl));
            pendingColumn = null;
            pendingTypeName = null;
        }

        return result;
    }

    private static List<PocoEntity> ExtractPocoEntities(string content)
    {
        var entities = new List<PocoEntity>();
        var normalized = content.Replace("\r\n", "\n");

        var classPattern = new Regex(@"\[Table\(""(?<table>[^""]+)""\)\][\s\S]*?public\s+class\s+(?<class>[A-Za-z_][A-Za-z0-9_]*)\s*\{", RegexOptions.IgnoreCase);
        var matches = classPattern.Matches(normalized);
        if (matches.Count == 0)
        {
            return entities;
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : normalized.Length;
            var block = normalized[start..end];
            var tableName = matches[i].Groups["table"].Value.Trim();
            var className = matches[i].Groups["class"].Value.Trim();
            var fields = ExtractPocoFields(block);
            entities.Add(new PocoEntity(tableName, className + ".cs", fields));
        }

        return entities;
    }

    private async Task<(bool Success, string PayloadText, string? Error)> FetchTableDefinitionAsync(
        string mcpUrl,
        string toolName,
        string tableName,
        CancellationToken ct)
    {
        try
        {
            var callPayload = new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new
                {
                    name = toolName,
                    arguments = new
                    {
                        operation = "table-definition",
                        objectName = tableName
                    }
                }
            };

            var callReq = new HttpRequestMessage(HttpMethod.Post, mcpUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(callPayload), Encoding.UTF8, "application/json")
            };

            using var response = await _http.SendAsync(callReq, ct);
            var responseText = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return (false, string.Empty, $"MCP HTTP {(int)response.StatusCode}: {responseText}");
            }

            var extracted = ExtractToolText(responseText);
            return string.IsNullOrWhiteSpace(extracted)
                ? (false, string.Empty, "MCP response did not contain tool output text.")
                : (true, extracted, null);
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }

    private static string ExtractToolText(string rawResponse)
    {
        var normalized = rawResponse?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        // Handle SSE-like wrappers: "event: ...\ndata: {...json...}"
        var dataIndex = normalized.IndexOf("data:", StringComparison.OrdinalIgnoreCase);
        if (dataIndex >= 0)
        {
            var afterData = normalized[(dataIndex + "data:".Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(afterData))
            {
                normalized = afterData;
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(normalized);
            var root = doc.RootElement;

            if (root.TryGetProperty("result", out var result))
            {
                if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    var chunks = new List<string>();
                    foreach (var item in content.EnumerateArray())
                    {
                        if (item.TryGetProperty("text", out var text))
                        {
                            var s = text.GetString();
                            if (!string.IsNullOrWhiteSpace(s))
                            {
                                chunks.Add(s!);
                            }
                        }
                    }

                    if (chunks.Count > 0)
                    {
                        return string.Join("\n", chunks);
                    }
                }

                if (result.TryGetProperty("structuredContent", out var structured))
                {
                    return structured.ToString();
                }
            }

            return root.ToString();
        }
        catch
        {
            return normalized;
        }
    }

    private static Dictionary<string, DbField> ExtractDbColumns(string schemaText)
    {
        var set = new Dictionary<string, DbField>(StringComparer.OrdinalIgnoreCase);

        // 1) Try JSON-first extraction for MCP structured payloads.
        TryExtractDbColumnsFromJson(schemaText, set);
        if (set.Count > 0)
        {
            return set;
        }

        var lines = schemaText.Replace("\r\n", "\n").Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var upper = line.ToUpperInvariant();
            if (upper.StartsWith("CONSTRAINT ") || upper.StartsWith("PRIMARY KEY") ||
                upper.StartsWith("FOREIGN KEY") || upper.StartsWith("UNIQUE ") ||
                upper.StartsWith("CHECK ") || upper.StartsWith("INDEX "))
            {
                continue;
            }
            if (upper.Contains(" ASC", StringComparison.Ordinal) ||
                upper.Contains(" DESC", StringComparison.Ordinal))
            {
                // Ignore key-order lines from index/constraint declarations.
                continue;
            }

            // Supports:
            // [COL] [nvarchar](30)
            // [COL][nvarchar](30)
            // [COL] nvarchar(30)
            var bracketed = Regex.Match(line, @"^\[(?<name>[^\]]+)\]\s*(?<rest>.*)$");
            if (bracketed.Success)
            {
                var name = bracketed.Groups["name"].Value.Trim();
                var rest = bracketed.Groups["rest"].Value.Trim();
                var (dt, dl) = ParseDbTypeFromRemainder(rest);
                set[name] = new DbField(name, dt, dl);
                continue;
            }

            var plain = Regex.Match(line, @"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s+(varchar|nvarchar|char|nchar|text|ntext|int|bigint|smallint|tinyint|bit|decimal|numeric|float|real|money|smallmoney|datetime|datetime2|smalldatetime|date|time|uniqueidentifier|binary|varbinary)\b", RegexOptions.IgnoreCase);
            if (plain.Success)
            {
                var name = plain.Groups["name"].Value.Trim();
                var rest = line[(plain.Groups["name"].Length)..].Trim();
                var (dt, dl) = ParseDbTypeFromRemainder(rest);
                set[name] = new DbField(name, dt, dl);
                continue;
            }

            // Markdown table row fallback: | ColumnName | DataType | ...
            if (line.StartsWith("|", StringComparison.Ordinal) && line.Count(c => c == '|') >= 2)
            {
                var cells = line.Trim('|').Split('|', StringSplitOptions.TrimEntries);
                if (cells.Length > 0)
                {
                    var first = cells[0].Trim();
                    if (!string.IsNullOrWhiteSpace(first) &&
                        !first.Contains('-', StringComparison.Ordinal) &&
                        !first.Equals("Column", StringComparison.OrdinalIgnoreCase) &&
                        !first.Equals("ColumnName", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = first.Trim('[', ']');
                        var (dt, dl) = cells.Length > 1 ? ParseTypeAndLength(cells[1]) : ("-", "-");
                        set[name] = new DbField(name, dt, dl);
                    }
                }
                continue;
            }

            // Fixed-width/plain text table fallback:
            // Column          Type           Nullable   Notes
            // JV_TYPE_CODE    nvarchar(30)   NOT NULL   Primary Key
            var split = Regex.Split(line, @"\s{2,}")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToArray();
            if (split.Length >= 2)
            {
                var first = split[0];
                var second = split[1];
                if (!first.Equals("Column", StringComparison.OrdinalIgnoreCase) &&
                    !first.Equals("ColumnName", StringComparison.OrdinalIgnoreCase) &&
                    LooksLikeType(second))
                {
                    var (dt, dl) = ParseTypeAndLength(second);
                    set[first.Trim('[', ']')] = new DbField(first.Trim('[', ']'), dt, dl);
                }
            }
        }

        return set;
    }

    private static void TryExtractDbColumnsFromJson(string text, Dictionary<string, DbField> set)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            WalkForColumns(doc.RootElement, set);
        }
        catch
        {
            // Not JSON; ignore.
        }
    }

    private static void WalkForColumns(JsonElement element, Dictionary<string, DbField> set)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var key = prop.Name;
                    if (IsColumnNameKey(key) && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var name = (prop.Value.GetString() ?? string.Empty).Trim().Trim('[', ']');
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            set[name] = new DbField(name, "-", "-");
                        }
                    }

                    WalkForColumns(prop.Value, set);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    WalkForColumns(item, set);
                }
                break;
        }
    }

    private static bool IsColumnNameKey(string key) =>
        key.Equals("ColumnName", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("COLUMN_NAME", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("column_name", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Column", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Field", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("FieldName", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("Name", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeType(string value)
    {
        var t = value.Trim().ToLowerInvariant();
        return t.StartsWith("varchar", StringComparison.Ordinal) ||
               t.StartsWith("nvarchar", StringComparison.Ordinal) ||
               t.StartsWith("char", StringComparison.Ordinal) ||
               t.StartsWith("nchar", StringComparison.Ordinal) ||
               t.StartsWith("int", StringComparison.Ordinal) ||
               t.StartsWith("bigint", StringComparison.Ordinal) ||
               t.StartsWith("smallint", StringComparison.Ordinal) ||
               t.StartsWith("tinyint", StringComparison.Ordinal) ||
               t.StartsWith("numeric", StringComparison.Ordinal) ||
               t.StartsWith("decimal", StringComparison.Ordinal) ||
               t.StartsWith("float", StringComparison.Ordinal) ||
               t.StartsWith("real", StringComparison.Ordinal) ||
               t.StartsWith("bit", StringComparison.Ordinal) ||
               t.StartsWith("date", StringComparison.Ordinal) ||
               t.StartsWith("datetime", StringComparison.Ordinal) ||
               t.StartsWith("datetime2", StringComparison.Ordinal) ||
               t.StartsWith("time", StringComparison.Ordinal) ||
               t.StartsWith("timestamp", StringComparison.Ordinal) ||
               t.StartsWith("uniqueidentifier", StringComparison.Ordinal) ||
               t.StartsWith("varbinary", StringComparison.Ordinal) ||
               t.StartsWith("binary", StringComparison.Ordinal);
    }

    private static List<ComparisonRow> BuildComparisonRows(
        string tableName,
        string fileName,
        Dictionary<string, DbField> dbColumns,
        List<PocoField> pocoFields)
    {
        var rows = new List<ComparisonRow>();
        var pocoByMapped = pocoFields
            .GroupBy(f => f.MappedFieldName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.First(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var db in dbColumns.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (pocoByMapped.TryGetValue(db, out var poco))
            {
                var dbf = dbColumns[db];
                var typeMatch = string.Equals(Norm(dbf.DataType), Norm(poco.DataType), StringComparison.OrdinalIgnoreCase);
                var lenMatch = string.Equals(Norm(dbf.DataLength), Norm(poco.DataLength), StringComparison.OrdinalIgnoreCase) ||
                               dbf.DataLength == "-" || poco.DataLength == "-";
                var status = typeMatch && lenMatch ? "MATCH" : "TYPE_OR_LENGTH_MISMATCH";
                var (reason, fixPrompt) = BuildReasonAndFixPrompt(status, tableName, dbf.Name, dbf.DataType, dbf.DataLength, poco.PropertyName, poco.MappedFieldName, poco.DataType, poco.DataLength);
                rows.Add(new ComparisonRow(fileName, tableName, dbf.Name, dbf.DataType, dbf.DataLength, poco.PropertyName, poco.MappedFieldName, poco.DataType, poco.DataLength, true, true, status, string.Empty, reason, fixPrompt));
            }
            else
            {
                var dbf = dbColumns[db];
                var status = "MISSING_IN_POCO";
                var (reason, fixPrompt) = BuildReasonAndFixPrompt(status, tableName, dbf.Name, dbf.DataType, dbf.DataLength, "-", "-", "-", "-");
                rows.Add(new ComparisonRow(fileName, tableName, dbf.Name, dbf.DataType, dbf.DataLength, "-", "-", "-", "-", true, false, status, string.Empty, reason, fixPrompt));
            }
        }

        foreach (var poco in pocoFields.OrderBy(x => x.MappedFieldName, StringComparer.OrdinalIgnoreCase))
        {
            if (dbColumns.ContainsKey(poco.MappedFieldName))
            {
                continue;
            }

            var status = "EXTRA_IN_POCO";
            var (reason, fixPrompt) = BuildReasonAndFixPrompt(status, tableName, "-", "-", "-", poco.PropertyName, poco.MappedFieldName, poco.DataType, poco.DataLength);
            rows.Add(new ComparisonRow(fileName, tableName, "-", "-", "-", poco.PropertyName, poco.MappedFieldName, poco.DataType, poco.DataLength, false, true, status, string.Empty, reason, fixPrompt));
        }

        if (rows.Count == 0)
        {
            rows.Add(new ComparisonRow(fileName, tableName, "-", "-", "-", "-", "-", "-", "-", false, false, "ERROR_POCO_PARSE", "No comparison rows could be produced.", "POCO parse produced zero comparable fields", "Check POCO syntax/attributes and rerun."));
        }

        return rows;
    }

    private static string ExtractPocoFileName(string content)
    {
        var classMatch = Regex.Match(content, @"public\s+class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase);
        return classMatch.Success ? classMatch.Groups["name"].Value.Trim() + ".cs" : "unknown.cs";
    }

    private static string MapClrTypeToDbLikeType(string clr)
    {
        var t = clr.Trim().TrimEnd('?').ToLowerInvariant();
        return t switch
        {
            "string" => "nvarchar",
            "long" => "bigint",
            "int" => "int",
            "short" => "smallint",
            "byte" => "tinyint",
            "bool" => "bit",
            "datetime" => "datetime",
            "decimal" => "decimal",
            "float" => "float",
            "double" => "float",
            _ => t
        };
    }

    private static (string DataType, string DataLength) ParseDbTypeFromRemainder(string rest)
    {
        // Accepts:
        // nvarchar(30)
        // [nvarchar](30)
        // [numeric](5,0)
        // [timestamp]
        var m = Regex.Match(rest ?? string.Empty, @"^\[?(?<type>[A-Za-z0-9_]+)\]?\s*(\((?<len>[^)]+)\))?", RegexOptions.IgnoreCase);
        if (!m.Success) return ("-", "-");
        var type = m.Groups["type"].Value.Trim().ToLowerInvariant();
        var len = m.Groups["len"].Success ? m.Groups["len"].Value.Trim() : "-";
        return (type, len);
    }

    private static (string DataType, string DataLength) ParseTypeAndLength(string value)
    {
        var m = Regex.Match(value ?? string.Empty, @"^(?<type>[A-Za-z0-9_]+)\s*(\((?<len>[^)]+)\))?", RegexOptions.IgnoreCase);
        if (!m.Success) return ("-", "-");
        var type = m.Groups["type"].Value.Trim().ToLowerInvariant();
        var len = m.Groups["len"].Success ? m.Groups["len"].Value.Trim() : "-";
        return (type, len);
    }

    private static string Norm(string v) => (v ?? string.Empty).Trim().ToLowerInvariant();

    private static (string Reason, string FixPrompt) BuildReasonAndFixPrompt(
        string status,
        string tableName,
        string dbFieldName,
        string dbDataType,
        string dbDataLength,
        string pocoPropertyName,
        string pocoMappedFieldName,
        string pocoDataType,
        string pocoDataLength)
    {
        return status switch
        {
            "MATCH" => ("DB and POCO mapping/type/length are aligned.", "No change required."),
            "MISSING_IN_POCO" => (
                $"DB column '{dbFieldName}' exists in table '{tableName}' but no POCO mapped field was found.",
                $"Add a POCO property for column '{dbFieldName}' with compatible type '{dbDataType}({dbDataLength})' and [Column(\"{dbFieldName}\", TypeName=\"{dbDataType}{(dbDataLength != "-" ? $"({dbDataLength})" : string.Empty)}\")]."),
            "EXTRA_IN_POCO" => (
                $"POCO mapped field '{pocoMappedFieldName}' ({pocoPropertyName}) has no matching DB column in table '{tableName}'.",
                $"Either add DB column '{pocoMappedFieldName}' in table '{tableName}' or remove/retarget POCO mapping [Column(\"{pocoMappedFieldName}\")]."),
            "TYPE_OR_LENGTH_MISMATCH" => (
                $"Type/length mismatch for mapped column '{dbFieldName}': DB={dbDataType}({dbDataLength}) vs POCO={pocoDataType}({pocoDataLength}).",
                $"Align POCO mapping for '{pocoPropertyName}' to DB '{dbDataType}({dbDataLength})' (or update DB if POCO contract is intended)."),
            _ => ("Row requires manual review.", "Review DB and POCO mapping, then apply appropriate fix.")
        };
    }

    private sealed record DbField(string Name, string DataType, string DataLength);
    private sealed record PocoField(string PropertyName, string MappedFieldName, string DataType, string DataLength);
    private sealed record PocoEntity(string TableName, string FileName, List<PocoField> Fields);

    private sealed record ComparisonRow(
        string FileName,
        string TableName,
        string DbFieldName,
        string DbDataType,
        string DbDataLength,
        string PocoPropertyName,
        string PocoMappedFieldName,
        string PocoDataType,
        string PocoDataLength,
        bool InDb,
        bool InPoco,
        string Status,
        string ErrorMessage,
        string ActualReason,
        string RectificationPrompt);
}
