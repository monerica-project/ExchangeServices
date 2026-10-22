using System.Security.Cryptography;
using System.Text;
using ExchangeServices.Implementations;
using Xunit;

namespace ExchangeServices.Tests;

public class AnonChangeSigningTests
{
    // Expected signature computed independently with the openssl recipe from https://anonchange.com/docs#authentication.
    [Fact]
    public void Signature_MatchesDocumentedRecipe()
    {
        const string body = "{\"from\":\"BTC\",\"to\":\"XMR\",\"rateType\":\"float\",\"amount\":\"0.01\"}";
        var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var signed = AnonChangeClient.BuildSignedString("1792500000", "abcdef0123456789abcdef0123456789", "/api/partner/v1/price", bodyHash);
        var sign = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("test_secret"), Encoding.UTF8.GetBytes(signed))).ToLowerInvariant();

        Assert.Equal("905c1b802508e3b93bf03edc3353ffa84c125309e0b64307c9aeeeb93074a22c", sign);
        Assert.Equal($"1792500000\nabcdef0123456789abcdef0123456789\nPOST\n/api/partner/v1/price\n\n{bodyHash}", signed);
    }
}

public class AnonChangeCoinTests
{
    [Theory]
    [InlineData("USDT", "Tron", "USDT_ETH")]
    [InlineData("USDT", "BSC", "USDT_ETH")]
    [InlineData("USDT", "", "USDT_ETH")]
    [InlineData("USDT", "Ethereum", "USDT_ETH")]
    [InlineData("USDT", "Solana", "USDT_SOL")]
    [InlineData("XMR", "", "XMR")]
    [InlineData("btc", null, "BTC")]
    public void Coin_UsesAWorkingNetwork(string ticker, string? network, string expected)
        => Assert.Equal(expected, AnonChangeClient.Coin(new ExchangeServices.Abstractions.AssetRef(ticker, network ?? string.Empty)));
}
