using System;
using MessagePack;

namespace Lykke.Job.SiriusDepositsDetector.Contract.Events
{
    [MessagePackObject(keyAsPropertyName: true)]
    public class CashinCompletedEvent
    {
        public Guid OperationId { get; set; }
        public string ClientId { get; set; }
        public decimal Amount { get; set; }
        public string AssetId { get; set; }
        public string TransactionHash { get; set; }
    }
}
