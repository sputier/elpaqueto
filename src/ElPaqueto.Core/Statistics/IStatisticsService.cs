using System.Collections.Generic;
using System.Threading.Tasks;

namespace ElPaqueto.Core.Statistics;

public interface IStatisticsService
{
    Task<int> GetPackagesTotalAmount();
    Task<int> GetVersionsTotalAmount();
    IEnumerable<string> GetKnownServices();
}
