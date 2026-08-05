namespace SRN.CC.Core.Schema;

/// <summary>
/// The single source of truth for every persisted schema version in the application, replacing the
/// private per-store constants that had already begun to drift apart.
/// </summary>
/// <remarks>
/// Three kinds are versioned as integers and one — <see cref="SchemaKind.Manifest"/> — as the string
/// <see cref="Manifest"/>. The integer-typed members <see cref="Current"/> and <see cref="Classify"/>
/// therefore reject <see cref="SchemaKind.Manifest"/> instead of inventing an integer for it.
/// </remarks>
public static class SchemaVersions
{
    /// <summary>Current schema version of the <c>.srnccproj</c> project file.</summary>
    public const int Project = 1;

    /// <summary>Current schema version of the per-user <c>settings.json</c> file.</summary>
    public const int Settings = 1;

    /// <summary>Current schema version of the SQLite preview cache database.</summary>
    public const int Cache = 1;

    /// <summary>
    /// Current schema version of the published provenance manifest. A string, not an integer,
    /// because the manifest format declares a <c>major.minor</c> value.
    /// </summary>
    public const string Manifest = "1.0";

    /// <summary>Returns the current integer schema version for an integer-versioned kind.</summary>
    /// <param name="kind">The artefact kind.</param>
    /// <returns>The version this build reads and writes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is <see cref="SchemaKind.Manifest"/>, which has no integer version, or
    /// is not a defined <see cref="SchemaKind"/>.
    /// </exception>
    public static int Current(SchemaKind kind)
    {
        return kind switch
        {
            SchemaKind.Project => Project,
            SchemaKind.Settings => Settings,
            SchemaKind.Cache => Cache,
            SchemaKind.Manifest => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                $"{nameof(SchemaKind)}.{nameof(SchemaKind.Manifest)} is versioned as the string "
                    + $"'{Manifest}'. Read {nameof(SchemaVersions)}.{nameof(Manifest)} instead of "
                    + "requesting an integer version."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                $"Unknown {nameof(SchemaKind)} value.")
        };
    }

    /// <summary>
    /// Decides how a file declaring <paramref name="fileVersion"/> may be opened.
    /// </summary>
    /// <param name="kind">The artefact kind.</param>
    /// <param name="fileVersion">The schema version read from the file.</param>
    /// <returns>
    /// <see cref="SchemaOpenMode.Unsupported"/> below version 1,
    /// <see cref="SchemaOpenMode.ReadOnlyNewer"/> above <see cref="Current"/>, and
    /// <see cref="SchemaOpenMode.Current"/> otherwise. A version between 1 and the current one is
    /// readable and writable because <see cref="SchemaMigrationPipeline"/> brings it forward first;
    /// no separate "upgradeable" mode exists.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is <see cref="SchemaKind.Manifest"/> or is not a defined
    /// <see cref="SchemaKind"/>.
    /// </exception>
    public static SchemaOpenMode Classify(SchemaKind kind, int fileVersion)
    {
        int current = Current(kind);

        if (fileVersion < 1)
        {
            return SchemaOpenMode.Unsupported;
        }

        return fileVersion > current ? SchemaOpenMode.ReadOnlyNewer : SchemaOpenMode.Current;
    }
}
