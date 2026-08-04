using System.Text;
using NUnit.Framework;
using SRN.CC.Preview.Render;

namespace SRN.CC.Tests.Preview.Render;

[TestFixture]
public class MtrDocumentTests
{
    [Test]
    public void Parse_WithAllDirectives_PopulatesEveryProperty()
    {
        // Arrange
        var mtrContent = @"// full material directive coverage
texture0 tno01_wall01
texture1 tno01_wall01_n
texture2 tno01_wall01_s
texture3 tno01_wall01_e
envmap CM_Water
bumpmap tno01_wall01_bump
renderhint NORMAL
customshadervs custom_material.vs
customshaderfs custom_material.fs
parameter isreflective 1
parameter tintcolor 1.0 0.5 0.25
";
        var bytes = Encoding.UTF8.GetBytes(mtrContent);

        // Act
        var document = MtrDocument.Parse(bytes);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(document.Texture0, Is.EqualTo("tno01_wall01"));
            Assert.That(document.Texture1, Is.EqualTo("tno01_wall01_n"));
            Assert.That(document.Texture2, Is.EqualTo("tno01_wall01_s"));
            Assert.That(document.Texture3, Is.EqualTo("tno01_wall01_e"));
            Assert.That(document.EnvironmentMap, Is.EqualTo("CM_Water"));
            Assert.That(document.BumpMap, Is.EqualTo("tno01_wall01_bump"));
            Assert.That(document.RenderHint, Is.EqualTo("NORMAL"));
            Assert.That(document.CustomShaderVertex, Is.EqualTo("custom_material.vs"));
            Assert.That(document.CustomShaderFragment, Is.EqualTo("custom_material.fs"));
            Assert.That(document.Parameters["isreflective"], Is.EqualTo("1"));
            Assert.That(document.Parameters["tintcolor"], Is.EqualTo("1.0 0.5 0.25"));
            Assert.That(document.Diagnostics, Is.Empty);
        });
    }

    [Test]
    public void Parse_WithEqualsSignSyntax_IsToleratedLikeWhitespace()
    {
        // Arrange - the ad-hoc parser this replaces historically emitted "key=value" test fixtures;
        // the shared parser must keep accepting that shape as well as canonical whitespace-separated
        // directives, so DependencyAnalyzer and any pre-existing fixtures do not regress.
        var mtrContent = "texture0=tex_diffuse\ntexture1=tex_specular\n";
        var bytes = Encoding.UTF8.GetBytes(mtrContent);

        // Act
        var document = MtrDocument.Parse(bytes);

        // Assert
        Assert.That(document.Texture0, Is.EqualTo("tex_diffuse"));
        Assert.That(document.Texture1, Is.EqualTo("tex_specular"));
    }

    [Test]
    public void Parse_WithEnvironmentMapDirectiveAlias_PopulatesEnvironmentMap()
    {
        // Arrange
        var mtrContent = "environmentmap CM_Sky\n";
        var bytes = Encoding.UTF8.GetBytes(mtrContent);

        // Act
        var document = MtrDocument.Parse(bytes);

        // Assert
        Assert.That(document.EnvironmentMap, Is.EqualTo("CM_Sky"));
    }

    [Test]
    public void Parse_WithCommentsAndBlankLines_SkipsThem()
    {
        // Arrange
        var mtrContent = @"// leading comment
# hash comment style is tolerated too

texture0 tex_main

// trailing comment
";
        var bytes = Encoding.UTF8.GetBytes(mtrContent);

        // Act
        var document = MtrDocument.Parse(bytes);

        // Assert
        Assert.That(document.Texture0, Is.EqualTo("tex_main"));
        Assert.That(document.Diagnostics, Is.Empty);
    }

    [Test]
    public void Parse_WithUnknownDirectives_TreatsThemAsTolerated()
    {
        // Arrange
        var mtrContent = @"texture0 tex_main
futurefeature somevalue
alphatest 0.5
";
        var bytes = Encoding.UTF8.GetBytes(mtrContent);

        // Act
        var document = MtrDocument.Parse(bytes);

        // Assert - unknown directives don't fail parsing and don't raise diagnostics.
        Assert.That(document.Texture0, Is.EqualTo("tex_main"));
        Assert.That(document.Diagnostics, Is.Empty);
    }

    [Test]
    public void Parse_WithMultipleParameterValueTokens_JoinsRemainderAsValue()
    {
        // Arrange
        var mtrContent = "parameter tintcolor 1.0 0.5 0.25\n";
        var bytes = Encoding.UTF8.GetBytes(mtrContent);

        // Act
        var document = MtrDocument.Parse(bytes);

        // Assert
        Assert.That(document.Parameters["tintcolor"], Is.EqualTo("1.0 0.5 0.25"));
    }

    [Test]
    public void Parse_WithParameterMissingName_AddsDiagnosticAndDoesNotThrow()
    {
        // Arrange
        var mtrContent = "parameter\n";
        var bytes = Encoding.UTF8.GetBytes(mtrContent);

        // Act
        var document = MtrDocument.Parse(bytes);

        // Assert
        Assert.That(document.Parameters, Is.Empty);
        Assert.That(document.Diagnostics, Has.Count.EqualTo(1));
    }

    [Test]
    public void Parse_WithDirectiveMissingValue_LeavesPropertyNullAndAddsDiagnostic()
    {
        // Arrange
        var mtrContent = "texture0\ntexture1 tex_valid\n";
        var bytes = Encoding.UTF8.GetBytes(mtrContent);

        // Act
        var document = MtrDocument.Parse(bytes);

        // Assert
        Assert.That(document.Texture0, Is.Null);
        Assert.That(document.Texture1, Is.EqualTo("tex_valid"));
        Assert.That(document.Diagnostics, Has.Count.EqualTo(1));
    }

    [Test]
    public void Parse_WithEmptyPayload_ReturnsDocumentWithDiagnosticAndNoThrow()
    {
        // Act
        var document = MtrDocument.Parse(ReadOnlySpan<byte>.Empty);

        // Assert
        Assert.That(document.Texture0, Is.Null);
        Assert.That(document.Diagnostics, Is.Not.Empty);
    }

    [Test]
    public void Parse_WithBinaryGarbage_DoesNotThrowAndYieldsNoDirectives()
    {
        // Arrange - bounded, malformed/truncated-input safety: garbage bytes (including NULs and
        // invalid UTF-8 sequences) must never throw, only ever produce an empty/partial document.
        var bytes = new byte[] { 0x00, 0xFF, 0xFE, 0x01, 0x02, 0x00, 0xC0, 0xAF, 0x00, 0x00 };

        // Act & Assert
        MtrDocument? document = null;
        Assert.DoesNotThrow(() => document = MtrDocument.Parse(bytes));
        Assert.That(document, Is.Not.Null);
        Assert.That(document!.Texture0, Is.Null);
        Assert.That(document.Texture1, Is.Null);
        Assert.That(document.EnvironmentMap, Is.Null);
    }

    [Test]
    public void Parse_WithTruncatedTrailingDirective_DoesNotThrow()
    {
        // Arrange - a payload cut off mid-directive (no trailing newline, no value token).
        var bytes = Encoding.UTF8.GetBytes("texture0 tex_ok\ntexture1");

        // Act & Assert
        MtrDocument? document = null;
        Assert.DoesNotThrow(() => document = MtrDocument.Parse(bytes));
        Assert.That(document!.Texture0, Is.EqualTo("tex_ok"));
        Assert.That(document.Texture1, Is.Null);
        Assert.That(document.Diagnostics, Has.Count.EqualTo(1));
    }

    [Test]
    public void Parse_WithOverlongLine_SkipsLineAndAddsDiagnosticWithoutThrowing()
    {
        // Arrange - a single absurdly long line must be bounded, not processed unbounded.
        var overlong = "texture0 " + new string('a', 5_000);
        var bytes = Encoding.UTF8.GetBytes(overlong);

        // Act & Assert
        MtrDocument? document = null;
        Assert.DoesNotThrow(() => document = MtrDocument.Parse(bytes));
        Assert.That(document!.Texture0, Is.Null);
        Assert.That(document.Diagnostics, Has.Count.EqualTo(1));
    }

    [Test]
    public void Textures_ReturnsAllFourInDeclarationOrder()
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes("texture0 t0\ntexture2 t2\n");

        // Act
        var document = MtrDocument.Parse(bytes);

        // Assert
        Assert.That(document.Textures, Is.EqualTo(new[] { "t0", null, "t2", null }));
        Assert.That(document.GetTexture(0), Is.EqualTo("t0"));
        Assert.That(document.GetTexture(2), Is.EqualTo("t2"));
        Assert.That(document.GetTexture(1), Is.Null);
        Assert.That(document.GetTexture(4), Is.Null);
        Assert.That(document.GetTexture(-1), Is.Null);
    }
}
