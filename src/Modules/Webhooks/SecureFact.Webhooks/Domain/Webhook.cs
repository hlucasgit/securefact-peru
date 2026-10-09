using SecureFact.Platform.Persistence;
using SecureFact.Webhooks.Contracts;

namespace SecureFact.Webhooks.Domain;

internal sealed class WebhookEndpoint : ITenantOwned
{
    /// <summary>Failed attempts in a row after which the endpoint is switched off: a dead address stops receiving, and the owner sees why.</summary>
    public const int FailureLimit = 40;

    private WebhookEndpoint()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Url { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string[] Events { get; private set; } = [];

    /// <summary>The secret that signs what is sent, envelope-encrypted (ADR-007): it has to be read back to sign, so a hash would not do.</summary>
    public byte[] SecretCiphertext { get; private set; } = [];

    public string SecretHint { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public DateTimeOffset? DisabledAt { get; private set; }

    public string? DisabledReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    public static WebhookEndpoint Create(Guid tenantId, string url, string? description, IEnumerable<string> events, bool isActive, byte[] secretCiphertext, string secretHint, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        Url = url,
        Description = description,
        Events = [.. events],
        SecretCiphertext = secretCiphertext,
        SecretHint = secretHint,
        IsActive = isActive,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Update(string url, string? description, IEnumerable<string> events, bool isActive, DateTimeOffset now)
    {
        Url = url;
        Description = description;
        Events = [.. events];
        if (isActive && !IsActive)
        {
            ConsecutiveFailures = 0;
            DisabledAt = null;
            DisabledReason = null;
        }
        else if (!isActive && IsActive)
        {
            DisabledAt = now;
            DisabledReason = "Desactivado por una persona";
        }

        IsActive = isActive;
        UpdatedAt = now;
    }

    public void ReplaceSecret(byte[] ciphertext, string hint, DateTimeOffset now)
    {
        SecretCiphertext = ciphertext;
        SecretHint = hint;
        UpdatedAt = now;
    }

    public void RecordSuccess(DateTimeOffset now)
    {
        if (ConsecutiveFailures != 0)
        {
            ConsecutiveFailures = 0;
            UpdatedAt = now;
        }
    }

    public void RecordFailure(DateTimeOffset now)
    {
        ConsecutiveFailures++;
        UpdatedAt = now;
        if (IsActive && ConsecutiveFailures >= FailureLimit)
        {
            IsActive = false;
            DisabledAt = now;
            DisabledReason = $"Falló {FailureLimit} veces seguidas";
        }
    }

    public void Disable(string reason, DateTimeOffset now)
    {
        IsActive = false;
        DisabledAt = now;
        DisabledReason = reason;
        UpdatedAt = now;
    }
}

internal sealed class WebhookDelivery : ITenantOwned
{
    private WebhookDelivery()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid EndpointId { get; private set; }

    /// <summary>The event it carries. With the endpoint it is unique: an event is delivered to an endpoint once, however many times the outbox hands it over.</summary>
    public Guid EventId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    /// <summary>The body that is sent, fixed when the delivery is made: every attempt sends the same bytes and signs the same content.</summary>
    public string Payload { get; private set; } = string.Empty;

    public WebhookDeliveryState State { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    public int? LastStatusCode { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    public uint Version { get; private set; }

    public static WebhookDelivery Create(Guid tenantId, Guid endpointId, Guid eventId, string eventType, string payload, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        EndpointId = endpointId,
        EventId = eventId,
        EventType = eventType,
        Payload = payload,
        State = WebhookDeliveryState.Pending,
        NextAttemptAt = now,
        CreatedAt = now,
    };

    public void CountAttempt() => Attempts++;

    public void MarkDelivered(int statusCode, DateTimeOffset now)
    {
        State = WebhookDeliveryState.Delivered;
        LastStatusCode = statusCode;
        LastError = null;
        NextAttemptAt = null;
        DeliveredAt = now;
    }

    public void MarkFailed(int? statusCode, string error, DateTimeOffset? next)
    {
        LastStatusCode = statusCode;
        LastError = error.Length > 300 ? error[..300] : error;
        State = next is null ? WebhookDeliveryState.Dead : WebhookDeliveryState.Failed;
        NextAttemptAt = next;
    }

    /// <summary>A person asks to send it again: the attempts start from zero and it is due now.</summary>
    public void Requeue(DateTimeOffset now)
    {
        State = WebhookDeliveryState.Pending;
        Attempts = 0;
        NextAttemptAt = now;
        DeliveredAt = null;
    }
}
