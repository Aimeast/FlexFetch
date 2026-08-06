using FlexFetch.Entities;
using FlexFetch.Enums;

namespace FlexFetch.Entities;

/// <summary>
/// A registered account. Passwords are stored as salted hashes only.
/// </summary>
public sealed class User
{
    public string Id { get; set; } = RandomId.New();

    public string UserName { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.User;

    public UserStatus Status { get; set; } = UserStatus.Active;

    public DateTime? LastLoginAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
