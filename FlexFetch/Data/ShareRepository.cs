using FlexFetch.Domain;
using FlexFetch.Domain.Entities;
using LiteDB;

namespace FlexFetch.Data;

public interface IShareRepository
{
    ShareToken? GetByToken(string token);
    IReadOnlyList<ShareToken> GetByTaskId(string taskId);
    IReadOnlyList<ShareToken> GetExpired(DateTime now);
    void Insert(ShareToken share);
    bool Delete(string token);
    bool DeleteByTaskId(string taskId);
}

public sealed class ShareRepository : IShareRepository
{
    private const string CollectionName = "shares";

    private readonly LiteDbStore _store;

    public ShareRepository(LiteDbStore store)
    {
        _store = store;
        _store.GetCollection<ShareToken>(CollectionName).EnsureIndex(s => s.Token, unique: true);
    }

    private ILiteCollection<ShareToken> Collection => _store.GetCollection<ShareToken>(CollectionName);

    public ShareToken? GetByToken(string token) =>
        Collection.FindOne(Query.EQ(nameof(ShareToken.Token), token));

    public IReadOnlyList<ShareToken> GetByTaskId(string taskId) =>
        Collection.Find(Query.EQ(nameof(ShareToken.TaskId), taskId)).ToList();

    /// <summary>Returns shares whose expiry is set and in the past.</summary>
    public IReadOnlyList<ShareToken> GetExpired(DateTime now)
    {
        var nowUtc = now.ToUniversalTime();
        return Collection.FindAll()
            .Where(s => s.ExpiresAt is not null && s.ExpiresAt.Value.ToUniversalTime() < nowUtc)
            .ToList();
    }

    public void Insert(ShareToken share) => Collection.Insert(share);

    public bool Delete(string token) =>
        Collection.DeleteMany(Query.EQ(nameof(ShareToken.Token), token)) > 0;

    public bool DeleteByTaskId(string taskId) =>
        Collection.DeleteMany(Query.EQ(nameof(ShareToken.TaskId), taskId)) > 0;
}
