namespace SRN.CC.Core.Schema;

/// <summary>
/// How a store may act on a file whose declared schema version has been classified by
/// <see cref="SchemaVersions.Classify"/>.
/// </summary>
public enum SchemaOpenMode
{
    /// <summary>
    /// The declared version is below the first released schema (that is, less than 1). The file is
    /// not a valid artefact of this kind; the store quarantines and rebuilds rather than reading it.
    /// </summary>
    Unsupported,

    /// <summary>
    /// The declared version is at or below the current version, so the file can be read and written.
    /// A version below the current one is brought forward by <see cref="SchemaMigrationPipeline"/>
    /// first.
    /// </summary>
    Current,

    /// <summary>
    /// The declared version is newer than this build understands. The file is read on a best-effort
    /// basis and never written back, so a future build's data survives untouched.
    /// </summary>
    ReadOnlyNewer
}
