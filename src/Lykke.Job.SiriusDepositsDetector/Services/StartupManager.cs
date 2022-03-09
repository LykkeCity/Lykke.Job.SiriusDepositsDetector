using System.Threading.Tasks;
using Antares.Sdk.Services;
using Common.Log;
using Lykke.Common.Log;
using Lykke.Cqrs;

namespace Lykke.Job.SiriusDepositsDetector.Services
{
    // NOTE: Sometimes, startup process which is expressed explicitly is not just better,
    // but the only way. If this is your case, use this class to manage startup.
    // For example, sometimes some state should be restored before any periodical handler will be started,
    // or any incoming message will be processed and so on.
    // Do not forget to remove As<IStartable>() and AutoActivate() from DI registartions of services,
    // which you want to startup explicitly.

    public class StartupManager : IStartupManager
    {
        private readonly ICqrsEngine _cqrsEngine;
        private readonly ILog _log;

        public StartupManager(
            ICqrsEngine cqrsEngine,
            ILogFactory logFactory)
        {
            _cqrsEngine = cqrsEngine;
            _log = logFactory.CreateLog(this);
        }

        public async Task StartAsync()
        {
            _cqrsEngine.StartSubscribers();
            await Task.CompletedTask;
        }
    }
}
