using Crescendarr.Core.Domain;
using Crescendarr.Core.Policies;

namespace Crescendarr.Core.Planning;

public interface IMergePlanner
{
    MergePlan Plan(DownloadSnapshot incoming, AlbumSnapshot current, IHostQualityPolicy hostQuality);
}
