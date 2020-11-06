using JetBrains.Annotations;
using Lykke.SettingsReader.Attributes;

namespace Lykke.Job.SiriusDepositsDetector.Settings.JobSettings
{
    [UsedImplicitly]
    public class CqrsSettings
    {
        [AmqpCheck]
        public string RabbitConnectionString { get; set; }
    }
}
