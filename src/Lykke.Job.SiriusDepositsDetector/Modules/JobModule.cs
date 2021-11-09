using System;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using AzureStorage.Tables;
using Common;
using JetBrains.Annotations;
using Lykke.Common.Log;
using Lykke.Job.SiriusDepositsDetector.AzureRepositories;
using Lykke.Job.SiriusDepositsDetector.Domain.Repositories;
using Lykke.Job.SiriusDepositsDetector.Services;
using Lykke.Job.SiriusDepositsDetector.Settings;
using Lykke.Mailerlite.ApiClient;
using Lykke.Sdk;
using Lykke.Service.Assets.Client;
using Lykke.SettingsReader;

namespace Lykke.Job.SiriusDepositsDetector.Modules
{
    [UsedImplicitly]
    public class JobModule : Module
    {
        private readonly IReloadingManager<AppSettings> _appSettings;

        public JobModule(IReloadingManager<AppSettings> appSettings)
        {
            _appSettings = appSettings;
        }

        protected override void Load(ContainerBuilder builder)
        {
            builder.RegisterType<StartupManager>()
                .As<IStartupManager>()
                .SingleInstance();

            builder.RegisterType<ShutdownManager>()
                .As<IShutdownManager>()
                .AutoActivate()
                .SingleInstance();

            builder.RegisterInstance(
                new Swisschain.Sirius.Api.ApiClient.ApiClient(_appSettings.CurrentValue.SiriusApiServiceClient.GrpcServiceUrl, _appSettings.CurrentValue.SiriusApiServiceClient.ApiKey)
            ).As<Swisschain.Sirius.Api.ApiClient.IApiClient>();
            
            builder
                .RegisterInstance(new LykkeMailerliteClient(_appSettings.CurrentValue.MailerliteServiceClient.GrpcServiceUrl, true))
                .As<ILykkeMailerliteClient>();
            
            builder.RegisterType<DepositsDetectorService>()
                .WithParameter(TypedParameter.From(_appSettings.CurrentValue.SiriusApiServiceClient.BrokerAccountId))
                .As<IStartable>()
                .As<IStopable>()
                .AutoActivate()
                .SingleInstance();

            builder.Register(ctx =>
                new LastCursorRepository(AzureTableStorage<CursorEntity>.Create(
                    _appSettings.ConnectionString(x => x.SiriusDepositsDetectorJob.Db.DataConnString),
                    "LastDepostCursors", ctx.Resolve<ILogFactory>()))
            ).As<ILastCursorRepository>().SingleInstance();

            builder.Register(ctx =>
                new OperationIdsRepository(AzureTableStorage<OperationIdEntity>.Create(
                    _appSettings.ConnectionString(x => x.SiriusDepositsDetectorJob.Db.DataConnString),
                    "OperationIds", ctx.Resolve<ILogFactory>()))
            ).As<IOperationIdsRepository>().SingleInstance();

            builder.RegisterMeClient(_appSettings.CurrentValue.MatchingEngineClient.IpEndpoint.GetClientIpEndPoint(), true);
            builder.RegisterAssetsClient(
                AssetServiceSettings.Create(
                    new Uri(_appSettings.CurrentValue.AssetsServiceClient.ServiceUrl),
                    _appSettings.CurrentValue.AssetsServiceClient.ExpirationPeriod));
        }
    }
}
