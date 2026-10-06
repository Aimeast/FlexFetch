using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Enums;
using FlexFetch.Services;
using FlexFetch.Startup;
using Microsoft.Extensions.Configuration;

namespace FlexFetch.Tests;

[TestClass]
public sealed class PasswordResetChildTests
{
    private string? _dir;

    [TestInitialize]
    public void Setup() => _dir = TestApp.CreateTempDataDir();

    [TestCleanup]
    public void Cleanup()
    {
        if (_dir is not null && Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string DbPath => Path.Combine(_dir!, "flexfetch.db");

    private string SeedUser(string userName, string password)
    {
        using var store = new LiteDbStore(DbPath);
        var users = new UserRepository(store);
        var user = new User
        {
            UserName = userName,
            PasswordHash = PasswordHasher.Hash(password),
            Status = UserStatus.Active,
        };
        users.Insert(user);
        return user.Id;
    }

    private UserService CreateService()
    {
        var store = new LiteDbStore(DbPath);
        return new UserService(new UserRepository(store), new ConfigurationBuilder().Build());
    }

    [TestMethod]
    public void Reset_ExistingUser_SwapsHashAndKeepsUserId()
    {
        // The user id must survive the reset: task ownership and shares
        // reference it, so re-seeding a new account would orphan them all.
        var originalId = SeedUser("admin", "old-pass");

        var result = PasswordResetChild.Reset(DbPath, "admin", "admin");

        Assert.AreEqual(PasswordResetResult.Success, result);
        using (var store = new LiteDbStore(DbPath))
        {
            var user = new UserRepository(store).GetByUserName("admin");
            Assert.AreEqual(originalId, user!.Id);
        }
    }

    [TestMethod]
    public void Reset_ExistingUser_OldPasswordRejected_NewPasswordLogsIn()
    {
        SeedUser("admin", "old-pass");

        PasswordResetChild.Reset(DbPath, "admin", "admin");

        var users = CreateService();
        Assert.AreEqual(
            LoginStatus.InvalidCredentials,
            users.Login("admin", "old-pass").Status);
        Assert.AreEqual(
            LoginStatus.Success,
            users.Login("admin", "admin").Status);
    }

    [TestMethod]
    public void Reset_UnknownUserName_NotFound()
    {
        SeedUser("admin", "old-pass");

        var result = PasswordResetChild.Reset(DbPath, "root", "newpass");

        Assert.AreEqual(PasswordResetResult.UserNotFound, result);
    }

    [TestMethod]
    public void Reset_ShortPassword_RejectedAndHashUnchanged()
    {
        SeedUser("admin", "old-pass");

        var result = PasswordResetChild.Reset(DbPath, "admin", "abc");

        Assert.AreEqual(PasswordResetResult.InvalidInput, result);
        Assert.AreEqual(
            LoginStatus.Success,
            CreateService().Login("admin", "old-pass").Status);
    }

    [TestMethod]
    public void Reset_MissingDatabase_Throws()
    {
        // The web app must have created the database before; pointing the
        // command at a fresh path is an operator mistake, not an empty slate
        // to "reset" - a stray empty database must not appear silently.
        Assert.ThrowsExactly<FileNotFoundException>(
            () => PasswordResetChild.Reset(Path.Combine(_dir!, "nope.db"), "admin", "admin"));
    }

    [TestMethod]
    public void Run_MissingArguments_UsageErrorWithoutTouchingDisk()
    {
        // Argument validation precedes data-dir resolution: a malformed call
        // exits before any file system access.
        Assert.AreEqual(1, PasswordResetChild.Run(new[] { "--reset-password", "admin" }));
        Assert.AreEqual(1, PasswordResetChild.Run(Array.Empty<string>()));
    }
}
