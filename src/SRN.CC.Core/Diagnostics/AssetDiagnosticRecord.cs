namespace SRN.CC.Core.Diagnostics;

public sealed record AssetDiagnosticRecord
{
    public DiagnosticCode Code { get; }
    public string Message { get; }
    public string? TargetPath { get; }
    public int? EntryIndex { get; }

    public AssetDiagnosticRecord(DiagnosticCode code, string message, string? targetPath = null, int? entryIndex = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Code = code;
        Message = message;
        TargetPath = targetPath;
        EntryIndex = entryIndex;
    }
}
