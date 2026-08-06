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

/// <summary>
/// Account management: registration policies, password hashing, login/logout,
/// roles, approve/disable/delete, inactivity determination.
/// </summary>
public sealed class UserService
{
    private readonly IUserRepository _users;
    private readonly IConfigRepository _config;

    public UserService(IUserRepository users, IConfigRepository config)
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
    /// Creates the initial admin if none exists and a bootstrap password
    /// is provided. Password is supplied via configuration (appsettings/env),
    /// never via the runtime config registry.
    /// </summary>
    public void EnsureInitialAdmin(string? bootstrapPassword)
    {
        if (string.IsNullOrWhiteSpace(bootstrapPassword))
        {
            return;
        }

        var hasAdmin = _users.GetAll().Any(u => u.Role == UserRole.Admin && u.Status == UserStatus.Active);
        if (!hasAdmin)
        {
            _users.Insert(new User
            {
                UserName = "admin",
                PasswordHash = PasswordHasher.Hash(bootstrapPassword),
                Role = UserRole.Admin,
                Status = UserStatus.Active,
            });
        }
    }

    private string GetConfig(string key) => _config.Get(key) ?? ConfigRegistry.GetDefault(key);

    private static bool IsValidUserName(string userName) =>
        userName.Length is >= 3 and <= 32 && userName.All(char.IsLetterOrDigit);

    private static bool IsValidPassword(string password) => password.Length >= 6;
}
