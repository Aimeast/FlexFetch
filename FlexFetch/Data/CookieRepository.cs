using FlexFetch.Domain;
using FlexFetch.Domain.Entities;
using LiteDB;

namespace FlexFetch.Data;

public interface ICookieRepository
{
    IReadOnlyList<CookieGroup> GetAllGroups();
    CookieGroup? GetGroupById(string id);
    CookieGroup? GetGroupByName(string name);
    void InsertGroup(CookieGroup group);
    bool UpdateGroup(CookieGroup group);
    bool DeleteGroup(string id);
}

public sealed class CookieRepository : ICookieRepository
{
    private const string CollectionName = "cookies";

    private readonly LiteDbStore _store;

    public CookieRepository(LiteDbStore store)
    {
        _store = store;
        _store.GetCollection<CookieGroup>(CollectionName).EnsureIndex(g => g.Name, unique: true);
    }

    private ILiteCollection<CookieGroup> Collection => _store.GetCollection<CookieGroup>(CollectionName);

    public IReadOnlyList<CookieGroup> GetAllGroups() => Collection.FindAll().ToList();

    public CookieGroup? GetGroupById(string id) => Collection.FindById(new BsonValue(id));

    public CookieGroup? GetGroupByName(string name) =>
        Collection.FindOne(Query.EQ(nameof(CookieGroup.Name), name));

    public void InsertGroup(CookieGroup group) => Collection.Insert(group);

    public bool UpdateGroup(CookieGroup group) => Collection.Update(group);

    public bool DeleteGroup(string id) => Collection.Delete(new BsonValue(id));
}
