namespace SRN.CC.Core.Preview;

/// <summary>
/// Typed, process-local preview payload too structured for FormattedContent or RawPayload.
/// Never persisted; implementations live in the producing layer.
/// </summary>
public interface IPreviewPayload
{
    string PayloadKind { get; }
    long ApproximateByteSize { get; }
}
