namespace SetYazilim.Llm.Api.Sql;

public enum DbObjectType
{
    Table,
    View,
    Procedure,
    Function,
    Trigger,
    Index,
}

public sealed record DbObjectInfo(
    string       Schema,
    string       Name,
    DbObjectType Type)
{
    public string QualifiedName => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
    public string TypeStr       => Type.ToString().ToLowerInvariant();
}

public sealed record SchemaIngestResult(
    int                           Total,
    int                           SuccessCount,
    List<(string Name, string Error)> Failures);

public interface ISqlSchemaProvider
{
    DbType DbType { get; }

    /// <summary>List all objects in the database (filtered by includeTypes if specified).</summary>
    Task<List<DbObjectInfo>> ListObjectsAsync(
        string connStr,
        HashSet<DbObjectType>? includeTypes,
        CancellationToken ct);

    /// <summary>Get the CREATE DDL script for a single object.</summary>
    Task<string> GetCreateScriptAsync(
        string connStr,
        DbObjectInfo obj,
        CancellationToken ct);
}
