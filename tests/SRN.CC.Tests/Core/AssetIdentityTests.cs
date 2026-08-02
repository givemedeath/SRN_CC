using NUnit.Framework;
using FluentAssertions;
using SRN.CC.Core.Identity;

namespace SRN.CC.Tests.Core;

[TestFixture]
public class AssetIdentityTests
{
    [Test]
    public void ValidResref_ShouldEncodeAndNormalize()
    {
        var identity = new AssetIdentity("My_ResRef_01", 2000);
        identity.ResourceType.Should().Be(2000);
        identity.OriginalName.Should().Be("My_ResRef_01");
        identity.CanonicalResrefBytes.Should().Equal("my_resref_01"u8.ToArray());
    }

    [Test]
    public void ASCIIUppercase_ShouldFoldToLowercase()
    {
        var id1 = new AssetIdentity("TESTITEM", 2000);
        var id2 = new AssetIdentity("testitem", 2000);

        id1.Should().Be(id2);
        id1.GetHashCode().Should().Be(id2.GetHashCode());
    }

    [Test]
    public void CP1252Accents_ShouldBePreservedWithoutUnicodeFolding()
    {
        // 'é' in CP1252 is 0xE9
        var identity = new AssetIdentity("café", 2000);
        identity.CanonicalResrefBytes.Should().Equal(new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9 });
    }

    [Test]
    public void ResrefLength_Outside1To16Bytes_ShouldThrow()
    {
        Action empty = () => _ = new AssetIdentity("", 2000);
        Action tooLong = () => _ = new AssetIdentity("12345678901234567", 2000);

        empty.Should().Throw<ArgumentException>();
        tooLong.Should().Throw<ArgumentException>();
    }

    [Test]
    public void InvalidCharacters_ShouldThrow()
    {
        Action withSlash = () => _ = new AssetIdentity("test/item", 2000);
        Action withBackslash = () => _ = new AssetIdentity("test\\item", 2000);

        withSlash.Should().Throw<ArgumentException>();
        withBackslash.Should().Throw<ArgumentException>();
    }

    [Test]
    public void SameResrefDifferentType_ShouldNotBeEqual()
    {
        var id1 = new AssetIdentity("test", 2000);
        var id2 = new AssetIdentity("test", 2001);

        id1.Should().NotBe(id2);
    }
}
