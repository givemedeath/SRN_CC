using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Preview.Render.Gl;

namespace SRN.CC.Tests.Preview.Render.Gl;

[TestFixture]
public class GlHandleTests
{
    [Test]
    public void Zero_ProducesAZeroNamedHandleOfTheRequestedKind()
    {
        GlHandle handle = GlHandle.Zero(GlResourceKind.Texture);

        handle.Name.Should().Be(0);
        handle.Kind.Should().Be(GlResourceKind.Texture);
        handle.Context.Should().Be(GlContextId.None);
        handle.IsZero.Should().BeTrue();
    }

    [Test]
    public void IsZero_IsFalseForANonZeroName()
    {
        GlHandle handle = new(GlContextId.Next(), 7, GlResourceKind.Buffer);

        handle.IsZero.Should().BeFalse();
    }

    [Test]
    public void Equality_ComparesAllThreeComponents()
    {
        GlContextId context = GlContextId.Next();
        GlHandle a = new(context, 1, GlResourceKind.Buffer);
        GlHandle b = new(context, 1, GlResourceKind.Buffer);
        GlHandle differentName = new(context, 2, GlResourceKind.Buffer);
        GlHandle differentKind = new(context, 1, GlResourceKind.Texture);

        a.Should().Be(b);
        a.Should().NotBe(differentName);
        a.Should().NotBe(differentKind);
    }
}
