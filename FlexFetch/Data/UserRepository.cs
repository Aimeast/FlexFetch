using FlexFetch.Entities;
using FlexFetch.Enums;
using LiteDB;

namespace FlexFetch.Data;

public interface IUserRepository
{
    User? GetById(string id);
    User? GetByUserName(string userName);
    IReadOnlyList<User> GetAll();
    IReadOnlyList<User> GetPending();
    void Insert(User user);
    bool Update(User user);
    bool Delete(string id);
}

public sealed class UserRepository : IUserRepository
{
    private const string CollectionName = "users";

    private readonly LiteDbStore _store;

    public UserRepository(LiteDbStore store)
    {
        _store = store;
        _store.GetCollection<User>(CollectionName).EnsureIndex(u => u.UserName, unique: true);
    }

    private ILiteCollection<User> Collection => _store.GetCollection<User>(CollectionName);

    public User? GetById(string id) => Collection.FindById(new BsonValue(id));

    public User? GetByUserName(string userName) =>
        Collection.FindOne(Query.EQ(nameof(User.UserName), userName));

    public IReadOnlyList<User> GetAll() => Collection.FindAll().ToList();

    public IReadOnlyList<User> GetPending() =>
        Collection.Find(Query.EQ(nameof(User.Status), UserStatus.Pending.ToString())).ToList();

    public void Insert(User user) => Collection.Insert(user);

    public bool Update(User user) => Collection.Update(user);

    public bool Delete(string id) => Collection.Delete(new BsonValue(id));
}
