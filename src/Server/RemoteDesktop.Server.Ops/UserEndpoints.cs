using System.Security.Claims;
using System.Text.Json;
using RemoteDesktop.Server.Ops.Auth;
using RemoteDesktop.Server.Ops.Core;
using RemoteDesktop.Server.Ops.Storage;
using RemoteDesktop.Server.Shared;

namespace RemoteDesktop.Server.Ops;

/// <summary>What the account form posts.</summary>
public sealed record CreateUserRequest(string? Username, string? Password, string? Role);

/// <summary>A replacement password.</summary>
public sealed record SetPasswordRequest(string? Password);

/// <summary>Whether an account may sign in.</summary>
public sealed record SetDisabledRequest(bool? Disabled);

/// <summary>
/// Account management for administrators: create, reset a password, reset a second factor, disable, delete.
/// </summary>
/// <remarks>
/// Before this, the only way to change an account was another account or a database editor, so a compromised
/// administrator could not be revoked from the console. Every change here ends the account's sessions, which is what
/// makes "disabled" and "password changed" take effect on the next request rather than at the end of a cookie's life.
/// The last enabled administrator cannot be disabled or deleted — that is the one change that would leave nobody able
/// to sign in.
/// </remarks>
public static class UserEndpoints
{
    private const int MaxUsernameLength = 64;
    private const int MinimumPasswordLength = 12;
    private const int MaximumPasswordLength = 1024;

    /// <summary>Maps <c>/api/users</c>.</summary>
    public static void MapOpsUsers(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("/api/users").RequireAuthorization(OpsAuth.AdminPolicy);

        group.MapGet("/", async (OpsStore store, CancellationToken cancellationToken) =>
        {
            var users = await store.ListUsersAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(users.Select(Describe));
        });

        group.MapPost("/", async (
            CreateUserRequest request,
            HttpContext context,
            OpsStore store,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            if (!TryValidateUsername(request.Username, out var username, out var nameError))
            {
                return Results.BadRequest(new { error = "username", message = nameError });
            }

            if (!TryValidatePassword(request.Password, out var password, out var passwordError))
            {
                return Results.BadRequest(new { error = "password", message = passwordError });
            }

            var role = OpsRoles.Canonical(request.Role);
            if (role is null)
            {
                return Results.BadRequest(new { error = "role", message = "role must be viewer, operator or admin" });
            }

            if (await store.FindUserAsync(username, cancellationToken).ConfigureAwait(false) is not null)
            {
                return Results.Conflict(new { error = "username", message = "an account with that name already exists" });
            }

            var secret = Totp.GenerateSecret();
            var user = new OpsAdminUser(
                Guid.NewGuid().ToString("N"),
                username,
                PasswordHasher.Hash(password),
                secret,
                role,
                Disabled: false,
                time.GetUtcNow().ToUnixTimeMilliseconds());

            try
            {
                await store.CreateUserAsync(user, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (OpsStore.IsUniqueViolation(ex))
            {
                // Another request took the name between the check above and this insert.
                return Results.Conflict(new { error = "username", message = "an account with that name already exists" });
            }

            await AuditAsync(store, context, "admin.create", username, $"created {role}", time, cancellationToken)
                .ConfigureAwait(false);

            // The secret is returned once. There is no endpoint that reads it again, by design.
            return Results.Ok(new
            {
                id = user.Id,
                username,
                role,
                totpSecret = secret,
                otpauthUri = Totp.BuildUri("RemoteDesktop", username, secret),
            });
        });

        group.MapPost("/{id}/password", async (
            string id,
            SetPasswordRequest request,
            HttpContext context,
            OpsStore store,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            if (!TryValidatePassword(request.Password, out var password, out var passwordError))
            {
                return Results.BadRequest(new { error = "password", message = passwordError });
            }

            var user = await store.FindUserByIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (!await store.UpdateUserPasswordAsync(id, PasswordHasher.Hash(password), cancellationToken).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            await AuditAsync(store, context, "admin.password", user.Username, "password reset; sessions revoked", time, cancellationToken)
                .ConfigureAwait(false);
            return Results.NoContent();
        });

        group.MapPost("/{id}/totp", async (
            string id,
            HttpContext context,
            OpsStore store,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var user = await store.FindUserByIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                return Results.NotFound();
            }

            var secret = Totp.GenerateSecret();
            if (!await store.UpdateUserTotpSecretAsync(id, secret, cancellationToken).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            await AuditAsync(store, context, "admin.totp", user.Username, "TOTP secret reset; sessions revoked", time, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new
            {
                totpSecret = secret,
                otpauthUri = Totp.BuildUri("RemoteDesktop", user.Username, secret),
            });
        });

        group.MapPost("/{id}/disabled", async (
            string id,
            SetDisabledRequest request,
            HttpContext context,
            OpsStore store,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            if (request.Disabled is not { } disabled)
            {
                return Results.BadRequest(new { error = "disabled", message = "disabled is required" });
            }

            var user = await store.FindUserByIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (disabled && IsSelf(context, id))
            {
                return Results.BadRequest(new { error = "disabled", message = "you cannot disable your own account" });
            }

            if (user.Disabled == disabled)
            {
                return Results.NoContent();
            }

            if (!await store.SetUserDisabledAsync(id, disabled, cancellationToken).ConfigureAwait(false))
            {
                return Results.BadRequest(new
                {
                    error = "disabled",
                    message = "the last enabled administrator cannot be disabled",
                });
            }

            await AuditAsync(
                store,
                context,
                disabled ? "admin.disable" : "admin.enable",
                user.Username,
                disabled ? "account disabled; sessions revoked" : "account enabled",
                time,
                cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        });

        group.MapDelete("/{id}", async (
            string id,
            HttpContext context,
            OpsStore store,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var user = await store.FindUserByIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (IsSelf(context, id))
            {
                return Results.BadRequest(new { error = "delete", message = "you cannot delete your own account" });
            }

            if (!await store.DeleteUserAsync(id, cancellationToken).ConfigureAwait(false))
            {
                return Results.BadRequest(new
                {
                    error = "delete",
                    message = "the last enabled administrator cannot be deleted",
                });
            }

            await AuditAsync(store, context, "admin.delete", user.Username, "account deleted; sessions revoked", time, cancellationToken)
                .ConfigureAwait(false);
            return Results.NoContent();
        });
    }

    private static object Describe(OpsAdminUserSummary user) => new
    {
        id = user.Id,
        username = user.Username,
        role = user.Role,
        disabled = user.Disabled,
        createdAt = user.CreatedAt,
    };

    private static bool TryValidateUsername(string? value, out string username, out string? message)
    {
        username = value?.Trim() ?? string.Empty;
        message = null;

        if (username.Length == 0 || username.Length > MaxUsernameLength)
        {
            message = $"username is required and at most {MaxUsernameLength} characters";
            return false;
        }

        return true;
    }

    private static bool TryValidatePassword(string? value, out string password, out string? message)
    {
        password = value ?? string.Empty;
        message = null;

        if (password.Length < MinimumPasswordLength || password.Length > MaximumPasswordLength)
        {
            message = $"password must be between {MinimumPasswordLength} and {MaximumPasswordLength} characters";
            return false;
        }

        return true;
    }

    private static bool IsSelf(HttpContext context, string id) =>
        string.Equals(context.User.FindFirstValue(ClaimTypes.NameIdentifier), id, StringComparison.Ordinal);

    private static Task<string> AuditAsync(
        OpsStore store,
        HttpContext context,
        string action,
        string target,
        string detail,
        TimeProvider time,
        CancellationToken cancellationToken) =>
        store.AppendAuditAsync(
            new OpsAuditEntry(
                Guid.NewGuid().ToString("N"),
                time.GetUtcNow().ToUnixTimeMilliseconds(),
                context.User.Identity?.Name ?? "unknown",
                action,
                target,
                JsonSerializer.Serialize(new { detail, ip = context.Connection.RemoteIpAddress?.ToString() }, OpsEventJson.Options),
                context.Connection.RemoteIpAddress?.ToString() ?? "-",
                "success"),
            cancellationToken);
}
