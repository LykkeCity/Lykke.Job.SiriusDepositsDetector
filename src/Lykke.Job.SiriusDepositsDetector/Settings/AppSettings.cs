using Lykke.Job.SiriusDepositsDetector.Settings.JobSettings;
using Lykke.Sdk.Settings;

namespace Lykke.Job.SiriusDepositsDetector.Settings
{
    public class AppSettings : BaseAppSettings
    {
        public SiriusDepositsDetectorJobSettings SiriusDepositsDetectorJob { get; set; }
        public SiriusApiServiceClientSettings SiriusApiServiceClient { get; set; }
        public MatchingEngineSettings MatchingEngineClient { get; set; }
        public AssetsServiceClientSettings AssetsServiceClient { get; set; }
    }
}
