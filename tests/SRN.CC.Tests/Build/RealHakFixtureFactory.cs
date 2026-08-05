using System.Text;
using SRN.CC.Formats.Hak;
using SWLOR.NWN.Formats.Common;

namespace SRN.CC.Tests.Build;

/// <summary>
/// Produces <em>genuine</em> HAK bytes for tests, through the production <see cref="HakWriter"/>.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed the only "HAK" fixtures in the suite were text files that happened to carry a
/// <c>.hak</c> extension, so nothing in the repository ever ran the build pipeline over a real ERF
/// header, key table, or resource table. Every byte this type writes goes through
/// <see cref="HakWriter.Write"/> — no hand-rolled headers — which is what makes a fixture failure a
/// signal about the production writer rather than about the fixture.
/// </para>
/// <para>
/// The factory is deliberately <c>public static</c> and completely deterministic: payloads come from
/// a fixed-seed linear congruential sequence, never from <see cref="Random"/> without a seed and
/// never from a clock. Two <see cref="DefaultCorpus"/> invocations therefore produce byte-identical
/// files, which is the property the repeat-build determinism tests rest on.
/// </para>
/// <para>
/// Its public surface is a contract consumed read-only by the end-to-end build scenario; add members
/// rather than changing the shape of the existing ones.
/// </para>
/// </remarks>
public static class RealHakFixtureFactory
{
    /// <summary>
    /// The untruncated 17-character name behind the truncation case in <see cref="DefaultCorpus"/>.
    /// </summary>
    /// <remarks>
    /// A resref field is 16 bytes, so this name cannot be stored whole. The factory truncates it and
    /// exposes the original here so a consumer can assert on both halves of the transformation
    /// without duplicating the literal.
    /// </remarks>
    public const string OversizedSourceName = "srn_long_name_017";

    /// <summary>The 16-byte resref that <see cref="OversizedSourceName"/> truncates to.</summary>
    public const string TruncatedName = "srn_long_name_01";

    /// <summary>The exact 16-byte resref that exercises the field-width boundary.</summary>
    public const string BoundaryName = "abcdefghijklmnop";

    private static readonly Encoding Cp1252;

    static RealHakFixtureFactory()
    {
        // CP1252 is not in the default .NET encoding set; the production identity type registers the
        // same provider, so this keeps the fixture usable even when no identity has been built yet.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp1252 = Encoding.GetEncoding(1252);
    }

    /// <summary>
    /// One resource as it will appear in a fixture: the raw resref field bytes, the numeric resource
    /// type, and the payload.
    /// </summary>
    /// <param name="ResrefBytes">
    /// 1-16 raw bytes. Deliberately bytes rather than a string: the CP1252-accented case exists to
    /// prove the pipeline never round-trips a resref through a lossy decode, and a <c>string</c>
    /// parameter would hide exactly the defect the case is looking for.
    /// </param>
    /// <param name="ResourceType">Numeric resource type, taken from <see cref="ResourceTypes"/>.</param>
    /// <param name="Payload">Payload bytes. May be empty; a zero-byte resource is legal in a HAK.</param>
    public sealed record FixtureEntry(byte[] ResrefBytes, ushort ResourceType, byte[] Payload);

    /// <summary>
    /// The shared corpus: one entry per interesting shape the build pipeline has to survive.
    /// </summary>
    /// <remarks>
    /// Covers the six model/texture/table types a content pack actually contains, a
    /// <c>lod</c> resource whose payload the pipeline has no reader for and must therefore carry
    /// opaquely, a CP1252-accented resref, both sides of the 16-byte resref boundary, and a
    /// zero-byte payload. Every type id is read from <see cref="ResourceTypes"/> rather than written
    /// as a literal, so a change to the vendored table can never silently desynchronise the fixture.
    /// </remarks>
    /// <returns>A freshly built, order-stable list. Two calls are value-equal byte for byte.</returns>
    public static IReadOnlyList<FixtureEntry> DefaultCorpus()
    {
        return new List<FixtureEntry>
        {
            // Same resref under two types: proves the writer's (type, resref) ordering, not just resref ordering.
            Entry("dtl_wall", "mdl", Payload(512, 0x5311_0001UL)),
            Entry("dtl_wall", "tga", Payload(1024, 0x5311_0002UL)),

            Entry("crate01", "dds", Payload(256, 0x5311_0003UL)),
            Entry("crate01", "mtr", Payload(64, 0x5311_0004UL)),

            // Zero-byte payload. Legal, and historically the shape that breaks naive offset maths.
            Entry("crate01", "txi", Array.Empty<byte>()),

            Entry("appearance", "2da", Payload(2048, 0x5311_0005UL)),

            // 'lod' is packaged as opaque bytes: nothing in the pipeline parses it.
            Entry("tree_lod0", "lod", Payload(333, 0x5311_0006UL)),

            // CP1252 accented resref, written as raw bytes rather than via a string round trip.
            new FixtureEntry(
                AccentedResrefBytes(),
                TypeOf("tga"),
                Payload(128, 0x5311_0007UL)),

            Entry(BoundaryName, "mdl", Payload(96, 0x5311_0008UL)),
            Entry(TruncatedName, "mdl", Payload(48, 0x5311_0009UL))
        };
    }

    /// <summary>
    /// The raw CP1252 bytes of the accented resref in <see cref="DefaultCorpus"/> (<c>caf</c>, 0xE9,
    /// <c>_tex</c>).
    /// </summary>
    /// <remarks>
    /// Exposed so a consumer can assert byte equality against the resref a <see cref="HakReader"/>
    /// hands back without re-deriving the encoding, which is the point of the case.
    /// </remarks>
    public static byte[] AccentedResrefBytes() =>
        new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'_', (byte)'t', (byte)'e', (byte)'x' };

    /// <summary>
    /// Writes <paramref name="entries"/> to <paramref name="path"/> as a real HAK, creating the
    /// parent directory if needed.
    /// </summary>
    /// <param name="path">Destination file path. Overwritten if it exists.</param>
    /// <param name="entries">Entries to pack. Order is irrelevant; the writer sorts canonically.</param>
    public static void WriteHak(string path, IEnumerable<FixtureEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);

        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        List<HakWriter.WriteItem> items = new();
        List<MemoryStream> payloadStreams = new();
        try
        {
            foreach (var entry in entries)
            {
                MemoryStream payloadStream = new(entry.Payload, writable: false);
                payloadStreams.Add(payloadStream);
                items.Add(new HakWriter.WriteItem(
                    new HakFormatKey(entry.ResrefBytes, entry.ResourceType),
                    payloadStream,
                    checked((uint)entry.Payload.Length)));
            }

            using FileStream output = new(full, FileMode.Create, FileAccess.Write, FileShare.None);
            HakWriter.Write(output, items);
        }
        finally
        {
            foreach (var stream in payloadStreams)
            {
                stream.Dispose();
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="entries"/> into <paramref name="directory"/> as loose files, one per
    /// entry, named <c>&lt;resref&gt;.&lt;extension&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The names come from the same resref bytes the HAK fixture uses, so a folder source and a HAK
    /// source built from one corpus resolve to the same identities and can be diffed against each
    /// other. Truncation is already applied in the corpus — a 17-character file name would be
    /// rejected by the folder indexer as an invalid resref rather than truncated, so the folder
    /// fixture would carry a diagnostic instead of an asset.
    /// </remarks>
    /// <param name="directory">Destination directory. Created if missing.</param>
    /// <param name="entries">Entries to write.</param>
    public static void WriteFolderSource(string directory, IEnumerable<FixtureEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(entries);

        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);

        foreach (var entry in entries)
        {
            File.WriteAllBytes(Path.Combine(root, FileNameFor(entry)), entry.Payload);
        }
    }

    /// <summary>The loose-file name <see cref="WriteFolderSource"/> gives <paramref name="entry"/>.</summary>
    public static string FileNameFor(FixtureEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string extension = ResourceTypes.GetExtension(entry.ResourceType);
        string resref = Cp1252.GetString(entry.ResrefBytes);
        return extension.Length == 0 ? resref : $"{resref}.{extension}";
    }

    /// <summary>Decodes a raw resref field back to text using CP1252, the on-disk encoding.</summary>
    public static string DecodeResref(ReadOnlySpan<byte> resrefBytes) => Cp1252.GetString(resrefBytes);

    /// <summary>Resolves an extension to its numeric type through the vendored table.</summary>
    /// <exception cref="ArgumentException">The extension is not a known resource type.</exception>
    public static ushort TypeOf(string extension)
    {
        if (!ResourceTypes.TryGetType(extension, out ushort typeId))
        {
            throw new ArgumentException($"'{extension}' is not a known resource type.", nameof(extension));
        }

        return typeId;
    }

    private static FixtureEntry Entry(string resref, string extension, byte[] payload) =>
        new(Cp1252.GetBytes(resref), TypeOf(extension), payload);

    /// <summary>
    /// Fills <paramref name="length"/> bytes from a fixed-seed LCG.
    /// </summary>
    /// <remarks>
    /// Written out rather than delegating to <see cref="Random"/> so the byte sequence is pinned to
    /// this source file: a determinism test that depends on a runtime's PRNG implementation is a
    /// test that can change meaning under a framework upgrade.
    /// </remarks>
    private static byte[] Payload(int length, ulong seed)
    {
        byte[] buffer = new byte[length];
        ulong state = seed;
        for (int i = 0; i < length; i++)
        {
            state = unchecked((state * 6364136223846793005UL) + 1442695040888963407UL);
            buffer[i] = (byte)(state >> 33);
        }

        return buffer;
    }
}
