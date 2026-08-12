namespace SRN.CC.Core.Resolution;

public enum ResolutionStatus
{
    Resolved,
    UnresolvedDuplicate,
    InvalidPin,
    Unavailable,

    /// <summary>
    /// The identity exists only in <see cref="SRN.CC.Core.Sources.SourceMode.Reference"/>
    /// sources, so no source wins it automatically. The user must pin one of the reference
    /// occurrences to include the asset in a build.
    /// </summary>
    ReferenceOnly,

    Unpackageable
}
