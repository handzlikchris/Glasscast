using GlassesRemote.Server.Pairing;

namespace GlassesRemote.Server.Tests.Pairing;

public sealed class SecretsTests
{
    [Fact]
    public void Token_is_256_bits_base64url_and_unique()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => Secrets.NewToken()).ToList();

        Assert.All(tokens, t => Assert.Matches("^[A-Za-z0-9_-]{43}$", t));
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public void Token_matches_only_its_own_hash()
    {
        var token = Secrets.NewToken();
        var hash = Secrets.HashToken(token);

        Assert.True(Secrets.TokenMatches(token, hash));
        Assert.False(Secrets.TokenMatches(Secrets.NewToken(), hash));
        Assert.False(Secrets.TokenMatches("", hash));
        Assert.False(Secrets.TokenMatches(new string('x', 500), hash));
    }

    [Fact]
    public void Pairing_code_avoids_ambiguous_characters()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = Secrets.NewPairingCode();
            Assert.Matches("^[A-HJKMNP-Z2-9]{3}-[A-HJKMNP-Z2-9]{3}$", code);
        }
    }
}
