namespace SRN.CC.Core.Diagnostics;

public enum DiagnosticCode
{
    SourceNotFound,
    RootInaccessible,
    InvalidHeader,
    InvalidTableOverlap,
    InvalidPayloadOverlap,
    SourceDriftDetected,
    UnknownFolderExtension,
    FileReadError,
    ChangedSourceFailure,
    InvalidResref,
    InvalidInstallRoot,
    CorruptedCacheQuarantined
}
