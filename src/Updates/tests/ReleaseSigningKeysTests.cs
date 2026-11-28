using AiDevHelpers.Updates;
using Xunit;

namespace AiDevHelpers.Updates.Tests;

public sealed class ReleaseSigningKeysTests
{
    [Fact]
    public void ReleaseKeyIsA32ByteEd25519PublicKey()
    {
        PackageSigningKey key = ReleaseSigningKeys.Current();

        Assert.True(key.Active);
        Assert.Equal(32, key.PublicKey.Length);
    }
}
