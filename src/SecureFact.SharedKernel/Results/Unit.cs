namespace SecureFact.SharedKernel.Results;

/// <summary>Value for operations that succeed without returning data: <c>Result&lt;Unit&gt;</c>.</summary>
public readonly record struct Unit
{
    public static Unit Value => default;
}
