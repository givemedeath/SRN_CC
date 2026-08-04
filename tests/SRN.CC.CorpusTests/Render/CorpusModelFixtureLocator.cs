using SRN.CC.Formats.Hak;

namespace SRN.CC.CorpusTests.Render;

/// <summary>
/// Shared helper for the opt-in milestone-6 corpus/GPU-adjacent test tier
/// (<c>ModelPreviewCorpusTests</c>, <c>ModelPreviewWorkingSetTests</c>): locates representative MDL
/// payloads from a real corpus root by scanning HAK archives for resource-type 2002 ("mdl") entries,
/// mirroring how <c>CorpusIndexAcceptanceTests</c> already enumerates corpus HAKs.
/// </summary>
internal static class CorpusModelFixtureLocator
{
    private const ushort MdlResourceType = 2002;

    /// <summary>
    /// Scans HAK files under <paramref name="corpusRoot"/> (in file-system enumeration order) for up
    /// to <paramref name="count"/> MDL payloads with a nonzero declared size, returning each as a
    /// human-readable label (for evidence output) paired with the raw MDL bytes. Never throws: a HAK
    /// that fails to open, or an individual entry whose payload cannot be read, is skipped rather
    /// than aborting the whole scan — corpus files are real-world assets and are not guaranteed to
    /// all be well-formed.
    /// </summary>
    public static List<(string Label, byte[] Bytes)> FindModelPayloads(string corpusRoot, int count)
    {
        List<(string Label, byte[] Bytes)> found = new();

        foreach (string hakPath in Directory.EnumerateFiles(corpusRoot, "*.hak", SearchOption.AllDirectories))
        {
            if (found.Count >= count)
            {
                break;
            }

            HakReader reader;
            try
            {
                using FileStream stream = new(hakPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                reader = new HakReader(stream);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (HakEntry entry in reader.Entries)
            {
                if (found.Count >= count)
                {
                    break;
                }

                if (entry.Key.ResourceType != MdlResourceType || entry.ResourceSize == 0)
                {
                    continue;
                }

                try
                {
                    using Stream payloadStream = HakReader.OpenPayloadStream(entry, hakPath);
                    using MemoryStream buffer = new();
                    payloadStream.CopyTo(buffer);
                    string resref = System.Text.Encoding.ASCII.GetString(entry.Key.ResrefBytes.Span);
                    found.Add(($"{Path.GetFileName(hakPath)}::{resref}", buffer.ToArray()));
                }
                catch (Exception)
                {
                    // A malformed individual payload must not abort the scan for the rest of the corpus.
                }
            }
        }

        return found;
    }
}
