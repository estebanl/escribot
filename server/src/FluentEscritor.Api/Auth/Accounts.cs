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
/// Erases everything the app holds about a user: their files first, then every document
/// with the account document last, so a failure part way never leaves files or documents
/// behind without an account.
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
}

public sealed record AccountCreationResult(AccountCreation Outcome, AppUser? User = null, string? Error = null);

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

        return new(AccountCreation.Created, user);
    }

    /// <summary>
    /// Finds or creates the account for someone who signed in with an external provider
    /// that has verified their email address. Accounts are matched by that address.
    /// </summary>
    public async Task<AccountCreationResult> SignInExternalAsync(
        string provider,
        string providerKey,
        string email,
        string? name,
        string? image,
        CancellationToken cancellationToken = default)
    {
        var login = new ExternalLogin(provider, providerKey, time.GetUtcNow());
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            var created = await CreateAsync(
                new AppUser { Email = email, Name = name ?? string.Empty, Image = image, EmailVerified = true, ExternalLogins = [login] },
                password: null,
                cancellationToken);
            if (created.Outcome != AccountCreation.EmailTaken)
            {
                return created;
            }

            // Someone created the account between the two calls: link to it instead.
            user = await users.FindByEmailAsync(email);
            if (user is null)
            {
                return created;
            }
        }

        var changed = false;
        if (!user.EmailVerified)
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

        if (!user.ExternalLogins.Any(l => l.Provider == provider && l.ProviderKey == providerKey))
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
