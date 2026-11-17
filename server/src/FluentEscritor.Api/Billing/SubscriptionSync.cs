using FluentEscritor.Api.Common;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Data.Models;
using Stripe;

namespace FluentEscritor.Api.Billing;

/// <summary>What a <c>customer.subscription.*</c> event says about the subscription, taken from its payload.</summary>
/// <param name="UserIdMetadata">The <c>userId</c> metadata Checkout puts on the subscriptions it creates.</param>
public sealed record SubscriptionSnapshot(
    string Id,
    string CustomerId,
    string Status,
    string? PriceId,
    DateTimeOffset? CurrentPeriodEnd,
    bool CancelAtPeriodEnd,
    string? UserIdMetadata)
{
    public static SubscriptionSnapshot From(Stripe.Subscription subscription)
    {
        var items = subscription.Items?.Data ?? [];

        // Billing periods live on the subscription items in current Stripe API versions.
        var ends = items.Select(i => i.CurrentPeriodEnd).Where(end => end > DateTime.UnixEpoch).ToList();

        return new(
            subscription.Id,
            subscription.CustomerId,
            subscription.Status,
            items.FirstOrDefault()?.Price?.Id,
            ends.Count == 0 ? null : new DateTimeOffset(DateTime.SpecifyKind(ends.Max(), DateTimeKind.Utc)),
            subscription.CancelAtPeriodEnd || subscription.CancelAt is not null,
            subscription.Metadata?.GetValueOrDefault("userId"));
    }
}

public enum SubscriptionApplyResult
{
    Applied,
    Stale,
    UnknownCustomer,
    Ignored,
}

/// <summary>
/// The user's Stripe customer, and the subscription state Stripe's webhook events carry.
/// Ported from <c>src/lib/billing/subscription.ts</c>.
/// </summary>
public sealed class SubscriptionSync(
    ISubscriptionRepository subscriptions,
    IUserAccountRepository accounts,
    IStripeGateway stripe,
    TimeProvider time)
{
    /// <summary>The <c>customer.subscription.*</c> events the webhook applies.</summary>
    public static readonly IReadOnlySet<string> SubscriptionEvents = new HashSet<string>
    {
        "customer.subscription.created",
        "customer.subscription.updated",
        "customer.subscription.deleted",
        "customer.subscription.paused",
        "customer.subscription.resumed",
    };

    /// <summary>The user's Stripe customer id, creating the customer on first use. One customer per user.</summary>
    public async Task<string> GetOrCreateCustomerAsync(UserAccount account, CancellationToken cancellationToken)
    {
        if (await subscriptions.GetAsync(account.Id, cancellationToken) is { } existing)
        {
            return existing.StripeCustomerId;
        }

        var customerId = await stripe.CreateCustomerAsync(account.Id, account.Email, account.Name, cancellationToken);

        // A concurrent request may have stored its customer first; that one is kept.
        var stored = await subscriptions.CreateAsync(account.Id, customerId, cancellationToken);
        return stored.StripeCustomerId;
    }

    /// <summary>
    /// Stores the state carried by a subscription event. The payload is the full subscription
    /// at that moment, so no API call is needed. Events can arrive late or out of order: one
    /// older than the last applied to the same subscription is skipped.
    /// </summary>
    /// <param name="eventCreated">The event's <c>created</c>, in Unix seconds.</param>
    public async Task<SubscriptionApplyResult> ApplyAsync(SubscriptionSnapshot subscription, long eventCreated, CancellationToken cancellationToken)
    {
        var row = await subscriptions.FindByStripeCustomerIdAsync(subscription.CustomerId, cancellationToken);
        if (row is null)
        {
            // A customer created outside the app (in the dashboard, say) but tagged with our user id.
            if (subscription.UserIdMetadata is not { Length: > 0 } userId
                || await accounts.GetAsync(userId, cancellationToken) is null)
            {
                return SubscriptionApplyResult.UnknownCustomer;
            }

            // A user who already has another customer keeps it, and this event matches nobody.
            await subscriptions.CreateAsync(userId, subscription.CustomerId, cancellationToken);
            row = await subscriptions.FindByStripeCustomerIdAsync(subscription.CustomerId, cancellationToken);
            if (row is null)
            {
                return SubscriptionApplyResult.UnknownCustomer;
            }
        }

        var result = SubscriptionApplyResult.Applied;
        await subscriptions.UpdateAsync(row.UserId, current =>
        {
            // Decided again on every retry, against the version actually being replaced.
            result = Decide(current, subscription, eventCreated);
            return result != SubscriptionApplyResult.Applied ? current : current with
            {
                StripeSubscriptionId = subscription.Id,
                Status = subscription.Status,
                PriceId = subscription.PriceId,
                CurrentPeriodEnd = subscription.CurrentPeriodEnd,
                CancelAtPeriodEnd = subscription.CancelAtPeriodEnd,
                LastEventAt = eventCreated,
                UpdatedAt = time.GetUtcNow(),
            };
        }, cancellationToken);

        return result;
    }

    internal static SubscriptionApplyResult Decide(Data.Models.Subscription current, SubscriptionSnapshot incoming, long eventCreated)
    {
        var sameSubscription = current.StripeSubscriptionId == incoming.Id;
        if (sameSubscription && eventCreated < current.LastEventAt)
        {
            return SubscriptionApplyResult.Stale;
        }

        // A second subscription on the same customer takes over only if it grants access and
        // the tracked one does not. The end of an old subscription then never downgrades a
        // user who has since subscribed again.
        if (current.StripeSubscriptionId is not null && !sameSubscription
            && (Plans.HasProAccess(current) || !Plans.IsProStatus(incoming.Status)))
        {
            return SubscriptionApplyResult.Ignored;
        }

        return SubscriptionApplyResult.Applied;
    }

    /// <summary>The wire name of a result, as the Next.js webhook reported it.</summary>
    public static string WireName(SubscriptionApplyResult result) => result switch
    {
        SubscriptionApplyResult.Applied => "applied",
        SubscriptionApplyResult.Stale => "stale",
        SubscriptionApplyResult.UnknownCustomer => "unknown_customer",
        _ => "ignored",
    };
}

/// <summary>Verifies and reads Stripe webhook deliveries.</summary>
public sealed class StripeWebhookReader(StripeSettings settings, TimeProvider time)
{
    /// <summary>How old a signature may be, as in Stripe's libraries.</summary>
    public const long ToleranceSeconds = 300;

    /// <summary>The event, or null when the signature is missing, wrong or too old.</summary>
    public Event? Read(string payload, string? signature)
    {
        if (string.IsNullOrEmpty(signature) || settings.WebhookSecret is not { } secret)
        {
            return null;
        }

        try
        {
            // Events are sent in the account's API version, which may differ from the
            // library's; the fields read here are the same in both.
            return EventUtility.ConstructEvent(
                payload,
                signature,
                secret,
                ToleranceSeconds,
                time.GetUtcNow().ToUnixTimeSeconds(),
                throwOnApiVersionMismatch: false);
        }
        catch (Exception)
        {
            // A bad signature, or a body that is not an event: either way nothing to trust.
            return null;
        }
    }
}
