using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Enums;
using FlexFetch.Services;

namespace FlexFetch.Tests;

[TestClass]
public sealed class UserServiceTests
{
    private string? _dir;
    private LiteDbStore? _store;
    private UserService? _service;
    private IConfigRepository? _config;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "flexfetch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new LiteDbStore(Path.Combine(_dir, "flexfetch.db"));
        _config = new ConfigRepository(_store);
        _service = new UserService(new UserRepository(_store), _config);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _store?.Dispose();
        if (_dir is not null && Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [TestMethod]
    public void PasswordHasher_HashesAndVerifies()
    {
        var hash = PasswordHasher.Hash("secret123");
        Assert.AreNotEqual("secret123", hash);
        Assert.IsTrue(PasswordHasher.Verify("secret123", hash));
        Assert.IsFalse(PasswordHasher.Verify("wrong", hash));
        Assert.IsFalse(PasswordHasher.Verify("secret123", "not-a-hash"));
    }

    [TestMethod]
    public void PasswordHasher_ProducesDifferentSalts()
    {
        Assert.AreNotEqual(PasswordHasher.Hash("secret123"), PasswordHasher.Hash("secret123"));
    }

    [TestMethod]
    public void Register_OpenPolicy_ActivatesImmediately()
    {
        var result = _service!.Register("alice", "password1");

        Assert.AreEqual(RegisterResult.Success, result);
        var user = _service.Login("alice", "password1");
        Assert.AreEqual(LoginStatus.Success, user.Status);
        Assert.IsNotNull(user.User);
    }

    [TestMethod]
    public void Register_ApprovalPolicy_CreatesPendingUser()
    {
        _config!.Set(ConfigKeys.RegistrationPolicy, "Approval");

        var result = _service!.Register("bob", "password1");

        Assert.AreEqual(RegisterResult.ApprovalPending, result);
        Assert.HasCount(1, _service.GetPending());

        // Pending users cannot log in yet.
        var login = _service.Login("bob", "password1");
        Assert.AreEqual(LoginStatus.NotActive, login.Status);
    }

    [TestMethod]
    public void Register_ClosedPolicy_Rejects()
    {
        _config!.Set(ConfigKeys.RegistrationPolicy, "Closed");

        Assert.AreEqual(RegisterResult.RegistrationClosed, _service!.Register("carol", "password1"));
    }

    [TestMethod]
    public void Register_DuplicateName_ReturnsTaken()
    {
        _service!.Register("dave", "password1");

        Assert.AreEqual(RegisterResult.UserNameTaken, _service.Register("dave", "password2"));
    }

    [TestMethod]
    public void Register_InvalidInput_Rejected()
    {
        Assert.AreEqual(RegisterResult.InvalidInput, _service!.Register("ab", "password1"));
        Assert.AreEqual(RegisterResult.InvalidInput, _service.Register("validname", "123"));
    }

    [TestMethod]
    public void Login_WrongPassword_ReturnsInvalidCredentials()
    {
        _service!.Register("erin", "password1");

        var result = _service.Login("erin", "wrongpass");

        Assert.AreEqual(LoginStatus.InvalidCredentials, result.Status);
        Assert.IsNull(result.User);
    }

    [TestMethod]
    public void Login_UpdatesLastLoginAt()
    {
        _service!.Register("frank", "password1");

        var before = DateTime.UtcNow;
        _service.Login("frank", "password1");

        var user = _service.GetById(_service.Login("frank", "password1").User!.Id);
        Assert.IsNotNull(user);
        Assert.IsNotNull(user.LastLoginAt);
        Assert.IsTrue(user.LastLoginAt >= before);
    }

    [TestMethod]
    public void Approve_ActivatesPendingUser()
    {
        _config!.Set(ConfigKeys.RegistrationPolicy, "Approval");
        _service!.Register("grace", "password1");
        var pending = _service.GetPending().Single();

        Assert.IsTrue(_service.Approve(pending.Id));
        Assert.HasCount(0, _service.GetPending());
        Assert.AreEqual(LoginStatus.Success, _service.Login("grace", "password1").Status);

        // Approving again is a no-op.
        Assert.IsFalse(_service.Approve(pending.Id));
    }

    [TestMethod]
    public void Disable_BlocksLogin()
    {
        _service!.Register("henry", "password1");
        var user = _service.GetPending().Concat(_service.Login("henry", "password1").User is { } u ? new[] { u } : Array.Empty<User>()).LastOrDefault()
            ?? throw new InvalidOperationException();

        Assert.IsTrue(_service.Disable(user.Id));
        Assert.AreEqual(LoginStatus.NotActive, _service.Login("henry", "password1").Status);

        // Disabling an already-disabled user is a no-op.
        Assert.IsFalse(_service.Disable(user.Id));
    }

    [TestMethod]
    public void Delete_RemovesUser()
    {
        _service!.Register("ivy", "password1");
        var user = _service.Login("ivy", "password1").User!;

        Assert.IsTrue(_service.Delete(user.Id));
        Assert.AreEqual(LoginStatus.InvalidCredentials, _service.Login("ivy", "password1").Status);
    }

    [TestMethod]
    public void IsInactive_RespectsThreshold()
    {
        _service!.Register("jack", "password1");
        var user = _service.Login("jack", "password1").User!;

        var now = DateTime.UtcNow;
        Assert.IsFalse(_service.IsInactive(user, now));

        // Threshold of 30 days: last login was 31 days ago -> inactive.
        var oldUser = new User { UserName = "old", CreatedAt = now.AddDays(-40) };
        Assert.IsTrue(_service.IsInactive(oldUser, now));
        // 20 days ago the cutoff was 50 days back, so a 40-day-old user was still active.
        Assert.IsFalse(_service.IsInactive(oldUser, now.AddDays(-20)));

        // 0 disables cleanup.
        _config!.Set(ConfigKeys.InactiveDays, "0");
        Assert.IsFalse(_service.IsInactive(oldUser, now));
    }

    [TestMethod]
    public void EnsureInitialAdmin_CreatesAdminWhenDatabaseEmpty()
    {
        // Empty database: admin is created as the first account.
        var password = _service!.EnsureInitialAdmin("bootpass", isDevelopment: false);
        Assert.AreEqual("bootpass", password);

        var admin = _service.Login("admin", "bootpass");
        Assert.AreEqual(LoginStatus.Success, admin.Status);
        Assert.AreEqual(UserRole.Admin, admin.User!.Role);
    }

    [TestMethod]
    public void EnsureInitialAdmin_ReturnsNullWhenUsersExist()
    {
        _service!.Register("alice", "password1");

        // Non-empty database: never touch existing accounts.
        Assert.IsNull(_service.EnsureInitialAdmin("bootpass", isDevelopment: false));
        Assert.AreEqual(LoginStatus.InvalidCredentials, _service.Login("admin", "bootpass").Status);
    }

    [TestMethod]
    public void EnsureInitialAdmin_ConfiguredPasswordTakesPriority()
    {
        var password = _service!.EnsureInitialAdmin("configured-pass", isDevelopment: false);
        Assert.AreEqual("configured-pass", password);
        Assert.AreEqual(LoginStatus.Success, _service.Login("admin", "configured-pass").Status);
    }

    [TestMethod]
    public void EnsureInitialAdmin_DevUsesSimplePassword_ProdGeneratesStrong()
    {
        var devPassword = _service!.EnsureInitialAdmin(null, isDevelopment: true);
        Assert.AreEqual("admin", devPassword);

        using var prodStore = new LiteDbStore(Path.Combine(Path.GetTempPath(), "flexfetch-test-" + Guid.NewGuid().ToString("N") + ".db"));
        var prodService = new UserService(new UserRepository(prodStore), new ConfigRepository(prodStore));
        var prodPassword = prodService.EnsureInitialAdmin(null, isDevelopment: false);
        Assert.IsNotNull(prodPassword);
        Assert.IsGreaterThanOrEqualTo(prodPassword!.Length, 18);
        Assert.AreEqual(LoginStatus.Success, prodService.Login("admin", prodPassword).Status);
        prodStore.Dispose();
    }
}
