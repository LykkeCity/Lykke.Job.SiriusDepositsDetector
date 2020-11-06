namespace Lykke.Job.SiriusDepositsDetector.Settings
{
    public class SiriusApiServiceClientSettings
    {
        public string GrpcServiceUrl { get; set; }
        public string ApiKey { get; set; }
        public long BrokerAccountId { get; set; }
    }
}
