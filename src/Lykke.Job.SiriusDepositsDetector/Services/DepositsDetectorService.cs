using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Common;
using Common.Log;
using Grpc.Core;
using Lykke.Common.Log;
using Lykke.Job.SiriusDepositsDetector.Domain.Repositories;
using Lykke.Job.SiriusDepositsDetector.Settings;
using Lykke.Service.Assets.Client;
using Swisschain.Sirius.Api.ApiClient;
using Swisschain.Sirius.Api.ApiContract.Deposit;

namespace Lykke.Job.SiriusDepositsDetector.Services
{
    public class DepositsDetectorService : IStartable, IStopable
    {
        private readonly ILastCursorRepository _lastCursorRepository;
        private readonly IAssetsServiceWithCache _assetsService;
        private readonly IApiClient _apiClient;
        private readonly long _brokerAccountId;
        private readonly DepositProcessor _depositProcessor;
        private long? _lastCursor;
        private readonly ILog _log;
        private CancellationTokenSource _cancellationTokenSource;

        public DepositsDetectorService(
            ILastCursorRepository lastCursorRepository,
            IAssetsServiceWithCache assetsService,
            IApiClient apiClient,
            SiriusApiServiceClientSettings siriusClientSettings,
            ILogFactory logFactory,
            DepositProcessor depositProcessor)
        {
            _lastCursorRepository = lastCursorRepository;
            _assetsService = assetsService;
            _apiClient = apiClient;
            _brokerAccountId = siriusClientSettings.BrokerAccountId;
            _depositProcessor = depositProcessor;
            _log = logFactory.CreateLog(this);
        }

        public void Start()
        {
            Task.Run(async () => await ProcessDepositsAsync());
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
        }

        public void Stop()
        {
            _cancellationTokenSource?.Cancel();
        }

        private async Task ProcessDepositsAsync()
        {
            var errorsInTheRowCount = 0;
            _cancellationTokenSource = new CancellationTokenSource();

            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                _lastCursor = await _lastCursorRepository.GetAsync(_brokerAccountId);
                var assets = await _assetsService.GetAllAssetsAsync(false, _cancellationTokenSource.Token);

                try
                {
                    var request = new DepositUpdateSearchRequest
                    {
                        StreamId = Guid.NewGuid().ToString(),
                        BrokerAccountId = _brokerAccountId,
                        Cursor = _lastCursor,
                        State =
                        {
                            DepositState.Confirmed
                        }
                    };

                    _log.Info("Getting updates...", context: new
                    {
                        StreamId = request.StreamId,
                        Cursor = request.Cursor
                    });

                    var stream = _apiClient.Deposits.GetUpdates(request);

                    while (await stream.ResponseStream.MoveNext(_cancellationTokenSource.Token).ConfigureAwait(false))
                    {
                        var updates = stream.ResponseStream.Current;

                        _log.Info($"Received stream batch of {updates.Items.Count} items");

                        foreach (var item in updates.Items)
                        {
                            if (item.DepositUpdateId <= _lastCursor)
                                continue;

                            if (await _depositProcessor.Process(assets, item))
                            {
                                await _lastCursorRepository.AddAsync(_brokerAccountId, item.DepositUpdateId);

                                errorsInTheRowCount = 0;
                                _lastCursor = item.DepositUpdateId;
                            }
                        }
                    }

                    _log.Info("End of stream");
                }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
                {
                    _log.Info("Request was canceleed", ex);
                }
                catch (RpcException ex)
                {
                    errorsInTheRowCount++;
                    if (ex.StatusCode == StatusCode.ResourceExhausted)
                    {
                        _log.Warning("Rate limit has been reached. Waiting 1 minute...", ex);
                        await Task.Delay(60000);
                    }
                    else
                    {
                        _log.Warning($"RpcException. {ex.Status}; {ex.StatusCode}", ex);
                    }
                }
                catch (Exception ex)
                {
                    errorsInTheRowCount++;
                    _log.Error(ex);
                }

                if (_cancellationTokenSource.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    // Just in case we didn't foresee something
                    checked
                    {
                        // It's needed to prevent overflow of the counter.
                        errorsInTheRowCount = Math.Min(int.MaxValue - 1, errorsInTheRowCount);

                        // Initial 2 seconds, 30 minutes max (if more than 20 errors in the row)
                        // It's needed to limit pow to avoid overflow of the Math.Pow result
                        var pow = Math.Min(21, errorsInTheRowCount);
                        var delay = Math.Min(30 * 60 * 1000, 1000 * (int)Math.Pow(2, pow));


                        _log.Info($"Will retry in {delay} milliseconds");

                        await Task.Delay(delay);
                    }
                } 
                catch(Exception ex)
                {
                    _log.Error(ex);

                    await Task.Delay(60000);
                }
            }
        }
    }
}
