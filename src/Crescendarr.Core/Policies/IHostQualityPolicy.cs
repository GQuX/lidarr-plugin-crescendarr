namespace Crescendarr.Core.Policies;

/// <summary>Comparison is supplied by Lidarr's relevant profile, NEVER inferred from format/size.</summary>
public enum IncomingQuality { StrictlyPreferred, Equal, Inferior, Incomparable }

public interface IHostQualityPolicy
{
    IncomingQuality Compare(long albumId, string incomingHostQualityToken, string existingHostQualityToken);
}
