using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;

namespace SecureFact.Unit.Tests.SharedKernel;

public class SeriesAndNumberTests
{
    [Theory]
    [InlineData("F001", "F001")]
    [InlineData("b002", "B002")]
    [InlineData("FA01", "FA01")]
    public void Series_accepts_four_alphanumeric_characters(string input, string expected)
    {
        var result = Series.Create(input);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("F01")]
    [InlineData("F0001")]
    [InlineData("F-01")]
    public void Series_rejects_malformed_values(string? input)
    {
        var result = Series.Create(input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidSeries, result.Error.Code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99_999_999)]
    public void DocumentNumber_accepts_one_to_eight_digits(long value) =>
        Assert.True(DocumentNumber.Create(value).IsSuccess);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100_000_000)]
    public void DocumentNumber_rejects_out_of_range(long value)
    {
        var result = DocumentNumber.Create(value);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidDocumentNumber, result.Error.Code);
    }
}
