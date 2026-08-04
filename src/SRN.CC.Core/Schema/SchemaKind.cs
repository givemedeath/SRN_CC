namespace SRN.CC.Core.Schema;

/// <summary>
/// The persisted artefacts whose on-disk shape carries a schema version.
/// Every store reads its version from <see cref="SchemaVersions"/> rather than a private constant.
/// </summary>
public enum SchemaKind
{
    /// <summary>The <c>.srnccproj</c> project file.</summary>
    Project,

    /// <summary>The per-user <c>settings.json</c> file.</summary>
    Settings,

    /// <summary>The SQLite preview cache database.</summary>
    Cache,

    /// <summary>
    /// The published <c>.srncc-manifest.json</c> provenance manifest. Alone among the kinds it is
    /// versioned as a string (<see cref="SchemaVersions.Manifest"/>) and has no integer version.
    /// </summary>
    Manifest
}
