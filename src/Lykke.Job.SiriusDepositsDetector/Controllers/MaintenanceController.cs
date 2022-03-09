using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Common.Log;
using Lykke.Common.Log;
using Lykke.Job.SiriusDepositsDetector.ApiModels;
using Lykke.Job.SiriusDepositsDetector.Services;
using Lykke.Job.SiriusDepositsDetector.Settings;
using Lykke.Service.Assets.Client;
using Microsoft.AspNetCore.Mvc;
using Swisschain.Sirius.Api.ApiClient;
using Swisschain.Sirius.Api.ApiContract.Deposit;

namespace Lykke.Job.SiriusDepositsDetector.Controllers
{
    [Route("api/maintenance")]
    public class MaintenanceController : ControllerBase
    {
        private readonly IApiClient _apiClient;
        private readonly IAssetsServiceWithCache _assetsService;
        private readonly DepositProcessor _depositProcessor;
        private readonly long _brokerAccountId;
        private ILog _log;

        public MaintenanceController(
            ILogFactory logFactory,
            IApiClient apiClient,
            IAssetsServiceWithCache assetsService,
            SiriusApiServiceClientSettings siriusClientSettings,
            DepositProcessor depositProcessor)
        {
            _apiClient = apiClient;
            _assetsService = assetsService;
            _depositProcessor = depositProcessor;
            _brokerAccountId = siriusClientSettings.BrokerAccountId;
            _log = logFactory.CreateLog(this);
        }

        [HttpPost("process-deposit")]
        public async Task<ActionResult> ProcessDeposit([FromBody] ProcessDepositRequest request, CancellationToken cancellationToken)
        {
            _log.Info("Manual processing of the deposit is being started", context: new
            {
                SiriusDepositId = request.SiriusDepositId
            });

            var assets = await _assetsService.GetAllAssetsAsync(false, cancellationToken);

            var siriusRequest = new DepositUpdateSearchRequest
            {
                StreamId = Guid.NewGuid().ToString(),
                BrokerAccountId = _brokerAccountId,
                DepositId = request.SiriusDepositId,
                State = 
                {
                    DepositState.Confirmed
                }
            };
                           
            _log.Info("Getting updates...", context: new
            {
                StreamId = siriusRequest.StreamId,
                Cursor = siriusRequest.Cursor
            });

            var stream = _apiClient.Deposits.GetUpdates(siriusRequest);

            if(!await stream.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                _log.Info("No updates found for the deposit", context: new
                {
                    SiriusDepositId = request.SiriusDepositId
                });

                return Ok("No updates found for the deposit");
            }

            var updates = stream.ResponseStream.Current;

            if(updates.Items.Count != 1)
            {
                _log.Info($"Exactly 1 update the deposit is expected but received {updates.Items.Count} updates", context: new
                {
                    SiriusDepositId = request.SiriusDepositId
                });

                return Ok("Exactly 1 update the deposit is expected but received {updates.Items.Count} updates");
            }

            var update = updates.Items.Single();

            if (await _depositProcessor.Process(assets, update))
            {
                _log.Info("Deposit was successfully processed", context: new
                {
                    SiriusDepositId = request.SiriusDepositId
                });

                return Ok("Deposit was successfully processed");
            }

            _log.Info("Deposit was not processed", context: new
            {
                SiriusDepositId = request.SiriusDepositId
            });

            return Ok("Deposit was not processed");
        }
    }
}
