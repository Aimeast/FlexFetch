using LiteDB;

namespace FlexFetch.Data;

/// <summary>
/// Wraps the LiteDB single-file database: connection, BsonMapper
/// configuration and lifecycle. Repositories share one instance.
/// </summary>
public sealed class LiteDbStore : IDisposable
{
    private readonly LiteDatabase _db;

    public LiteDbStore(string dbFilePath)
    {
        var mapper = new BsonMapper();
        // Store enums as strings for stable, human-readable persistence
        // (repository queries compare against ToString() values).
        mapper.EnumAsInteger = false;

        var connectionString = new ConnectionString
        {
            Filename = dbFilePath,
            Connection = ConnectionType.Shared,
            ReadOnly = false,
        };

        _db = new LiteDatabase(connectionString, mapper);
    }

    public LiteDatabase Database => _db;

    /// <summary>Gets a typed collection, ensuring it exists.</summary>
    public ILiteCollection<T> GetCollection<T>(string name) where T : class => _db.GetCollection<T>(name);

    public void Dispose()
    {
        _db.Dispose();
    }
}
