﻿using System;
using Lykke.SettingsReader.Attributes;

namespace Lykke.Job.SiriusDepositsDetector.Settings
{
    public class AssetsServiceClientSettings
    {
        [HttpCheck("api/isalive")]
        public string ServiceUrl { get; set; }

        public TimeSpan ExpirationPeriod { get; set; }
    }
}
