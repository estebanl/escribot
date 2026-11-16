using System.Reflection;
using System.Text.Json;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Data.Models;
using FluentEscritor.Api.Storage;
using Microsoft.AspNetCore.Identity;

namespace FluentEscritor.Api.Auth;

/// <summary>What every new account starts with: default settings and the starter Vault.</summary>
public sealed class NewUserProvisioner(ISettingsRepository settings, IVaultRepository vault, TimeProvider time)
{
    private const string ResourceName = "FluentEscritor.Api.Data.Seed.starter-vault.json";

    private static readonly Lazy<IReadOnlyList<VaultItem>> StarterVault = new(LoadStarterVault);

    public async Task ProvisionAsync(string userId, CancellationToken cancellationToken = default)
    {
        await settings.GetOrCreateAsync(userId, cancellationToken);

        var now = time.GetUtcNow();
        var items = StarterVault.Value
            .Select(item => item with { Id = $"vault-{Guid.NewGuid()}", NextReviewAt = now, CreatedAt = now })
            .ToList();
        await vault.SaveManyAsync(userId, items, cancellationToken);
    }

    internal static IReadOnlyList<VaultItem> LoadStarterVault()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var document = JsonDocument.Parse(stream);

        return document.RootElement.EnumerateArray()
            .Select(entry => new VaultItem
            {
                Id = "starter",
                ItemType = entry.GetProperty("itemType").GetString()!,
                Criterion = entry.GetProperty("criterion").GetString()!,
                OriginalText = entry.GetProperty("originalText").GetString()!,
                UpgradedText = entry.GetProperty("upgradedText").GetString()!,
                Explanation = entry.GetProperty("explanation").GetString()!,
                ExampleSentence = entry.GetProperty("exampleSentence").GetString()!,
            })
            .ToList();
    }
}

/// <summary>
/// Erases everything the app holds about a user: their files first, then every document,
/// with the lookups and the account document last. A failure part way therefore leaves
/// an account that can still sign in and delete again, never files or documents without one.
/// </summary>
public sealed class AccountDeletion(IImageRepository images, IFileStore files, IUserDataRepository userData)
{
    public async Task DeleteAsync(string userId, CancellationToken cancellationToken = default)
    {
        // Billing is not on this server yet. When it is, the Stripe customer must be
        // deleted here, before anything else, so a deleted user is never billed again.
        foreach (var image in await images.ListAsync(userId, cancellationToken))
        {
            await files.DeleteAsync(image.BlobName, cancellationToken);
        }

        await userData.DeleteAllAsync(userId, cancellationToken);
    }
}

public enum AccountCreation
{
    Created,
    EmailTaken,
    NotAllowed,
    Invalid,

    /// <summary>An account with this address exists, and the external sign-in is not proof enough to enter it.</summary>
    NotLinked,
}

/// <param name="IsNew">The account was created by this call.</param>
public sealed record AccountCreationResult(AccountCreation Outcome, AppUser? User = null, string? Error = null, bool IsNew = false);

/// <summary>Creating accounts, by password or through an external sign-in, under the same rules.</summary>
public sealed partial class AccountService(
    UserManager<AppUser> users,
    AuthSettings settings,
    NewUserProvisioner provisioner,
    IAuthSessionStore sessions,
    TimeProvider time,
    ILogger<AccountService> logger)
{
    public const string SignupNotAllowedMessage = "Sign-ups are currently invite-only.";

    /// <param name="password">Null for an account that signs in only through an external provider.</param>
    public async Task<AccountCreationResult> CreateAsync(AppUser user, string? password, CancellationToken cancellationToken = default)
    {
        if (!settings.IsSignupAllowed(user.Email))
        {
            return new(AccountCreation.NotAllowed, Error: SignupNotAllowedMessage);
        }

        var result = password is null ? await users.CreateAsync(user) : await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            var duplicate = result.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName));
            return duplicate
                ? new(AccountCreation.EmailTaken, Error: "An account with this email already exists.")
                : new(AccountCreation.Invalid, Error: result.Errors.First().Description);
        }

        try
        {
            await provisioner.ProvisionAsync(user.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The account exists and works without this: settings are created on first
            // use anyway. Failing the sign-up here would leave an account the person
            // was told does not exist.
            LogProvisioningFailed(logger, ex);
        }

        return new(AccountCreation.Created, user, IsNew: true);
    }

    /// <summary>
    /// Finds or creates the account for someone who signed in with an external provider.
    /// <para>
    /// An account already linked to this provider identity is simply entered. Otherwise
    /// accounts are matched by email address, and that is only safe when the provider
    /// proves the person controls the address now (<paramref name="emailProven"/>). When
    /// it does not, an existing account is never entered or linked, and a new account
    /// starts unverified like one created with a password.
    /// </para>
    /// </summary>
    public async Task<AccountCreationResult> SignInExternalAsync(
        string provider,
        string providerKey,
        string email,
        bool emailProven,
        string? name,
        string? image,
        CancellationToken cancellationToken = default)
    {
        var login = new ExternalLogin(provider, providerKey, time.GetUtcNow());
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            var created = await CreateAsync(
                new AppUser { Email = email, Name = name ?? string.Empty, Image = image, EmailVerified = emailProven, ExternalLogins = [login] },
                password: null,
                cancellationToken);
            if (created.Outcome != AccountCreation.EmailTaken)
            {
                return created;
            }

            // Someone created the account between the two calls: treat it as existing.
            user = await users.FindByEmailAsync(email);
            if (user is null)
            {
                return created;
            }
        }

        var linked = user.ExternalLogins.Any(l => l.Provider == provider && l.ProviderKey == providerKey);
        if (!linked && !emailProven)
        {
            return new(AccountCreation.NotLinked, Error: "An account with this email already exists. Sign in with your password.");
        }

        var changed = false;
        if (emailProven && !user.EmailVerified)
        {
            // Until now nobody had shown that they control this address, so whoever set
            // the password may not be its owner. The provider has now vouched for this
            // person: the unproven password and any session opened with it are dropped,
            // or the other party would keep access to the account.
            if (user.PasswordHash is not null)
            {
                user.PasswordHash = null;
                await sessions.RemoveAllForUserAsync(user.Id, cancellationToken);
            }

            user.EmailVerified = true;
            changed = true;
        }

        if (!linked)
        {
            user.ExternalLogins.Add(login);
            changed = true;
        }

        if (changed)
        {
            // Changing the security stamp also saves the user.
            var result = await users.UpdateSecurityStampAsync(user);
            if (!result.Succeeded)
            {
                return new(AccountCreation.Invalid, Error: "Could not sign you in. Please try again.");
            }
        }

        return new(AccountCreation.Created, user);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A new account was created but could not be given its starting data.")]
    private static partial void LogProvisioningFailed(ILogger logger, Exception exception);
}
