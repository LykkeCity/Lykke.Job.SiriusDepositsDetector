using JetBrains.Annotations;

namespace Lykke.Job.SiriusDepositsDetector.Settings.JobSettings
{
    [UsedImplicitly]
    public class SiriusDepositsDetectorJobSettings
    {
        public DbSettings Db { get; set; }
        public CqrsSettings Cqrs { get; set; }
    }
}
