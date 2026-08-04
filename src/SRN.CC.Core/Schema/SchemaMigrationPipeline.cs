using System.Text.Json.Nodes;

namespace SRN.CC.Core.Schema;

/// <summary>
/// Composes the registered <see cref="IJsonSchemaMigration"/> steps for one
/// <see cref="SchemaKind"/> into a single forward upgrade.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline resolves the whole chain before applying anything, so a missing step is reported as
/// an error with nothing half-written; it never skips a version silently.
/// </para>
/// <para>
/// The caller's document is never mutated. Migrations run against a detached deep copy, which is
/// what makes the pipeline safe for <c>ProjectStore</c>, whose unknown-field preservation depends on
/// the live <c>ProjectPreferences.RawRootNode</c> staying exactly as it was read.
/// </para>
/// <para>
/// Stamping the migrated version back into the document is the caller's job: the version property
/// name differs per store and the pipeline does not know it.
/// </para>
/// </remarks>
public sealed class SchemaMigrationPipeline
{
    private static readonly IReadOnlyList<string> NoMigrations = Array.Empty<string>();

    private readonly SchemaKind _kind;
    private readonly int _targetVersion;
    private readonly Dictionary<int, IJsonSchemaMigration> _byFromVersion;

    /// <summary>
    /// Creates a pipeline for one artefact kind.
    /// </summary>
    /// <param name="kind">The artefact kind. Must be integer-versioned.</param>
    /// <param name="migrations">
    /// The registered steps, in any order. Every step must declare <paramref name="kind"/>, a
    /// <see cref="IJsonSchemaMigration.FromVersion"/> of at least 1, a
    /// <see cref="IJsonSchemaMigration.ToVersion"/> greater than its own
    /// <see cref="IJsonSchemaMigration.FromVersion"/>, and a
    /// <see cref="IJsonSchemaMigration.FromVersion"/> no other step already claims.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="migrations"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> has no integer version — see <see cref="SchemaVersions.Current"/>.
    /// </exception>
    /// <exception cref="ArgumentException">A registered step is null or violates the rules above.</exception>
    public SchemaMigrationPipeline(SchemaKind kind, IEnumerable<IJsonSchemaMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);

        _kind = kind;
        _targetVersion = SchemaVersions.Current(kind);
        _byFromVersion = new Dictionary<int, IJsonSchemaMigration>();

        foreach (IJsonSchemaMigration migration in migrations)
        {
            if (migration is null)
            {
                throw new ArgumentException(
                    $"A null migration was registered for {kind}.",
                    nameof(migrations));
            }

            if (migration.Kind != kind)
            {
                throw new ArgumentException(
                    $"Migration {migration.FromVersion}->{migration.ToVersion} declares kind "
                        + $"{migration.Kind} but was registered on a {kind} pipeline.",
                    nameof(migrations));
            }

            if (migration.FromVersion < 1)
            {
                throw new ArgumentException(
                    $"Migration for {kind} declares FromVersion {migration.FromVersion}; the first "
                        + "released schema version is 1.",
                    nameof(migrations));
            }

            if (migration.ToVersion <= migration.FromVersion)
            {
                throw new ArgumentException(
                    $"Migration for {kind} declares ToVersion {migration.ToVersion}, which does not "
                        + $"advance past FromVersion {migration.FromVersion}.",
                    nameof(migrations));
            }

            if (!_byFromVersion.TryAdd(migration.FromVersion, migration))
            {
                throw new ArgumentException(
                    $"Two migrations for {kind} both start at schema version "
                        + $"{migration.FromVersion}.",
                    nameof(migrations));
            }
        }
    }

    /// <summary>The artefact kind this pipeline upgrades.</summary>
    public SchemaKind Kind => _kind;

    /// <summary>
    /// The version the two-argument <see cref="TryUpgrade(JsonObject, int, out JsonObject, out IReadOnlyList{string}, out string?)"/>
    /// upgrades to — <see cref="SchemaVersions.Current"/> for <see cref="Kind"/>.
    /// </summary>
    public int TargetVersion => _targetVersion;

    /// <summary>The number of registered steps. Zero in this build.</summary>
    public int MigrationCount => _byFromVersion.Count;

    /// <summary>
    /// Upgrades a document from <paramref name="fromVersion"/> to <see cref="TargetVersion"/>.
    /// </summary>
    /// <param name="document">The document as read from disk. Never mutated.</param>
    /// <param name="fromVersion">The schema version the document declares.</param>
    /// <param name="upgraded">
    /// On success, a detached document at <see cref="TargetVersion"/> — a copy even when no step ran.
    /// On failure, the unmodified <paramref name="document"/>.
    /// </param>
    /// <param name="applied">
    /// On success, one entry per step applied, in the order applied; empty when the document was
    /// already current. On failure, empty: nothing is applied unless the whole chain resolves.
    /// </param>
    /// <param name="error">On failure, why. On success, <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="upgraded"/> is at <see cref="TargetVersion"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public bool TryUpgrade(
        JsonObject document,
        int fromVersion,
        out JsonObject upgraded,
        out IReadOnlyList<string> applied,
        out string? error)
    {
        return TryUpgrade(document, fromVersion, _targetVersion, out upgraded, out applied, out error);
    }

    /// <summary>
    /// Upgrades a document from <paramref name="fromVersion"/> to an explicit
    /// <paramref name="targetVersion"/>.
    /// </summary>
    /// <remarks>
    /// Production callers use the two-argument overload, which targets the current version. This
    /// overload exists so a chain longer than the shipped schema can be exercised while every
    /// <see cref="SchemaVersions"/> constant is still 1.
    /// </remarks>
    /// <param name="document">The document as read from disk. Never mutated.</param>
    /// <param name="fromVersion">The schema version the document declares.</param>
    /// <param name="targetVersion">The version to reach. Must be at least 1.</param>
    /// <param name="upgraded">
    /// On success, a detached document at <paramref name="targetVersion"/> — a copy even when no
    /// step ran. On failure, the unmodified <paramref name="document"/>.
    /// </param>
    /// <param name="applied">
    /// On success, one entry per step applied, in the order applied; empty when the document was
    /// already at <paramref name="targetVersion"/>. On failure, empty.
    /// </param>
    /// <param name="error">On failure, why. On success, <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="upgraded"/> is at <paramref name="targetVersion"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetVersion"/> is below 1.</exception>
    public bool TryUpgrade(
        JsonObject document,
        int fromVersion,
        int targetVersion,
        out JsonObject upgraded,
        out IReadOnlyList<string> applied,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetVersion, 1);

        upgraded = document;
        applied = NoMigrations;
        error = null;

        if (fromVersion < 1)
        {
            error = $"{_kind} schema version {fromVersion} is below the first released version 1.";
            return false;
        }

        if (fromVersion > targetVersion)
        {
            error = $"{_kind} schema version {fromVersion} is newer than the target version "
                + $"{targetVersion} and cannot be upgraded.";
            return false;
        }

        // Resolve the whole chain first so a gap never leaves a partly-migrated document behind.
        List<IJsonSchemaMigration> plan = new();
        int version = fromVersion;
        while (version < targetVersion)
        {
            if (!_byFromVersion.TryGetValue(version, out IJsonSchemaMigration? step))
            {
                error = $"No {_kind} migration is registered from schema version {version} to "
                    + $"{version + 1}; cannot reach version {targetVersion} from {fromVersion}.";
                return false;
            }

            if (step.ToVersion > targetVersion)
            {
                error = $"The {_kind} migration from schema version {step.FromVersion} produces "
                    + $"version {step.ToVersion}, overshooting the target version {targetVersion}.";
                return false;
            }

            plan.Add(step);
            version = step.ToVersion;
        }

        JsonObject working = (JsonObject)document.DeepClone();
        List<string> log = new(plan.Count);

        foreach (IJsonSchemaMigration step in plan)
        {
            JsonObject? result = step.Apply(working);
            if (result is null)
            {
                error = $"The {_kind} migration from schema version {step.FromVersion} to "
                    + $"{step.ToVersion} returned no document.";
                upgraded = document;
                applied = NoMigrations;
                return false;
            }

            working = result;
            log.Add($"{_kind} {step.FromVersion}->{step.ToVersion}");
        }

        upgraded = working;
        applied = log;
        return true;
    }
}
