using System.Collections.Concurrent;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Data.Models;
using FluentEscritor.Api.Email;
using FluentEscritor.Api.Storage;

namespace FluentEscritor.Api.Tests;

/// <summary>
/// The repositories sign-in uses, kept in memory, so its tests need no emulator. They
/// follow the documented behaviour of the real ones; RepositoryTests checks the real ones.
/// </summary>
public sealed class InMemoryData(TimeProvider time) :
    IUserAccountRepository, IAuthSessionStore, IOneTimeTokenStore, ISettingsRepository,
    IVaultRepository, IImageRepository, IRateLimiter, IUserDataRepository, IErrorEventRepository
{
    private readonly object _gate = new();

    public Dictionary<string, UserAccount> Accounts { get; } = [];

    public Dictionary<string, AuthSession> Sessions { get; } = [];

    public Dictionary<(OneTimeTokenPurpose Purpose, string Token), (string UserId, DateTimeOffset ExpiresAt)> Tokens { get; } = [];

    public Dictionary<string, UserSettings> Settings { get; } = [];

    public Dictionary<string, List<VaultItem>> Vault { get; } = [];

    public Dictionary<string, List<ImageMetadata>> Images { get; } = [];

    public ConcurrentDictionary<string, int> RateCounts { get; } = new();

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();

    // ---- Accounts ----

    public Task<CreateAccountResult> CreateAsync(UserAccount account, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var normalized = Normalize(account.Email);
            if (Accounts.Values.Any(a => a.NormalizedEmail == normalized))
            {
                return Task.FromResult(CreateAccountResult.EmailTaken);
            }

            var now = time.GetUtcNow();
            Accounts[account.Id] = account with { NormalizedEmail = normalized, CreatedAt = now, UpdatedAt = now };
            return Task.FromResult(CreateAccountResult.Created);
        }
    }

    public Task<UserAccount?> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Accounts.GetValueOrDefault(userId));
        }
    }

    public Task<UserAccount?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Accounts.Values.FirstOrDefault(a => a.NormalizedEmail == Normalize(email)));
        }
    }

    public Task<UserAccount?> UpdateAsync(string userId, Func<UserAccount, UserAccount> change, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!Accounts.TryGetValue(userId, out var current))
            {
                return Task.FromResult<UserAccount?>(null);
            }

            var updated = change(current) with { Id = current.Id, Email = current.Email, NormalizedEmail = current.NormalizedEmail, UpdatedAt = time.GetUtcNow() };
            Accounts[userId] = updated;
            return Task.FromResult<UserAccount?>(updated);
        }
    }

    // ---- Sessions ----

    public Task CreateAsync(string token, string userId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Sessions.Add(token, new AuthSession(userId, time.GetUtcNow(), expiresAt));
            return Task.CompletedTask;
        }
    }

    public Task<AuthSession?> FindAsync(string token, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var session = Sessions.GetValueOrDefault(token);
            return Task.FromResult(session is not null && session.ExpiresAt > time.GetUtcNow() ? session : null);
        }
    }

    public async Task<bool> ExtendAsync(string token, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        var session = await FindAsync(token, cancellationToken);
        lock (_gate)
        {
            if (session is null)
            {
                return false;
            }

            Sessions[token] = session with { ExpiresAt = expiresAt };
            return true;
        }
    }

    public Task RemoveAsync(string token, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Sessions.Remove(token);
            return Task.CompletedTask;
        }
    }

    public Task RemoveAllForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var token in Sessions.Where(s => s.Value.UserId == userId).Select(s => s.Key).ToList())
            {
                Sessions.Remove(token);
            }

            return Task.CompletedTask;
        }
    }

    // ---- Emailed tokens ----

    public Task CreateAsync(OneTimeTokenPurpose purpose, string token, string userId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Tokens.Add((purpose, token), (userId, expiresAt));
            return Task.CompletedTask;
        }
    }

    public Task<string?> FindUserIdAsync(OneTimeTokenPurpose purpose, string token, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Tokens.TryGetValue((purpose, token), out var entry) && entry.ExpiresAt > time.GetUtcNow() ? entry.UserId : null);
        }
    }

    public Task<string?> RedeemAsync(OneTimeTokenPurpose purpose, string token, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Tokens.Remove((purpose, token), out var entry) && entry.ExpiresAt > time.GetUtcNow() ? entry.UserId : null);
        }
    }

    public Task RemoveAllForUserAsync(OneTimeTokenPurpose purpose, string userId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var key in Tokens.Where(t => t.Key.Purpose == purpose && t.Value.UserId == userId).Select(t => t.Key).ToList())
            {
                Tokens.Remove(key);
            }

            return Task.CompletedTask;
        }
    }

    // ---- Settings ----

    public Task<UserSettings> GetOrCreateAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!Settings.TryGetValue(userId, out var settings))
            {
                Settings[userId] = settings = new UserSettings { UserId = userId, UpdatedAt = time.GetUtcNow() };
            }

            return Task.FromResult(settings);
        }
    }

    public async Task<UserSettings> UpdateAsync(string userId, Func<UserSettings, UserSettings> change, CancellationToken cancellationToken = default)
    {
        var current = await GetOrCreateAsync(userId, cancellationToken);
        lock (_gate)
        {
            return Settings[userId] = change(current);
        }
    }

    // ---- Vault ----

    public Task<IReadOnlyList<VaultItem>> ListAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<VaultItem>>([.. Vault.GetValueOrDefault(userId) ?? []]);
        }
    }

    Task<VaultItem?> IVaultRepository.GetAsync(string userId, string itemId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task SaveAsync(string userId, VaultItem item, CancellationToken cancellationToken = default) =>
        SaveManyAsync(userId, [item], cancellationToken);

    public Task SaveManyAsync(string userId, IReadOnlyList<VaultItem> items, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!Vault.TryGetValue(userId, out var list))
            {
                Vault[userId] = list = [];
            }

            list.AddRange(items);
            return Task.CompletedTask;
        }
    }

    Task<bool> IVaultRepository.DeleteAsync(string userId, string itemId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    // ---- Images ----

    Task<ImageMetadata?> IImageRepository.GetAsync(string userId, string imageId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<IReadOnlyList<ImageMetadata>> IImageRepository.ListAsync(string userId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ImageMetadata>>([.. Images.GetValueOrDefault(userId) ?? []]);
        }
    }

    public Task SaveAsync(string userId, ImageMetadata image, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!Images.TryGetValue(userId, out var list))
            {
                Images[userId] = list = [];
            }

            list.Add(image);
            return Task.CompletedTask;
        }
    }

    Task<bool> IImageRepository.DeleteAsync(string userId, string imageId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<long> IImageRepository.GetTotalBytesAsync(string userId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    // ---- Rate limits ----

    public Task<RateLimitResult> CheckAsync(string key, int limit, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var seconds = (long)window.TotalSeconds;
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var windowStart = now - (now % seconds);
        var count = RateCounts.AddOrUpdate($"{key}:{windowStart}", 1, (_, current) => current + 1);
        return Task.FromResult(new RateLimitResult(count <= limit, (int)Math.Max(1, windowStart + seconds - now)));
    }

    // ---- Everything a user owns ----

    Task<UserDataExport> IUserDataRepository.ExportAsync(string userId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task IUserDataRepository.ImportAsync(string userId, UserDataImport import, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    // ---- Error events ----

    public List<ErrorEvent> Errors { get; } = [];

    public Task RecordAsync(ErrorEvent errorEvent, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Errors.Add(errorEvent);
            return Task.CompletedTask;
        }
    }

    Task<IReadOnlyList<ErrorEvent>> IErrorEventRepository.ListRecentAsync(int limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<int> IErrorEventRepository.CountSinceAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public async Task DeleteAllAsync(string userId, CancellationToken cancellationToken = default)
    {
        await RemoveAllForUserAsync(userId, cancellationToken);
        await RemoveAllForUserAsync(OneTimeTokenPurpose.EmailVerification, userId, cancellationToken);
        await RemoveAllForUserAsync(OneTimeTokenPurpose.PasswordReset, userId, cancellationToken);
        lock (_gate)
        {
            Settings.Remove(userId);
            Vault.Remove(userId);
            Images.Remove(userId);
            Accounts.Remove(userId);
        }
    }
}

/// <summary>Keeps the emails the app would have sent.</summary>
public sealed class RecordingEmailSender : IEmailSender
{
    public List<OutgoingEmail> Sent { get; } = [];

    /// <summary>Set to make every delivery fail.</summary>
    public bool Fail { get; set; }

    public Task SendAsync(OutgoingEmail email, CancellationToken cancellationToken = default)
    {
        if (Fail)
        {
            throw new EmailDeliveryException("Email delivery failed (500).");
        }

        Sent.Add(email);
        return Task.CompletedTask;
    }
}

/// <summary>A file store in memory that also remembers what it was asked to delete.</summary>
public sealed class FakeFileStore(Exception? failure = null) : IFileStore
{
    public List<string> Deleted { get; } = [];

    public Task PingAsync(CancellationToken cancellationToken) =>
        failure is null ? Task.CompletedTask : Task.FromException(failure);

    public Dictionary<string, byte[]> Saved { get; } = [];

    public Task SaveAsync(string name, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        Saved[name] = content.ToArray();
        return Task.CompletedTask;
    }

    public Task<byte[]?> ReadAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(Saved.GetValueOrDefault(name));

    public Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        Saved.Remove(name);
        Deleted.Add(name);
        return Task.CompletedTask;
    }
}
