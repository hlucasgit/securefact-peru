using SecureFact.SharedKernel;
using SecureFact.SharedKernel.Domain;

namespace SecureFact.Unit.Tests.SharedKernel;

public class RucTests
{
    [Theory]
    [InlineData("20100066603")] // example RUC used in SUNAT's Programmer Manual
    [InlineData(" 20100066603 ")]
    public void Create_accepts_valid_ruc(string input)
    {
        var result = Ruc.Create(input);

        Assert.True(result.IsSuccess);
        Assert.Equal("20100066603", result.Value.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2010006660")]
    [InlineData("201000666033")]
    [InlineData("2010006660A")]
    [InlineData("20100066604")] // wrong check digit
    public void Create_rejects_invalid_ruc(string? input)
    {
        var result = Ruc.Create(input);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.InvalidRuc, result.Error.Code);
    }
}
