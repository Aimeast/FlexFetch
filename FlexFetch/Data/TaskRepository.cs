using FlexFetch.Domain;
using FlexFetch.Domain.Entities;
using LiteDB;
using TaskStatus = FlexFetch.Domain.Enums.TaskStatus;

namespace FlexFetch.Data;

public interface ITaskRepository
{
    TaskItem? GetById(string id);
    IReadOnlyList<TaskItem> GetByOwner(string ownerUserId);
    IReadOnlyList<TaskItem> GetByStatus(TaskStatus status);
    IReadOnlyList<TaskItem> GetChildren(string parentId);
    IReadOnlyList<TaskItem> GetAll();
    void Insert(TaskItem task);
    bool Update(TaskItem task);
    bool Delete(string id);
}

public sealed class TaskRepository : ITaskRepository
{
    private const string CollectionName = "tasks";

    private readonly LiteDbStore _store;

    public TaskRepository(LiteDbStore store)
    {
        _store = store;
        var col = _store.GetCollection<TaskItem>(CollectionName);
        col.EnsureIndex(t => t.OwnerUserId);
        col.EnsureIndex(t => t.ParentId);
        col.EnsureIndex(t => t.Status);
    }

    private ILiteCollection<TaskItem> Collection => _store.GetCollection<TaskItem>(CollectionName);

    public TaskItem? GetById(string id) => Collection.FindById(new BsonValue(id));

    public IReadOnlyList<TaskItem> GetByOwner(string ownerUserId) =>
        Collection.Find(Query.EQ(nameof(TaskItem.OwnerUserId), ownerUserId)).ToList();

    public IReadOnlyList<TaskItem> GetByStatus(TaskStatus status) =>
        Collection.Find(Query.EQ(nameof(TaskItem.Status), status.ToString())).ToList();

    public IReadOnlyList<TaskItem> GetChildren(string parentId) =>
        Collection.Find(Query.EQ(nameof(TaskItem.ParentId), parentId)).ToList();

    public IReadOnlyList<TaskItem> GetAll() => Collection.FindAll().ToList();

    public void Insert(TaskItem task) => Collection.Insert(task);

    public bool Update(TaskItem task) => Collection.Update(task);

    public bool Delete(string id) => Collection.Delete(new BsonValue(id));
}
