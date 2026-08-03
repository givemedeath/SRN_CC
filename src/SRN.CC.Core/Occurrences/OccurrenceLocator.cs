namespace SRN.CC.Core.Occurrences;

public abstract record OccurrenceLocator;

public sealed record HakEntryLocator(int EntryIndex) : OccurrenceLocator
{
    public override string ToString() => $"HakEntry({EntryIndex})";
}

public sealed record FolderFileLocator : OccurrenceLocator
{
    public string NormalizedRelativePath { get; }

    public FolderFileLocator(string normalizedRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedRelativePath);
        NormalizedRelativePath = normalizedRelativePath.Replace('\\', '/').TrimStart('/');
    }

    public override string ToString() => $"FolderFile({NormalizedRelativePath})";
}
