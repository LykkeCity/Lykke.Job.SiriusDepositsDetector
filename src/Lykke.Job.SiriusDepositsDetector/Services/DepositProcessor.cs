using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Common;
using Common.Log;
using Lykke.Common.Log;
using Lykke.Cqrs;
using Lykke.Job.SiriusDepositsDetector.Contract;
using Lykke.Job.SiriusDepositsDetector.Contract.Events;
using Lykke.Job.SiriusDepositsDetector.Domain.Repositories;
using Lykke.MatchingEngine.Connector.Abstractions.Services;
using Lykke.MatchingEngine.Connector.Models.Api;
using Lykke.Service.Assets.Client.Models;
using Swisschain.Sirius.Api.ApiContract.Deposit;

namespace Lykke.Job.SiriusDepositsDetector.Services
{
    public class DepositProcessor 
    {
        private readonly ILog _log;
        private readonly IOperationIdsRepository _operationIdsRepository;
        private readonly IMatchingEngineClient _meClient;
        private readonly ICqrsEngine _cqrsEngine;

        public DepositProcessor(ILogFactory logFactory,
            IOperationIdsRepository operationIdsRepository,
            IMatchingEngineClient meClient,
            ICqrsEngine cqrsEngine)
        {
            _log = logFactory.CreateLog(this);
            _operationIdsRepository = operationIdsRepository;
            _meClient = meClient;
            _cqrsEngine = cqrsEngine;
        }

        public async Task<bool> Process(IReadOnlyCollection<Asset> assets, DepositUpdateResponse item)
        {
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
                
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.UserNativeId))
            {
                _log.Warning("UserNativeId is empty", context: new
                {
                    SiriusDepositId = item.DepositId,
                    TransactionId = item.TransactionInfo?.TransactionId
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

                    return true;

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
}
