using System;
using Lykke.AzureStorage.Tables;

namespace Lykke.Job.SiriusDepositsDetector.AzureRepositories
{
    public class OperationIdEntity : AzureTableEntity
    {
        public long DepositId { get; set; }
        public string OperationId { get; set; }

        public static string GetPk(long depositId) => depositId.ToString();
        public static string GetRk(long depositId) => depositId.ToString();

        public static OperationIdEntity Create(long depositId)
        {
            string operationId = Guid.NewGuid().ToString();

            return new OperationIdEntity
            {
                PartitionKey = GetPk(depositId), RowKey = GetRk(depositId), OperationId = operationId
            };
        }
    }
}
