using Escribot.Api.Data;
using Escribot.Api.Data.Models;
using Microsoft.AspNetCore.Identity;

namespace Escribot.Api.Auth;

/// <summary>
/// The user as ASP.NET Core Identity works with it: a mutable copy of the stored
/// <see cref="UserAccount"/>. The email address is also the user name.
/// </summary>
public sealed class AppUser
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public required string Email { get; init; }

    public string Name { get; set; } = string.Empty;

    public bool EmailVerified { get; set; }

    public string? Image { get; set; }

    public string? PasswordHash { get; set; }

    public string? SecurityStamp { get; set; }

    public List<ExternalLogin> ExternalLogins { get; set; } = [];

    public static AppUser From(UserAccount account) => new()
    {
        Id = account.Id,
        Email = account.Email,
        Name = account.Name,
        EmailVerified = account.EmailVerified,
        Image = account.Image,
        PasswordHash = account.PasswordHash,
        SecurityStamp = account.SecurityStamp,
        ExternalLogins = [.. account.ExternalLogins],
    };

    public UserAccount ToAccount() => ApplyTo(new UserAccount { Id = Id, Email = Email });

    /// <summary>Copies what Identity may change onto the stored account. The id and email stay.</summary>
    public UserAccount ApplyTo(UserAccount account) => account with
    {
        Name = Name,
        EmailVerified = EmailVerified,
        Image = Image,
        PasswordHash = PasswordHash,
        SecurityStamp = SecurityStamp,
        ExternalLogins = [.. ExternalLogins],
    };
}

/// <summary>
/// The Identity user store over the account repository, and through it Cosmos DB.
/// Emails are unique because creating an account reserves its email lookup document;
/// the checks Identity makes before creating are only there for a friendlier error.
/// </summary>
internal sealed class AccountUserStore(IUserAccountRepository accounts, AccountDeletion deletion, IdentityErrorDescriber errors)
    : IUserPasswordStore<AppUser>, IUserEmailStore<AppUser>, IUserSecurityStampStore<AppUser>
{
    public async Task<IdentityResult> CreateAsync(AppUser user, CancellationToken cancellationToken)
    {
        var result = await accounts.CreateAsync(user.ToAccount(), cancellationToken);
        return result == CreateAccountResult.Created
            ? IdentityResult.Success
            : IdentityResult.Failed(errors.DuplicateEmail(user.Email));
    }

    public async Task<IdentityResult> UpdateAsync(AppUser user, CancellationToken cancellationToken)
    {
        // The repository writes only if nobody else wrote in between, and otherwise
        // applies these fields to the newer version.
        var updated = await accounts.UpdateAsync(user.Id, user.ApplyTo, cancellationToken);
        return updated is null ? IdentityResult.Failed(errors.ConcurrencyFailure()) : IdentityResult.Success;
    }

    public async Task<IdentityResult> DeleteAsync(AppUser user, CancellationToken cancellationToken)
    {
        await deletion.DeleteAsync(user.Id, cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<AppUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
        await accounts.GetAsync(userId, cancellationToken) is { } account ? AppUser.From(account) : null;

    // The repository normalizes the address itself, so Identity's normalized form is as good as any.
    public async Task<AppUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        await accounts.FindByEmailAsync(normalizedEmail, cancellationToken) is { } account ? AppUser.From(account) : null;

    public Task<AppUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
        FindByEmailAsync(normalizedUserName, cancellationToken);

    public Task<string> GetUserIdAsync(AppUser user, CancellationToken cancellationToken) => Task.FromResult(user.Id);

    public Task<string?> GetUserNameAsync(AppUser user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.Email);

    public Task<string?> GetNormalizedUserNameAsync(AppUser user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.Email);

    public Task<string?> GetEmailAsync(AppUser user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.Email);

    public Task<string?> GetNormalizedEmailAsync(AppUser user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.Email);

    // The user name is the email address, which cannot be changed, and the repository
    // keeps its own normalized copy.
    public Task SetUserNameAsync(AppUser user, string? userName, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetNormalizedUserNameAsync(AppUser user, string? normalizedName, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetEmailAsync(AppUser user, string? email, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The email address of an account cannot be changed.");

    public Task SetNormalizedEmailAsync(AppUser user, string? normalizedEmail, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> GetEmailConfirmedAsync(AppUser user, CancellationToken cancellationToken) => Task.FromResult(user.EmailVerified);

    public Task SetEmailConfirmedAsync(AppUser user, bool confirmed, CancellationToken cancellationToken)
    {
        user.EmailVerified = confirmed;
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(AppUser user, CancellationToken cancellationToken) => Task.FromResult(user.PasswordHash);

    public Task<bool> HasPasswordAsync(AppUser user, CancellationToken cancellationToken) => Task.FromResult(user.PasswordHash is not null);

    public Task SetPasswordHashAsync(AppUser user, string? passwordHash, CancellationToken cancellationToken)
    {
        user.PasswordHash = passwordHash;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(AppUser user, CancellationToken cancellationToken) => Task.FromResult(user.SecurityStamp);

    public Task SetSecurityStampAsync(AppUser user, string stamp, CancellationToken cancellationToken)
    {
        user.SecurityStamp = stamp;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}
