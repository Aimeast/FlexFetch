using FlexFetch.Entities;
using LiteDB;

namespace FlexFetch.Data;

public interface IGuestRepository
{
    GuestSession? GetById(string id);

    void Insert(GuestSession session);

    void Touch(string id, DateTime now);

    /// <summary>Returns sessions whose last activity is older than the cutoff.</summary>
    IReadOnlyList<GuestSession> GetIdleBefore(DateTime cutoff);

    bool Delete(string id);
}

public sealed class GuestRepository : IGuestRepository
{
    private const string CollectionName = "guestSessions";

    private readonly LiteDbStore _store;

    public GuestRepository(LiteDbStore store)
    {
        _store = store;
    }

    private ILiteCollection<GuestSession> Collection => _store.GetCollection<GuestSession>(CollectionName);

    public GuestSession? GetById(string id) => Collection.FindById(new BsonValue(id));

    public void Insert(GuestSession session) => Collection.Insert(session);

    public void Touch(string id, DateTime now)
    {
        var session = GetById(id);
        if (session is null)
        {
            return;
        }

        session.LastActiveAt = now;
        Collection.Update(session);
    }

    /// <summary>
    /// Returns sessions whose last activity is older than the cutoff. The
    /// date comparison happens LINQ-side, mirroring ShareRepository.GetExpired:
    /// LiteDB-stored DateTimes compare reliably only after they have been read
    /// back. Guest session rows are few, so a full scan is fine.
    /// </summary>
    public IReadOnlyList<GuestSession> GetIdleBefore(DateTime cutoff)
    {
        var cutoffUtc = cutoff.ToUniversalTime();
        return Collection.FindAll()
            .Where(g => g.LastActiveAt.ToUniversalTime() < cutoffUtc)
            .ToList();
    }

    public bool Delete(string id) => Collection.Delete(new BsonValue(id));
}
