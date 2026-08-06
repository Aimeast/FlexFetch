using FlexFetch.Domain;
using FlexFetch.Domain.Entities;
using LiteDB;

namespace FlexFetch.Data;

public interface IConfigRepository
{
    string? Get(string key);
    IReadOnlyDictionary<string, string> GetAll();
    void Set(string key, string value);
    bool Delete(string key);
}

public sealed class ConfigRepository : IConfigRepository
{
    private const string CollectionName = "config";

    private readonly LiteDbStore _store;

    public ConfigRepository(LiteDbStore store)
    {
        _store = store;
        _store.GetCollection<SystemConfig>(CollectionName).EnsureIndex(c => c.Key, unique: true);
    }

    private ILiteCollection<SystemConfig> Collection => _store.GetCollection<SystemConfig>(CollectionName);

    public string? Get(string key) =>
        Collection.FindOne(Query.EQ(nameof(SystemConfig.Key), key))?.Value;

    public IReadOnlyDictionary<string, string> GetAll() =>
        Collection.FindAll().ToDictionary(c => c.Key, c => c.Value);

    public void Set(string key, string value)
    {
        var existing = Collection.FindOne(Query.EQ(nameof(SystemConfig.Key), key));
        if (existing is null)
        {
            Collection.Insert(new SystemConfig { Key = key, Value = value });
        }
        else
        {
            existing.Value = value;
            existing.UpdatedAt = DateTime.UtcNow;
            Collection.Update(existing);
        }
    }

    public bool Delete(string key) =>
        Collection.DeleteMany(Query.EQ(nameof(SystemConfig.Key), key)) > 0;
}
