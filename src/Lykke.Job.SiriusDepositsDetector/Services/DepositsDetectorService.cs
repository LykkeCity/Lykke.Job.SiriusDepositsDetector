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
using Lykke.Job.SiriusDepositsDetector.Utils;
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
                            
                    _log.Info("Getting updates...", context: $"request: {request.ToJson()}");

                    var updates = _apiClient.Deposits.GetUpdates(request);

                    while (await updates.ResponseStream.MoveNext(_cancellationTokenSource.Token).ConfigureAwait(false))
                    {
                        DepositUpdateArrayResponse update = updates.ResponseStream.Current;

                        foreach (var item in update.Items)
                        {
                            if (item.DepositUpdateId <= _lastCursor)
                                continue;

                            if (string.IsNullOrWhiteSpace(item.UserNativeId))
                            {
                                _log.Warning("UserNativeId is empty");
                                continue;
                            }

                            var decimalAmount = decimal.Parse(item.Amount.Value,
                                NumberStyles.Number,
                                CultureInfo.InvariantCulture);
                            var scale = decimalAmount.GetScale();
                            var meAmount = ((double)decimalAmount).TruncateDecimalPlaces(scale, toUpper: false);
                            
                            _log.Info("Deposit detected", context: $"deposit: {item.ToJson()}, meAmount: {meAmount}");

                            Guid operationId = await _operationIdsRepository.GetOperationIdAsync(item.DepositId);
                            string assetId = assets.FirstOrDefault(x => x.SiriusAssetId == item.AssetId)?.Id;

                            if (string.IsNullOrEmpty(assetId))
                            {
                                _log.Warning("Lykke asset not found", context: new {siriusAssetId = item.AssetId, depositId = item.DepositId});
                                continue;
                            }

                            var cashInResult = await _meClient.CashInOutAsync
                            (
                                id:  operationId.ToString(),
                                clientId: item.ReferenceId ?? item.UserNativeId,
                                assetId: assetId,
                                amount: meAmount
                            );

                            if (cashInResult == null)
                            {
                                throw new InvalidOperationException("ME response is null, don't know what to do");
                            }

                            switch (cashInResult.Status)
                            {
                                case MeStatusCodes.Ok:
                                case MeStatusCodes.Duplicate:
                                    if (cashInResult.Status == MeStatusCodes.Duplicate)
                                    {
                                        _log.Info(message: "Deduplicated by the ME", context: new {operationId, depositId = item.DepositId});
                                    }

                                    _log.Info("Deposit processed", context: new {cashInResult.TransactionId});
                                    
                                    _cqrsEngine.PublishEvent(new CashinCompletedEvent
                                    {
                                        ClientId = item.UserNativeId,
                                        AssetId = assetId,
                                        Amount = decimal.Parse(item.Amount.Value),
                                        OperationId = operationId,
                                        TransactionHash = item.TransactionInfo.TransactionId,
                                        WalletId =  item.ReferenceId == item.UserNativeId ? default(string) : item.ReferenceId,
                                    }, SiriusDepositsDetectorBoundedContext.Name);

                                    await _lastCursorRepository.AddAsync(_brokerAccountId, item.DepositUpdateId);
                                    _lastCursor = item.DepositUpdateId;

                                    break;

                                case MeStatusCodes.Runtime:
                                    throw new Exception($"Cashin into the ME is failed. ME status: {cashInResult.Status}, ME message: {cashInResult.Message}");

                                default:
                                    _log.Warning($"Unexpected response from ME. Status: {cashInResult.Status}, ME message: {cashInResult.Message}", context: operationId.ToString());
                                    break;
                            }
                        }
                    }

                    _log.Info("End of stream");
                }
                catch (RpcException ex)
                {
                    if (ex.StatusCode == StatusCode.ResourceExhausted)
                    {
                        _log.Warning($"Rate limit has been reached. Waiting 1 minute...", ex);
                        await Task.Delay(60000);
                    }
                    else
                    {
                        _log.Warning($"RpcException. {ex.Status}; {ex.StatusCode}", ex);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex);
                }

                await Task.Delay(5000);
            }
        }
    }
}
