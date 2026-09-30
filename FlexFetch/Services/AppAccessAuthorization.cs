using System.Security.Claims;
using FlexFetch.Config;
using Microsoft.AspNetCore.Authorization;

namespace FlexFetch.Services;

/// <summary>
/// Authorization requirement for app functionality (the task API): granted
/// for any authenticated user, or for anonymous visitors when the
/// account.allowAnonymous configuration is enabled. Admin-only and
/// account-management endpoints never reference this requirement.
/// </summary>
public sealed class AppAccessRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// Grants <see cref="AppAccessRequirement"/>. Anonymous grants exist so a
/// deployment can expose the downloader on a trusted network (e.g. home
/// LAN) without accounts; each visitor then works in a private guest
/// session (see <see cref="GuestSessionService"/>).
/// </summary>
public sealed class AppAccessHandler : AuthorizationHandler<AppAccessRequirement>
{
    private readonly IConfiguration _config;

    public AppAccessHandler(IConfiguration config)
    {
        _config = config;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AppAccessRequirement requirement)
    {
        var authenticated = context.User.Identity?.IsAuthenticated == true
            && !string.IsNullOrEmpty(context.User.FindFirstValue(ClaimTypes.NameIdentifier));
        var anonymousAllowed = ConfigRegistry.From(_config, ConfigKeys.AllowAnonymous)
            .Equals("true", StringComparison.OrdinalIgnoreCase);
        if (authenticated || anonymousAllowed)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
