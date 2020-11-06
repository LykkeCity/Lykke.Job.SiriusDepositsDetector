using System;
using System.Threading.Tasks;
using AzureStorage;
using Lykke.Job.SiriusDepositsDetector.Domain.Repositories;

namespace Lykke.Job.SiriusDepositsDetector.AzureRepositories
{
    public class OperationIdsRepository : IOperationIdsRepository
    {
        private readonly INoSQLTableStorage<OperationIdEntity> _tableStorage;

        public OperationIdsRepository(INoSQLTableStorage<OperationIdEntity> tableStorage)
        {
            _tableStorage = tableStorage;
        }

        public async Task<Guid> GetOperationIdAsync(long depositId)
        {
            var entity = await _tableStorage.GetOrInsertAsync(OperationIdEntity.GetPk(depositId),
                OperationIdEntity.GetRk(depositId),
                () => OperationIdEntity.Create(depositId));

            return Guid.Parse(entity.OperationId);
        }
    }
}
