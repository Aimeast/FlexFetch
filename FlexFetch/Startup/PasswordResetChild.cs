using FlexFetch.Data;
using FlexFetch.Services;

namespace FlexFetch.Startup;

/// <summary>Result of an OS-level password reset (see PasswordResetChild).</summary>
public enum PasswordResetResult
{
    Success,
    UserNotFound,
    InvalidInput,
}

/// <summary>
/// OS-operator command to reset an account password from the server shell:
/// <c>--reset-password &lt;userName&gt; &lt;newPassword&gt;</c>. Shell access
/// is the trust anchor (whoever can run processes on the host can edit the
/// database anyway); the web-side change-password path stays the channel for
/// the signed-in user. The stored hash is swapped in place, keeping the user
/// id - task ownership and shares reference it. Exits without starting the
/// web application.
/// </summary>
public static class PasswordResetChild
{
    public static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: FlexFetch --reset-password <userName> <newPassword>");
            return 1;
        }

        var userName = args[1];
        var newPassword = args[2];

        // Same data-dir discovery as the web app: bundled appsettings plus
        // environment, /data adopted on Linux when it exists. The editable
        // user config file is loaded after data-dir resolution in the app
        // too, so it cannot relocate the data directory here either.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var dataDir = RuntimeSetup.ResolveDataDir(configuration);
        var dbPath = Path.Combine(Path.GetFullPath(dataDir), "flexfetch.db");

        PasswordResetResult result;
        try
        {
            result = Reset(dbPath, userName, newPassword);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Password reset failed: {ex.Message}");
            Console.Error.WriteLine("If FlexFetch is running, stop it first and retry.");
            return 3;
        }

        switch (result)
        {
            case PasswordResetResult.Success:
                Console.WriteLine($"Password reset for '{userName}'.");
                return 0;
            case PasswordResetResult.UserNotFound:
                Console.Error.WriteLine($"User '{userName}' does not exist in {dbPath}.");
                return 2;
            default:
                Console.Error.WriteLine("New password must be at least 5 characters.");
                return 1;
        }
    }

    /// <summary>Core of the reset against a database file; public for tests.</summary>
    public static PasswordResetResult Reset(string dbPath, string userName, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(userName) || newPassword is null || newPassword.Length < 5)
        {
            return PasswordResetResult.InvalidInput;
        }

        if (!File.Exists(dbPath))
        {
            throw new FileNotFoundException("FlexFetch database not found", dbPath);
        }

        using var store = new LiteDbStore(dbPath);
        var users = new UserRepository(store);
        var user = users.GetByUserName(userName);
        if (user is null)
        {
            return PasswordResetResult.UserNotFound;
        }

        user.PasswordHash = PasswordHasher.Hash(newPassword);
        return users.Update(user) ? PasswordResetResult.Success : PasswordResetResult.UserNotFound;
    }
}
