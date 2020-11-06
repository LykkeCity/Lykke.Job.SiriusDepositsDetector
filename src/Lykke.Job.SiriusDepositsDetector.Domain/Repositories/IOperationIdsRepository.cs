using System;
using System.Threading.Tasks;

namespace Lykke.Job.SiriusDepositsDetector.Domain.Repositories
{
    public interface IOperationIdsRepository
    {
        Task<Guid> GetOperationIdAsync(long depositId);
    }
}
