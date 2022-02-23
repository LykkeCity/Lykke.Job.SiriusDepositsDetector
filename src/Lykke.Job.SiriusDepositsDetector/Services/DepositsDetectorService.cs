using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Common;
using Common.Log;
using Grpc.Core;
using Lykke.Common.Log;
using Lykke.Cqrs;
using Lykke.Job.SiriusDepositsDetector.Contract;
using Lykke.Job.SiriusDepositsDetector.Contract.Events;
using Lykke.Job.SiriusDepositsDetector.Domain.Repositories;
using Lykke.MatchingEngine.Connector.Abstractions.Services;
using Lykke.MatchingEngine.Connector.Models.Api;
using Lykke.Service.Assets.Client;
using Swisschain.Sirius.Api.ApiClient;
using Swisschain.Sirius.Api.ApiContract.Deposit;

namespace Lykke.Job.SiriusDepositsDetector.Services
{
    public class DepositsDetectorService : IStartable, IStopable
    {
        private readonly ILastCursorRepository _lastCursorRepository;
        private readonly IOperationIdsRepository _operationIdsRepository;
        private readonly IMatchingEngineClient _meClient;
        private readonly IAssetsServiceWithCache _assetsService;
        private readonly IApiClient _apiClient;
        private readonly ICqrsEngine _cqrsEngine;
        private readonly long _brokerAccountId;
        private long? _lastCursor;
        private readonly ILog _log;
        private CancellationTokenSource _cancellationTokenSource;

        public DepositsDetectorService(
            ILastCursorRepository lastCursorRepository,
            IOperationIdsRepository operationIdsRepository,
            IMatchingEngineClient meClient,
            IAssetsServiceWithCache assetsService,
            IApiClient apiClient,
            ICqrsEngine cqrsEngine,
            long brokerAccountId,
            ILogFactory logFactory
            )
        {
            _lastCursorRepository = lastCursorRepository;
            _operationIdsRepository = operationIdsRepository;
            _meClient = meClient;
            _assetsService = assetsService;
            _apiClient = apiClient;
            _cqrsEngine = cqrsEngine;
            _brokerAccountId = brokerAccountId;
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
                        BrokerAccountId = _brokerAccountId,
                        Cursor = _lastCursor
                    };

                    request.State.Add(DepositState.Confirmed);
                            
                    _log.Info("Getting updates...", context: new
                    {
                        Cursor = request.Cursor
                    });

                    var updates = _apiClient.Deposits.GetUpdates(request);

                    while (await updates.ResponseStream.MoveNext(_cancellationTokenSource.Token).ConfigureAwait(false))
                    {
                        var update = updates.ResponseStream.Current;

                        foreach (var item in update.Items)
                        {
                            if (item.DepositUpdateId <= _lastCursor)
                                continue;

                            if (item.DepositType == DepositType.Broker)
                            {
                                _log.Info("Broker deposit skipped", new
                                {
                                    SiriusDepositId = item.DepositId,
                                    item.BlockchainId,
                                    item.AssetId,
                                    item.AssetSymbol,
                                    item.AssetAddress,
                                    item.TransactionInfo?.TransactionId
                                });
                                continue;
                            }

                            if (string.IsNullOrWhiteSpace(item.UserNativeId))
                            {
                                _log.Warning("UserNativeId is empty", context: new
                                {
                                    SiriusDepositId = item.DepositId,
                                    item.TransactionInfo?.TransactionId
                                });

                                throw new InvalidOperationException("UserNativeId is empty");
                            }

                            var asset = assets.FirstOrDefault(x => x.SiriusAssetId == item.AssetId);
                            if (asset == null)
                            {
                                _log.Warning("Lykke asset not found", context: new {siriusAssetId = item.AssetId, depositId = item.DepositId});
                                
                                throw new InvalidOperationException("Lykke asset not found");
                            }
                            
                            var decimalAmount = decimal.Parse(item.Amount.Value,
                                NumberStyles.Number,
                                CultureInfo.InvariantCulture);
                            var meAmount = ((double)decimalAmount).TruncateDecimalPlaces(asset.Accuracy, toUpper: false);
                            
                            var operationId = await _operationIdsRepository.GetOperationIdAsync(item.DepositId);

                            _log.Info("Deposit detected", context: new
                            {
                                OperationId = operationId.ToString(),
                                SiriusDepositId = item.DepositId,
                                TransactionId = item.TransactionInfo?.TransactionId,
                                AssetSymbol = asset.Symbol,
                                Amount = item.Amount,
                                MeAmount = meAmount
                            });

                            var cashInResult = await _meClient.CashInOutAsync
                            (
                                id:  operationId.ToString(),
                                clientId: item.ReferenceId ?? item.UserNativeId,
                                assetId: asset.Id,
                                amount: meAmount
                            );

                            if (cashInResult == null)
                            {
                                _log.Warning($"ME response is null, don't know what to do",
                                    context: new
                                    {
                                        OperationId = operationId.ToString(),
                                        SiriusDepositId = item.DepositId,
                                        TransactionId = item.TransactionInfo?.TransactionId,
                                        AssetSymbol = asset.Symbol
                                    });

                                throw new InvalidOperationException("ME response is null, don't know what to do");
                            }

                            switch (cashInResult.Status)
                            {
                                case MeStatusCodes.Ok:
                                case MeStatusCodes.Duplicate:
                                    errorsInTheRowCount = 0;

                                    if (cashInResult.Status == MeStatusCodes.Duplicate)
                                    {
                                        _log.Info(message: "Deduplicated by the ME", context: new
                                        {
                                            OperationId = operationId.ToString(),
                                            SiriusDepositId = item.DepositId,
                                            TransactionId = item.TransactionInfo?.TransactionId,
                                            AssetSymbol = asset.Symbol
                                        });
                                    }

                                    _log.Info("Deposit processed", context: new
                                    {
                                        OperationId = operationId.ToString(),
                                        SiriusDepositId = item.DepositId,
                                        TransactionId = item.TransactionInfo?.TransactionId
                                    });
                                    
                                    _cqrsEngine.PublishEvent(new CashinCompletedEvent
                                    {
                                        ClientId = item.UserNativeId,
                                        AssetId = asset.Id,
                                        Amount = decimal.Parse(item.Amount.Value),
                                        OperationId = operationId,
                                        TransactionHash = item.TransactionInfo.TransactionId,
                                        WalletId =  item.ReferenceId == item.UserNativeId ? default(string) : item.ReferenceId,
                                    }, SiriusDepositsDetectorBoundedContext.Name);

                                    await _lastCursorRepository.AddAsync(_brokerAccountId, item.DepositUpdateId);
                                    _lastCursor = item.DepositUpdateId;

                                    break;

                                case MeStatusCodes.Runtime:
                                    _log.Warning($"Unexpected response from ME. ME status: {cashInResult.Status}, ME message: {cashInResult.Message}",
                                        context: new
                                        {
                                            OperationId = operationId.ToString(),
                                            SiriusDepositId = item.DepositId,
                                            TransactionId = item.TransactionInfo?.TransactionId,
                                            AssetSymbol = asset.Symbol
                                        });
                                    throw new Exception($"Unexpected response from ME. ME status: {cashInResult.Status}, ME message: {cashInResult.Message}");

                                default:
                                    _log.Warning($"Unexpected response from ME. ME status: {cashInResult.Status}, ME message: {cashInResult.Message}", 
                                        context: new
                                        {
                                            OperationId = operationId.ToString(),
                                            SiriusDepositId = item.DepositId,
                                            TransactionId = item.TransactionInfo?.TransactionId,
                                            AssetSymbol = asset.Symbol
                                        });
                                    throw new Exception($"Unexpected response from ME. ME status: {cashInResult.Status}, ME message: {cashInResult.Message}");
                            }
                        }
                    }

                    _log.Info("End of stream");
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

                // Initial 2 seconds, 30 minutes max (if more than 20 errors in the row)
                var delay = Math.Min(30 * 60 * 1000, 1000 * (int)Math.Pow(2, errorsInTheRowCount));

                _log.Info($"Will retry in {delay} milliseconds");

                await Task.Delay(delay);
            }
        }
    }
}
