using System.Globalization;

namespace SRN.CC.Infrastructure.Logging;

/// <summary>
/// Owns the on-disk naming, ordering and retention of the rolling log directory. Rotation policy
/// lives here rather than inside <see cref="JsonLineLogSink"/> so that naming and pruning can be
/// tested without writing a byte of log content, and so an operator tool could later enumerate the
/// same set without instantiating a writer.
/// </summary>
/// <remarks>
/// The rotated stamp uses a fixed-width, zero-padded UTC pattern (<c>yyyyMMddTHHmmssfffZ</c>)
/// precisely so that an <em>ordinal</em> sort of the file names is identical to a chronological
/// sort. Every ordering method here therefore reads the directory and sorts by name; no timestamps
/// are parsed for ordering and no file metadata is trusted, because copying a log folder rewrites
/// <c>LastWriteTime</c> but never rewrites the name.
/// </remarks>
public sealed class LogFileSet
{
    /// <summary>Name of the file that is currently being appended to.</summary>
    public const string ActiveFileName = "srncc.log";

    /// <summary>Shared leading component of both the active and the rotated file names.</summary>
    public const string FileNameStem = "srncc";

    /// <summary>Extension shared by the active and the rotated file names.</summary>
    public const string FileNameExtension = ".log";

    /// <summary>
    /// Stamp embedded in a rotated file name. Fixed width and lexicographically ordered; see the
    /// type-level remarks for why that property is load-bearing.
    /// </summary>
    public const string RotatedStampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    /// <summary>Shipped retention: at most ten files in total, active file included (PLAN.md:98).</summary>
    public const int DefaultMaxFileCount = 10;

    private const string RotatedFileNamePrefix = FileNameStem + ".";
    private const string RotatedSearchPattern = FileNameStem + ".*" + FileNameExtension;

    /// <summary>Rendered width of <see cref="RotatedStampFormat"/>: <c>yyyyMMddTHHmmssfffZ</c>.</summary>
    private const int RotatedStampLength = 19;

    /// <summary>Creates a file set over a log directory.</summary>
    /// <param name="directoryPath">
    /// Directory that holds the active and rotated files, normally <c>AppPaths.LogDirectory</c>.
    /// Nothing is created here; the directory is materialised lazily by <see cref="EnsureDirectory"/>.
    /// </param>
    /// <param name="maxFileCount">
    /// Total number of files to retain, active file included. Injectable so a test can prove the
    /// pruning rule against three files instead of ten.
    /// </param>
    public LogFileSet(string directoryPath, int maxFileCount = DefaultMaxFileCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileCount, 1);

        DirectoryPath = Path.GetFullPath(directoryPath);
        MaxFileCount = maxFileCount;
        ActivePath = Path.Combine(DirectoryPath, ActiveFileName);
    }

    /// <summary>Absolute path of the directory holding the set.</summary>
    public string DirectoryPath { get; }

    /// <summary>Total files retained, active file included.</summary>
    public int MaxFileCount { get; }

    /// <summary>Absolute path of the file currently being appended to.</summary>
    public string ActivePath { get; }

    /// <summary>Builds the rotated file name for a moment in time. Pure; touches no file system.</summary>
    /// <param name="rotatedAtUtc">Moment the rotation happens. Converted to UTC before stamping.</param>
    public static string BuildRotatedFileName(DateTimeOffset rotatedAtUtc) =>
        RotatedFileNamePrefix +
        rotatedAtUtc.ToUniversalTime().ToString(RotatedStampFormat, CultureInfo.InvariantCulture) +
        FileNameExtension;

    /// <summary>
    /// Returns <see langword="true"/> and the rotation stamp when <paramref name="fileName"/> is one
    /// of this set's rotated files. Foreign files in the log directory fail this test and are then
    /// never enumerated, ordered, or deleted — pruning must not be able to eat an operator's notes.
    /// </summary>
    public static bool TryParseRotatedFileName(string fileName, out DateTimeOffset rotatedAtUtc)
    {
        rotatedAtUtc = default;

        if (string.IsNullOrEmpty(fileName) ||
            fileName.Length != RotatedFileNamePrefix.Length + RotatedStampLength + FileNameExtension.Length ||
            !fileName.StartsWith(RotatedFileNamePrefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(FileNameExtension, StringComparison.Ordinal))
        {
            return false;
        }

        string stamp = fileName.Substring(RotatedFileNamePrefix.Length, RotatedStampLength);
        return DateTimeOffset.TryParseExact(
            stamp,
            RotatedStampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out rotatedAtUtc);
    }

    /// <summary>
    /// Creates the log directory if it is missing. Separate from the constructor so that merely
    /// describing a layout never touches the disk, matching <c>AppPaths</c>.
    /// </summary>
    public void EnsureDirectory() => Directory.CreateDirectory(DirectoryPath);

    /// <summary>
    /// The rotated files present on disk, <b>oldest first</b>. An empty list is returned when the
    /// directory does not exist, so a caller never has to pre-check.
    /// </summary>
    public IReadOnlyList<string> ListRotatedPaths()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return Array.Empty<string>();
        }

        var rotated = new List<string>();
        foreach (string path in Directory.EnumerateFiles(DirectoryPath, RotatedSearchPattern))
        {
            if (TryParseRotatedFileName(Path.GetFileName(path), out _))
            {
                rotated.Add(path);
            }
        }

        // Ordinal on the file name is chronological by construction; see the type-level remarks.
        rotated.Sort(static (left, right) => string.CompareOrdinal(
            Path.GetFileName(left), Path.GetFileName(right)));
        return rotated;
    }

    /// <summary>
    /// Picks a free rotated path for <paramref name="rotatedAtUtc"/>, advancing the stamp by one
    /// millisecond per collision.
    /// </summary>
    /// <remarks>
    /// Two rotations inside the same millisecond are possible under a tiny size cap. Disambiguating
    /// with a numeric suffix would break the "ordinal sort equals chronological sort" invariant that
    /// pruning depends on, so the stamp is nudged forward instead: the resulting name is still a
    /// valid, correctly ordered stamp and is at most a few milliseconds ahead of the wall clock.
    /// </remarks>
    public string ReserveRotatedPath(DateTimeOffset rotatedAtUtc)
    {
        DateTimeOffset stamp = rotatedAtUtc;
        while (true)
        {
            string candidate = Path.Combine(DirectoryPath, BuildRotatedFileName(stamp));
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            stamp = stamp.AddMilliseconds(1);
        }
    }

    /// <summary>
    /// Deletes the oldest rotated files until at most <see cref="MaxFileCount"/> files remain in the
    /// set, so retention holds <c>MaxFileCount - 1</c> rotated files plus the active one.
    /// </summary>
    /// <returns>How many files were deleted.</returns>
    /// <remarks>
    /// The active file always counts against the budget even when it does not exist yet. Pruning
    /// runs immediately after a rename, in the window where the active file is momentarily absent;
    /// counting only what is on disk right then would leave room for one file too many for the rest
    /// of the process's life.
    /// <para>
    /// Only the oldest surplus files are considered: if one of them cannot be deleted — an operator
    /// is tailing it, a scanner has it open — it is skipped and left for the next rotation rather
    /// than compensated for by deleting a newer file that retention still wants. Retention is a
    /// courtesy to the disk, not a correctness guarantee, and must never become a reason logging
    /// stops.
    /// </para>
    /// </remarks>
    public int Prune()
    {
        IReadOnlyList<string> rotated = ListRotatedPaths();
        int surplus = Math.Min(rotated.Count + 1 - MaxFileCount, rotated.Count);
        if (surplus <= 0)
        {
            return 0;
        }

        int deleted = 0;
        for (int i = 0; i < surplus; i++)
        {
            try
            {
                File.Delete(rotated[i]);
                deleted++;
            }
            catch (IOException)
            {
                // Held open elsewhere; leave it for the next rotation.
            }
            catch (UnauthorizedAccessException)
            {
                // Read-only or ACL-denied; same treatment.
            }
        }

        return deleted;
    }
}
