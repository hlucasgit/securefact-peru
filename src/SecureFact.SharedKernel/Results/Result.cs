using System.Diagnostics.CodeAnalysis;

namespace SecureFact.SharedKernel.Results;

public readonly record struct Result<T>
{
    private readonly T? _value;
    private readonly Error? _error;

    private Result(T value)
    {
        _value = value;
        _error = null;
    }

    private Result(Error error)
    {
        _value = default;
        _error = error;
    }

    [MemberNotNullWhen(true, nameof(_value))]
    [MemberNotNullWhen(false, nameof(_error))]
    public bool IsSuccess => _error is null;

    public T Value => IsSuccess ? _value : throw new InvalidOperationException("Result is a failure; no value is available.");

    public Error Error => !IsSuccess ? _error : throw new InvalidOperationException("Result is a success; no error is available.");

    public static Result<T> Success(T value) => new(value);

    public static Result<T> Failure(Error error) => new(error);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);
}
