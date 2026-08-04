using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Preview.Render.Gl;

namespace SRN.CC.Tests.Preview.Render.Gl;

[TestFixture]
public class GlContextIdTests
{
    [Test]
    public void Next_ReturnsMonotonicallyIncreasingValues()
    {
        GlContextId first = GlContextId.Next();
        GlContextId second = GlContextId.Next();
        GlContextId third = GlContextId.Next();

        first.Value.Should().BeLessThan(second.Value);
        second.Value.Should().BeLessThan(third.Value);
    }

    [Test]
    public void Next_NeverReturnsNone()
    {
        for (int i = 0; i < 10; i++)
        {
            GlContextId.Next().Should().NotBe(GlContextId.None);
        }
    }

    [Test]
    public void None_IsTheDefaultValue()
    {
        GlContextId.None.Should().Be(default(GlContextId));
        GlContextId.None.Value.Should().Be(0);
    }

    [Test]
    public void DistinctDevices_GetDistinctContextIds()
    {
        using var deviceA = new FakeGlDevice();
        using var deviceB = new FakeGlDevice();

        deviceA.ContextId.Should().NotBe(deviceB.ContextId);
    }
}
