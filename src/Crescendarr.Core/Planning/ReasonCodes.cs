namespace Crescendarr.Core.Planning;

public static class ReasonCodes
{
    public const string EditionMismatch = "EDITION_MISMATCH";
    public const string InvalidSnapshot = "INVALID_SNAPSHOT";
    public const string UnverifiedIdentity = "UNVERIFIED_IDENTITY";
    public const string SourceChanged = "SOURCE_UNSTABLE";
    public const string HostIneligible = "HOST_IMPORT_INELIGIBLE";
    public const string CompoundFile = "COMPOUND_FILE_REVIEW";
    public const string DuplicateCandidate = "DUPLICATE_CANDIDATE";
    public const string DuplicateRecording = "DUPLICATE_RECORDING";
    public const string ExistingUnverified = "EXISTING_FILE_UNVERIFIED";
    public const string MissingQuality = "QUALITY_EVIDENCE_MISSING";
    public const string QualityUnknown = "HOST_QUALITY_COMPARISON_UNAVAILABLE";
    public const string ExistingBetter = "EXISTING_EQUAL_OR_PREFERRED";
    public const string Upgrade = "STRICT_HOST_UPGRADE";
    public const string AddMissing = "VERIFIED_MISSING_RECORDING";
}
