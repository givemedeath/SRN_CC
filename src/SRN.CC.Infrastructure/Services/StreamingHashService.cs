using System.Security.Cryptography;
using SRN.CC.Core.Occurrences;
using SRN.CC.Core.Services;
using SRN.CC.Core.Sources;

namespace SRN.CC.Infrastructure.Services;

public sealed class StreamingHashService : IStreamingHashService
{
    private readonly ISourceReaderDispatcher _dispatcher;

    public StreamingHashService(ISourceReaderDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async Task<byte[]> ComputeSha256Async(AssetSource source, AssetOccurrence occurrence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(occurrence);

        await using Stream stream = await _dispatcher.OpenOccurrenceAsync(source, occurrence, cancellationToken).ConfigureAwait(false);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }
}
