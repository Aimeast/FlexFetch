using System.Security.Cryptography;
using System.Text;
using FlexFetch.Config;
using FlexFetch.Data;
using FlexFetch.Entities;
using FlexFetch.Enums;

namespace FlexFetch.Services;

public enum RegisterResult
{
    Success = 0,
    ApprovalPending = 1,
    UserNameTaken = 2,
    RegistrationClosed = 3,
    InvalidInput = 4,
}

public enum LoginStatus
{
    Success = 0,
    InvalidCredentials = 1,
    NotActive = 2,
}

public sealed record LoginOutcome(LoginStatus Status, User? User);

public enum ChangePasswordResult
{
    Success = 0,
    UserNotFound = 1,
    InvalidCredentials = 2,
    InvalidInput = 3,
}

/// <summary>
/// Account management: registration policies, password hashing, login/logout,
/// roles, approve/disable/delete, inactivity determination.
/// </summary>
public sealed class UserService
{
    private readonly IUserRepository _users;
    private readonly IConfiguration _config;

    public UserService(IUserRepository users, IConfiguration config)
    {
        _users = users;
        _config = config;
    }

    public RegistrationPolicy GetRegistrationPolicy() =>
        Enum.TryParse<RegistrationPolicy>(GetConfig(ConfigKeys.RegistrationPolicy), true, out var policy)
            ? policy
            : RegistrationPolicy.Open;

    public RegisterResult Register(string userName, string password)
    {
        if (!IsValidUserName(userName) || !IsValidPassword(password))
        {
            return RegisterResult.InvalidInput;
        }

        if (_users.GetByUserName(userName) is not null)
        {
            return RegisterResult.UserNameTaken;
        }

        var policy = GetRegistrationPolicy();
        if (policy == RegistrationPolicy.Closed)
        {
            return RegisterResult.RegistrationClosed;
        }

        var user = new User
        {
            UserName = userName,
            PasswordHash = PasswordHasher.Hash(password),
            Status = policy == RegistrationPolicy.Approval ? UserStatus.Pending : UserStatus.Active,
        };
        _users.Insert(user);

        return user.Status == UserStatus.Pending ? RegisterResult.ApprovalPending : RegisterResult.Success;
    }

    public LoginOutcome Login(string userName, string password)
    {
        var user = _users.GetByUserName(userName);
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            return new LoginOutcome(LoginStatus.InvalidCredentials, null);
        }

        if (user.Status != UserStatus.Active)
        {
            return new LoginOutcome(LoginStatus.NotActive, user);
        }

        user.LastLoginAt = DateTime.UtcNow;
        _users.Update(user);
        return new LoginOutcome(LoginStatus.Success, user);
    }

    public User? GetById(string id) => _users.GetById(id);

    /// <summary>
    /// Changes a user's own password: the current password must verify and
    /// the new one must satisfy the same rule as registration.
    /// </summary>
    public ChangePasswordResult ChangePassword(string userId, string currentPassword, string newPassword)
    {
        var user = _users.GetById(userId);
        if (user is null)
        {
            return ChangePasswordResult.UserNotFound;
        }

        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash))
        {
            return ChangePasswordResult.InvalidCredentials;
        }

        if (!IsValidPassword(newPassword))
        {
            return ChangePasswordResult.InvalidInput;
        }

        user.PasswordHash = PasswordHasher.Hash(newPassword);
        return _users.Update(user)
            ? ChangePasswordResult.Success
            : ChangePasswordResult.UserNotFound;
    }

    public IReadOnlyList<User> GetPending() => _users.GetPending();

    public bool Approve(string id)
    {
        var user = _users.GetById(id);
        if (user is null || user.Status != UserStatus.Pending)
        {
            return false;
        }

        user.Status = UserStatus.Active;
        return _users.Update(user);
    }

    public bool Disable(string id)
    {
        var user = _users.GetById(id);
        if (user is null || user.Status == UserStatus.Disabled)
        {
            return false;
        }

        user.Status = UserStatus.Disabled;
        return _users.Update(user);
    }

    public bool Delete(string id) => _users.Delete(id);

    /// <summary>
    /// Determines whether a user is inactive per the configured threshold
    /// (account.inactiveDays; 0 disables cleanup). Used by the cleanup
    /// hosted service.
    /// </summary>
    public bool IsInactive(User user, DateTime now)
    {
        var days = int.TryParse(GetConfig(ConfigKeys.InactiveDays), out var d) ? d : 30;
        if (days <= 0)
        {
            return false;
        }

        var cutoff = now.ToUniversalTime().AddDays(-days);
        var lastActive = user.LastLoginAt?.ToUniversalTime() ?? user.CreatedAt.ToUniversalTime();
        return lastActive < cutoff;
    }

    /// <summary>
    /// Creates the initial admin when the database has no users yet - admin is
    /// always the first account, so the name can never be taken by a regular
    /// user. Password priority: configured value, else a simple dev password
    /// in development, else a generated strong password. Returns the created
    /// password, or null when no admin was created.
    /// </summary>
    public string? EnsureInitialAdmin(string? configuredPassword, bool isDevelopment)
    {
        if (_users.GetAll().Any())
        {
            return null; // database already initialized; never touch accounts
        }

        var password = configuredPassword
            ?? (isDevelopment ? "admin" : GenerateStrongPassword());

        _users.Insert(new User
        {
            UserName = "admin",
            PasswordHash = PasswordHasher.Hash(password),
            Role = UserRole.Admin,
            Status = UserStatus.Active,
        });

        return password;
    }

    private static string GenerateStrongPassword()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*()-_=+";
        var bytes = new byte[18];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(chars.Length);
        foreach (var b in bytes)
        {
            sb.Append(chars[b % chars.Length]);
        }
        return sb.ToString();
    }

    private string GetConfig(string key) => ConfigRegistry.From(_config, key);

    private static bool IsValidUserName(string userName) =>
        userName.Length is >= 3 and <= 32 && userName.All(char.IsLetterOrDigit);

    private static bool IsValidPassword(string password) => password.Length >= 5;
}
