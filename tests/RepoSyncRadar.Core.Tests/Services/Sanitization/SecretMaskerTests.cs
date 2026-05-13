using RepoSyncRadar.Core.Services.Sanitization;
using Xunit;

namespace RepoSyncRadar.Core.Tests.Services.Sanitization;

public class SecretMaskerTests
{
    [Fact]
    public void Mask_Replaces_GitHub_Personal_Access_Token()
    {
        // ghp_ followed by 36+ alphanumerics. Length 40 is the common form.
        const string pat = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghij1234";
        var input = $"token={pat} trailing";

        var masked = SecretMasker.Mask(input);

        Assert.DoesNotContain(pat, masked, StringComparison.Ordinal);
        Assert.Contains("***GITHUB_PAT***", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_Replaces_OpenAI_Style_Api_Key()
    {
        const string key = "sk-ABCDEFGHIJKLMNOPQRSTUVWX1234";
        var input = $"OPENAI_API_KEY={key}";

        var masked = SecretMasker.Mask(input);

        Assert.DoesNotContain(key, masked, StringComparison.Ordinal);
        Assert.Contains("***OPENAI_KEY***", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_Replaces_Jwt_Token()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.S-AbCdEfGhIjKlMn";
        var input = $"Authorization: Bearer {jwt}";

        var masked = SecretMasker.Mask(input);

        Assert.DoesNotContain(jwt, masked, StringComparison.Ordinal);
        Assert.Contains("***JWT***", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_Replaces_Email_Address()
    {
        const string email = "foo@example.com";
        var input = $"contact: {email}";

        var masked = SecretMasker.Mask(input);

        Assert.DoesNotContain(email, masked, StringComparison.Ordinal);
        Assert.Contains("***EMAIL***", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_Replaces_Ipv4_Address()
    {
        const string ip = "192.168.1.10";
        var input = $"server={ip}:8080";

        var masked = SecretMasker.Mask(input);

        Assert.DoesNotContain(ip, masked, StringComparison.Ordinal);
        Assert.Contains("***IPV4***", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_Does_Not_Remask_Already_Masked_Tokens_And_Still_Masks_Real_Secrets()
    {
        // The "後勝ち / 既マスク部分を再マスクしない" case from the plan:
        //   - a pre-masked ***EMAIL*** marker must survive unchanged
        //   - a real, unmasked secret on the same line must still be redacted
        //   - the final output must contain exactly one occurrence of ***EMAIL***
        //     for the pre-mask plus one for the real secret (no nesting)
        var input = "pre=***EMAIL*** real=alice@example.com pat=ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghij1234";

        var masked = SecretMasker.Mask(input);

        Assert.DoesNotContain("alice@example.com", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghij1234", masked, StringComparison.Ordinal);
        Assert.Contains("***GITHUB_PAT***", masked, StringComparison.Ordinal);

        // Both the pre-existing marker and the freshly-masked email collapse to the
        // same literal token; they must not be wrapped again as ***EMAIL*****EMAIL***.
        var emailHits = CountOccurrences(masked, "***EMAIL***");
        Assert.Equal(2, emailHits);

        // No nested masking artefacts.
        Assert.DoesNotContain("***EMAIL*****", masked, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }

        return count;
    }
}
