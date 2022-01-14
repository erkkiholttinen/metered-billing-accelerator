namespace Metering.Aggregator
{
    using System.Reactive.Linq;
    using System.Collections.Concurrent;
    using Metering.Types;
    using Metering.Types.EventHub;
    using Metering.ClientSDK;
    using SomeMeterCollection = Microsoft.FSharp.Core.FSharpOption<Metering.Types.MeterCollection>;

    // https://github.com/Azure/azure-sdk-for-net/blob/main/sdk/eventhub/Azure.Messaging.EventHubs/samples/Sample05_ReadingEvents.md

    public static class AggregatorWorkerExtensions
    {
        public static void RegisterMeteringAggregator(this IServiceCollection services)
        {
            services.AddSingleton(MeteringConfigurationProviderModule.create(
                connections: MeteringConnectionsModule.getFromEnvironment(),
                marketplaceClient: MarketplaceClient.submitUsagesCsharp.ToFSharpFunc()));

            services.AddHostedService<AggregatorWorker>();
        }
    }

    public class AggregatorWorker : BackgroundService
    {
        private readonly ILogger<AggregatorWorker> _logger;
        private readonly MeteringConfigurationProvider config;

        public AggregatorWorker(ILogger<AggregatorWorker> logger, MeteringConfigurationProvider meteringConfigurationProvider)
        {
            (_logger, config) = (logger, meteringConfigurationProvider);
        }

        private IDisposable SubscribeEmitter(IObservable<MeterCollection> events)
        {
            List<MarketplaceRequest> previousToBeSubmitted = new();
            ConcurrentQueue<MarketplaceRequest[]> tobeSubmitted = new();
            var producer = config.MeteringConnections.createEventHubProducerClient();

            // Run an endless loop,
            // - to look at the concurrent queue,
            // - submit REST calls to marketplace, and then
            // - submit the marketplace responses to EventHub. 
            var task = Task.Factory.StartNew(async () => {
                while (true)
                {
                    await Task.Delay(1000);
                    if (tobeSubmitted.TryDequeue(out var usage))
                    {
                        var response = await config.SubmitUsage(usage);
                        await producer.ReportUsagesSubmitted(response, CancellationToken.None);
                    }
                }
            });

            return events
                .Subscribe(
                    onNext: meterCollection =>
                    {
                        // Only add new (unseen) events to the concurrent queue.
                        var current = meterCollection.metersToBeSubmitted().ToList();
                        var newOnes = current.Except(previousToBeSubmitted).ToList();
                        if (newOnes.Any())
                        {
                            newOnes
                                .Chunk(25)
                                .ForEach(tobeSubmitted.Enqueue);
                        }
                        previousToBeSubmitted = current;
                    }
                );
        }

        private void RegularlyCreateSnapshots(PartitionID partitionId, MeterCollection meterCollection, Func<string> prefix)
        {
            if (meterCollection.getLastSequenceNumber() % 100 == 0)
            {
                _logger.LogInformation($"{prefix()} Processed event {partitionId.value()}#{meterCollection.getLastSequenceNumber()}");
            }

            if (meterCollection.getLastSequenceNumber() % 500 == 0)
            {
                MeterCollectionStore.storeLastState(config, meterCollection: meterCollection).Wait();
                _logger.LogInformation($"{prefix()} Saved state {partitionId.value()}#{meterCollection.getLastSequenceNumber()}");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Func<SomeMeterCollection, EventHubProcessorEvent<SomeMeterCollection, MeteringUpdateEvent>, SomeMeterCollection> accumulator =
                MeteringAggregator.createAggregator(config);

            List<IDisposable> subscriptions = new();

            // pretty-print which partitions we already 'own'
            var props = await config.MeteringConnections.createEventHubConsumerClient().GetEventHubPropertiesAsync(stoppingToken);
            var partitions = new string[props.PartitionIds.Length];
            Array.Fill(partitions, "_");
            string currentPartitions() => string.Join("", partitions);

            var groupedSub = Metering.EventHubObservableClient
                .create(config, stoppingToken)
                .Subscribe(
                    onNext: group => {
                        var partitionId = group.Key;
                        partitions[int.Parse(partitionId.value())] = partitionId.value();

                        IObservable<MeterCollection> events = group
                            .Scan(seed: MeterCollectionModule.Uninitialized, accumulator: accumulator)
                            .Choose(); // '.Choose()' is cleaner than '.Where(x => x.IsSome()).Select(x => x.Value)'

                        // Subscribe the creation of snapshots
                        events
                            .Subscribe(
                                onNext: coll => RegularlyCreateSnapshots(partitionId, coll, currentPartitions),
                                onError: ex =>
                                {
                                    _logger.LogError($"Error {partitionId.value()}: {ex.Message}");
                                },
                                onCompleted: () =>
                                {
                                    _logger.LogWarning($"Closing {partitionId.value()}");
                                    partitions[int.Parse(partitionId.value())] = "_";
                                })
                            .AddToSubscriptions(subscriptions);

                        // Subscribe the submission to marketplace.
                        SubscribeEmitter(events)
                            .AddToSubscriptions(subscriptions);
                    },
                    onCompleted: () => {
                        _logger.LogWarning($"Closing everything");
                    }, 
                    onError: ex => {
                        _logger.LogCritical($"Error: {ex.Message}");
                    }
                );
            subscriptions.Add(groupedSub);

            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }

            subscriptions.ForEach(subscription => subscription.Dispose());
        }
    }

    internal static class E
    {
        public static void AddToSubscriptions(this IDisposable i, List<IDisposable> l) => l.Add(i);
        public static string UpTo(this string s, int length) => s.Length > length ? s[..length] : s;
        public static string W(this string s, int width) => String.Format($"{{0,-{width}}}", s);
        public static void ForEach<T>(this IEnumerable<T> ts, Action<T> action) { foreach (var t in ts) { action(t); } }
    }
}