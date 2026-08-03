using FluentAssertions;
using NUnit.Framework;
using SRN.CC.Formats.Hak;

namespace SRN.CC.Tests.Formats;

[TestFixture]
public class OwnedBoundedStreamTests
{
    [Test]
    public async Task ReadAsync_SharedBaseStream_SerializesPositionAndRead()
    {
        byte[] contents = Enumerable.Range(0, 100).Select(value => (byte)value).ToArray();
        await using DelayedMemoryStream baseStream = new(contents);
        await using OwnedBoundedStream first = new(baseStream, 0, 10, ownsStream: false);
        await using OwnedBoundedStream second = new(baseStream, 50, 10, ownsStream: false);
        byte[] firstBuffer = new byte[10];
        byte[] secondBuffer = new byte[10];

        await Task.WhenAll(first.ReadAsync(firstBuffer).AsTask(), second.ReadAsync(secondBuffer).AsTask());

        firstBuffer.Should().Equal(contents[..10]);
        secondBuffer.Should().Equal(contents[50..60]);
    }

    private sealed class DelayedMemoryStream(byte[] contents) : MemoryStream(contents)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(25, cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}

