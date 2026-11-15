using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ElPaqueto.Security.ApiKeys;

namespace ElPaqueto.Security.Tests;

public class ApiKeyTokenTests
{
    private const string KeyId = "AbCdEf012345";
    private const string Secret = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefg";
    private const string Token = "epq_" + KeyId + "_" + Secret;

    [Fact]
    public void Constants_DescribeA60CharacterTokenWithAtLeast256BitsOfSecret()
    {
        Assert.Equal(60, ApiKeyToken.Length);
        Assert.Equal(ApiKeyToken.Length, Token.Length);
        Assert.Equal(62, ApiKeyGenerator.Alphabet.Distinct().Count());
        Assert.True(ApiKeyToken.SecretLength * Math.Log2(62) >= 256);
        Assert.True((ApiKeyToken.SecretLength - 1) * Math.Log2(62) < 256);
        Assert.True(ApiKeyToken.MaximumInputLength >= ApiKeyToken.Length);
    }

    [Fact]
    public void TryParse_SplitsAWellFormedToken()
    {
        Assert.True(ApiKeyToken.TryParse(Token, out var parts));
        Assert.Equal(KeyId, parts.KeyId);
        Assert.Equal(Secret, parts.Secret);
    }

    [Fact]
    public void ParsedToken_NeverPrintsItsSecret()
    {
        Assert.True(ApiKeyToken.TryParse(Token, out var parts));

        Assert.DoesNotContain(Secret, parts.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, $"{parts}", StringComparison.Ordinal);
        Assert.Contains(KeyId, parts.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<string?> MalformedTokens() => new()
    {
        null,
        string.Empty,
        "epq_",
        "EPQ_" + KeyId + "_" + Secret,
        "Epq_" + KeyId + "_" + Secret,
        "eqp_" + KeyId + "_" + Secret,
        " " + Token,
        Token + " ",
        Token + "\n",
        Token + "\r\n",
        "\t" + Token,
        Token[..^1],
        Token + "a",
        "epq_" + KeyId[..^1] + "_" + Secret + "a",
        "epq_" + KeyId + "a_" + Secret[..^1],
        "epq_" + KeyId + "-" + Secret,
        "epq-" + KeyId + "_" + Secret,
        "epq_" + KeyId + "_" + Secret[..^1] + "-",
        "epq_" + KeyId + "_" + Secret[..^1] + "=",
        "epq_" + KeyId + "_" + Secret[..^1] + "é",
        "epq_" + KeyId + "_" + Secret[..^1] + "Ａ", // fullwidth A
        "epq_" + KeyId + "_" + Secret[..^1] + "٠", // Arabic-Indic digit zero
        "epq_" + KeyId + "_" + Secret[..^1] + "\0",
        "epq_" + KeyId + "_" + "K" + Secret[1..], // Kelvin sign: equal to K only ignoring case
        "epq_" + KeyId + "_" + Secret + "_" + Secret,
        new string('a', ApiKeyToken.Length),
    };

    [Theory]
    [MemberData(nameof(MalformedTokens))]
    public void TryParse_RejectsAnythingButTheExactFormat(string? value)
    {
        Assert.False(ApiKeyToken.TryParse(value, out var parts));
        Assert.Null(parts.KeyId);
        Assert.False(ApiKeyToken.IsTokenFormat(value));
    }

    [Theory]
    [MemberData(nameof(MalformedTokens))]
    public void Pattern_AgreesWithTheParser(string? value)
    {
        Assert.Equal(
            ApiKeyToken.TryParse(value, out _),
            value is not null && Regex.IsMatch(value, ApiKeyToken.Pattern, RegexOptions.None, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void TryParse_OfAHugeInput_ReturnsQuicklyAndFalse()
    {
        var huge = "epq_" + new string('a', 5_000_000);

        Assert.False(ApiKeyToken.TryParse(huge, out _));
    }

    [Fact]
    public void TryMap_AcceptsExactly248ByteValues_EachCharacterHavingFourAntecedents()
    {
        var counts = new Dictionary<char, int>();
        var rejected = new List<int>();

        for (var value = 0; value <= byte.MaxValue; value++)
        {
            if (ApiKeyGenerator.TryMap((byte)value, out var character))
            {
                counts[character] = counts.GetValueOrDefault(character) + 1;
            }
            else
            {
                rejected.Add(value);
            }
        }

        Assert.Equal(Enumerable.Range(248, 8), rejected);
        Assert.Equal(62, counts.Count);
        Assert.All(counts.Values, count => Assert.Equal(4, count));
        Assert.Equal(ApiKeyGenerator.Alphabet.Order(), counts.Keys.Order());
    }

    [Fact]
    public void Generator_SkipsRejectedBytesInsteadOfFoldingThem()
    {
        var calls = 0;
        var generator = new ApiKeyGenerator(buffer =>
        {
            calls++;
            if (calls == 1)
            {
                buffer.Fill(0xFF); // all rejected
                return;
            }

            for (var i = 0; i < buffer.Length; i++)
            {
                // Rejected values interleaved with accepted ones.
                buffer[i] = i % 2 == 0 ? (byte)(i / 2) : (byte)(248 + (i % 8));
            }
        });

        var keyId = generator.GenerateKeyId();

        Assert.Equal(ApiKeyGenerator.Alphabet[..12], keyId);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void TenThousandGeneratedTokens_MatchTheFormatAndAreUnique()
    {
        var keyIds = new HashSet<string>(StringComparer.Ordinal);
        var secrets = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 10_000; i++)
        {
            var (keyId, secret) = ApiKeyGenerator.Default.GenerateToken();
            Assert.True(ApiKeyToken.IsTokenFormat(ApiKeyToken.Format(keyId, secret)));
            Assert.True(keyIds.Add(keyId));
            Assert.True(secrets.Add(secret));
        }
    }

    [Fact]
    public void GeneratedCharacters_AreSpreadOverTheWholeAlphabet()
    {
        var counts = new int[128];
        var total = 0;
        for (var i = 0; i < 10_000; i++)
        {
            foreach (var c in ApiKeyGenerator.Default.GenerateSecret())
            {
                counts[c]++;
                total++;
            }
        }

        // 430000 characters: about 6935 each, standard deviation about 82. 10 % is more than 8 deviations.
        var expected = total / 62.0;
        foreach (var c in ApiKeyGenerator.Alphabet)
        {
            Assert.InRange(counts[c], expected * 0.9, expected * 1.1);
        }
    }

    [Fact]
    public void SecretHash_IsTheSha256OfTheAsciiSecret()
    {
        var hash = ApiKeySecretHash.Compute(Secret);

        Assert.Equal(SHA256.HashData(Encoding.ASCII.GetBytes(Secret)), hash);
        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void SecretHash_RefusesAValueOfAnotherLengthWithoutRepeatingIt()
    {
        var exception = Assert.Throws<ArgumentException>(() => ApiKeySecretHash.Compute(Secret + "x"));

        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
    }
}
