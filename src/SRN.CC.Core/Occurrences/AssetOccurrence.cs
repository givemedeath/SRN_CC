using SRN.CC.Core.Identity;

namespace SRN.CC.Core.Occurrences;

public sealed record AssetOccurrence
{
    public AssetIdentity Identity { get; }
    public Guid SourceId { get; }
    public OccurrenceLocator Locator { get; }
    public string OriginalName { get; }
    public long Size { get; }
    public ValidationState ValidationState { get; }
    public IReadOnlyDictionary<string, string>? ExtensionMetadata { get; }
    public byte[]? Sha256 { get; }

    public AssetOccurrence(
        AssetIdentity identity,
        Guid sourceId,
        OccurrenceLocator locator,
        string originalName,
        long size,
        ValidationState validationState = ValidationState.Valid,
        IReadOnlyDictionary<string, string>? extensionMetadata = null,
        byte[]? sha256 = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalName);
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size), "Size cannot be negative.");

        Identity = identity;
        SourceId = sourceId;
        Locator = locator;
        OriginalName = originalName;
        Size = size;
        ValidationState = validationState;
        ExtensionMetadata = extensionMetadata;
        Sha256 = sha256;
    }
}
