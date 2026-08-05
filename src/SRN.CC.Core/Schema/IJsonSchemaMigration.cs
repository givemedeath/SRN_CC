using System.Text.Json.Nodes;

namespace SRN.CC.Core.Schema;

/// <summary>
/// One forward step of a JSON-backed schema, from <see cref="FromVersion"/> to
/// <see cref="ToVersion"/>. Steps are registered with a <see cref="SchemaMigrationPipeline"/>, which
/// composes them in ascending version order.
/// </summary>
/// <remarks>
/// No migration ships with this build; the seam exists so that the first real one is a new class and
/// a registration, not a change to any store.
/// </remarks>
public interface IJsonSchemaMigration
{
    /// <summary>The artefact kind this step applies to.</summary>
    SchemaKind Kind { get; }

    /// <summary>The schema version this step reads. Must be at least 1.</summary>
    int FromVersion { get; }

    /// <summary>The schema version this step produces. Must be greater than <see cref="FromVersion"/>.</summary>
    int ToVersion { get; }

    /// <summary>
    /// Transforms a document from <see cref="FromVersion"/> to <see cref="ToVersion"/>.
    /// </summary>
    /// <param name="document">
    /// A document the pipeline owns outright — a detached copy of the caller's input, safe to mutate
    /// in place. Implementations may return it or return a new object.
    /// </param>
    /// <returns>The migrated document. Must not be <see langword="null"/>.</returns>
    JsonObject Apply(JsonObject document);
}
