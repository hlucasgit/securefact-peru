namespace SecureFact.Tenancy.Domain;

/// <summary>A subscription plan: what a tenant may hold and issue. A null limit means unlimited.</summary>
internal sealed class Plan
{
    private Plan()
    {
    }

    /// <summary>The plan every tenant had before plans existed. It limits nothing, so adding plans changes no behaviour until a tenant is moved.</summary>
    public static readonly Guid DefaultId = new("0f1e2d3c-4b5a-4968-8776-655443322110");

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public int? MaxCompanies { get; private set; }

    public int? MaxUsers { get; private set; }

    public int? MaxDocumentsPerMonth { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public uint Version { get; private set; }

    public static Plan Create(Guid id, string code, string name, int? maxCompanies, int? maxUsers, int? maxDocumentsPerMonth, DateTimeOffset now) => new()
    {
        Id = id,
        Code = code,
        Name = name,
        MaxCompanies = maxCompanies,
        MaxUsers = maxUsers,
        MaxDocumentsPerMonth = maxDocumentsPerMonth,
        IsActive = true,
        CreatedAt = now,
    };

    public void Update(string name, int? maxCompanies, int? maxUsers, int? maxDocumentsPerMonth, bool isActive)
    {
        Name = name;
        MaxCompanies = maxCompanies;
        MaxUsers = maxUsers;
        MaxDocumentsPerMonth = maxDocumentsPerMonth;
        IsActive = isActive;
    }
}
