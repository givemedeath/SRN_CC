using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Preview.Render.Gl;

namespace SRN.CC.Tests.Preview.Render.Gl;

[TestFixture]
public class FakeGlDeviceTests
{
    [Test]
    public void CreateBuffer_AssignsIncrementingFakeNamesAndTracksLiveness()
    {
        using var device = new FakeGlDevice();

        GlHandle first = device.CreateBuffer(GlBufferTarget.Vertex, new byte[] { 1, 2, 3, 4 });
        GlHandle second = device.CreateBuffer(GlBufferTarget.Index, new byte[] { 5, 6 });

        first.Name.Should().NotBe(0);
        second.Name.Should().NotBe(first.Name);
        device.LiveResources.Should().Contain([first, second]);
        device.Calls.Should().Contain(c => c.Method == "CreateBuffer");
    }

    [Test]
    public void CreateTexture2D_TracksLivenessAndRecordsCall()
    {
        using var device = new FakeGlDevice();
        byte[] pixels = new byte[8 * 8 * 4];

        GlHandle handle = device.CreateTexture2D(8, 8, pixels, hasAlpha: false);

        handle.IsZero.Should().BeFalse();
        handle.Kind.Should().Be(GlResourceKind.Texture);
        device.LiveResources.Should().Contain(handle);
        device.Calls.Should().Contain(c => c.Method == "CreateTexture2D");
    }

    [Test]
    public void CreateProgram_Succeeds_ReturnsNonZeroHandleAndNullLog()
    {
        using var device = new FakeGlDevice();

        GlHandle handle = device.CreateProgram("vertex-source", "fragment-source", out string? log);

        handle.IsZero.Should().BeFalse();
        handle.Kind.Should().Be(GlResourceKind.Program);
        log.Should().BeNull();
        device.LiveResources.Should().Contain(handle);
    }

    [Test]
    public void CreateProgram_WithFailProgramLink_ReturnsZeroHandleAndNonNullLog_NeverThrows()
    {
        using var device = new FakeGlDevice(failProgramLink: true);

        Action act = () =>
        {
            GlHandle handle = device.CreateProgram("vertex-source", "fragment-source", out string? log);
            handle.IsZero.Should().BeTrue();
            log.Should().NotBeNull();
        };

        act.Should().NotThrow();
        device.LiveResources.Should().BeEmpty();
    }

    [Test]
    public void Delete_RemovesFromLiveResourcesAndRecordsCall()
    {
        using var device = new FakeGlDevice();
        GlHandle handle = device.CreateBuffer(GlBufferTarget.Vertex, new byte[] { 1 });

        device.Delete(handle);

        device.LiveResources.Should().BeEmpty();
        device.Calls.Should().Contain(c => c.Method == "Delete");
    }

    [Test]
    public void Delete_OfAForeignHandle_IsIgnored()
    {
        using var deviceA = new FakeGlDevice();
        using var deviceB = new FakeGlDevice();
        GlHandle handleFromA = deviceA.CreateBuffer(GlBufferTarget.Vertex, new byte[] { 1 });

        deviceB.Delete(handleFromA);

        deviceB.Calls.Should().BeEmpty();
        deviceA.LiveResources.Should().Contain(handleFromA);
    }

    [Test]
    public void SimulateContextLoss_SetsIsLostAndClearsLiveResources_WithoutRecordingAnyCall()
    {
        using var device = new FakeGlDevice();
        device.CreateBuffer(GlBufferTarget.Vertex, new byte[] { 1, 2 });
        device.CreateTexture2D(4, 4, new byte[4 * 4 * 4], hasAlpha: true);
        int callsBefore = device.Calls.Count;

        device.SimulateContextLoss();

        device.IsLost.Should().BeTrue();
        device.LiveResources.Should().BeEmpty();
        device.Calls.Should().HaveCount(callsBefore, "context loss must never itself issue/record a GL call");
    }

    [Test]
    public void AfterContextLoss_EveryOperationIsANoOp()
    {
        using var device = new FakeGlDevice();
        device.SimulateContextLoss();
        int callsBefore = device.Calls.Count;

        GlHandle buffer = device.CreateBuffer(GlBufferTarget.Vertex, new byte[] { 1 });
        GlHandle texture = device.CreateTexture2D(2, 2, new byte[2 * 2 * 4], hasAlpha: false);
        GlHandle program = device.CreateProgram("vs", "fs", out string? log);

        buffer.IsZero.Should().BeTrue();
        texture.IsZero.Should().BeTrue();
        program.IsZero.Should().BeTrue();
        log.Should().BeNull();
        device.Calls.Should().HaveCount(callsBefore);
        device.LiveResources.Should().BeEmpty();
    }

    [Test]
    public void Dispose_DeletesEveryLiveResourceAndRecordsCalls()
    {
        var device = new FakeGlDevice();
        device.CreateBuffer(GlBufferTarget.Vertex, new byte[] { 1 });
        device.CreateBuffer(GlBufferTarget.Index, new byte[] { 2 });

        device.Dispose();

        device.LiveResources.Should().BeEmpty();
        device.Calls.Count(c => c.Method == "Delete").Should().Be(2);
    }
}
