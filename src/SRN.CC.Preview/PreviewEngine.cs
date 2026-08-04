using SRN.CC.Core.Preview;
using SRN.CC.Core.Services;

namespace SRN.CC.Preview;

public sealed class PreviewEngine
{
    private readonly ISourceReaderDispatcher _dispatcher;
    private readonly IReadOnlyList<IPreviewProvider> _providers;
    private readonly SemaphoreSlim _concurrencySemaphore = new(3, 3); // Max 3 concurrent preview jobs

    public PreviewEngine(
        ISourceReaderDispatcher dispatcher,
        IEnumerable<IPreviewProvider> providers)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _providers = providers?.ToList() ?? throw new ArgumentNullException(nameof(providers));
    }

    public async Task<PreviewResult> ExecutePreviewAsync(
        PreviewRequest request,
        int debounceMs = 150,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (debounceMs > 0)
        {
            await Task.Delay(debounceMs, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        await _concurrencySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var provider = _providers.FirstOrDefault(p => p.Family == request.PreferredFamily && p.CanPreview(request))
                           ?? _providers.FirstOrDefault(p => p.Family == PreviewFamily.Hex && p.CanPreview(request))
                           ?? _providers.FirstOrDefault(p => p.Family == PreviewFamily.Metadata && p.CanPreview(request));

            if (provider == null)
            {
                return new PreviewResult(
                    Occurrence: request.Occurrence,
                    Family: request.PreferredFamily,
                    IsSuccess: false,
                    MetadataText: null,
                    RawPayload: null,
                    FormattedContent: null,
                    ErrorMessage: $"No preview provider available for family {request.PreferredFamily}.",
                    Diagnostics: new[] { "Unsupported preview request" }
                );
            }

            await using var stream = await _dispatcher.OpenOccurrenceAsync(request.Source, request.Occurrence, cancellationToken).ConfigureAwait(false);

            return await provider.GeneratePreviewAsync(request, stream, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PreviewResult(
                Occurrence: request.Occurrence,
                Family: request.PreferredFamily,
                IsSuccess: false,
                MetadataText: null,
                RawPayload: null,
                FormattedContent: null,
                ErrorMessage: $"Preview error: {ex.Message}",
                Diagnostics: new[] { ex.ToString() }
            );
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }
}
