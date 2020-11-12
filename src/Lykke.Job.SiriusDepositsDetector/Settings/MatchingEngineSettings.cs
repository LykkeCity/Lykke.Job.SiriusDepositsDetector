using JetBrains.Annotations;

namespace Lykke.Job.SiriusDepositsDetector.Settings
{
    [UsedImplicitly]
    public class MatchingEngineSettings
    {
        [UsedImplicitly(ImplicitUseKindFlags.Assign)]
        public IpEndpointSettings IpEndpoint { get; set; }
    }
}
