﻿namespace Metering.Types

open System
open System.Runtime.CompilerServices
open Microsoft.Extensions.Configuration
open Azure.Core
open Azure.Storage.Blobs
open Azure.Messaging.EventHubs.Consumer
open Azure.Messaging.EventHubs.Producer
open Azure.Messaging.EventHubs

type EventHubName =
    { NamespaceName: string
      FullyQualifiedNamespace: string
      InstanceName: string }

module EventHubName =
    let create nameSpaceName instanceName =
        { NamespaceName = nameSpaceName
          FullyQualifiedNamespace = $"{nameSpaceName}.servicebus.windows.net"
          InstanceName = instanceName }

    let toStr (e: EventHubName) = $"{e.FullyQualifiedNamespace}/{e.InstanceName}"

type CaptureStorage = 
    { CaptureFileNameFormat: string
      Storage: BlobContainerClient }

type EventHubConfig =
    { EventHubName: EventHubName
      ConsumerGroupName: string
      CheckpointStorage: BlobContainerClient
      CaptureStorage: CaptureStorage option
      InfraStructureCredentials: TokenCredential }

type MeteringConnections =
    { MeteringAPICredentials: MeteringAPICredentials 
      SnapshotStorage: BlobContainerClient      
      EventHubConfig: EventHubConfig }

[<Extension>]
module MeteringConnections =
    let private environmentVariablePrefix = "AZURE_METERING_"

    let private getFromConfig (get: (string -> string option)) (consumerGroupName: string) =
        let getRequired var =
            match var |> get with
            | Some s -> s
            | None -> failwith $"Missing configuration {environmentVariablePrefix}{var}"

        let meteringApiCredential = 
            match ("MARKETPLACE_TENANT_ID" |> get, "MARKETPLACE_CLIENT_ID" |> get, "MARKETPLACE_CLIENT_SECRET" |> get) with
            | (Some t, Some i, Some s) -> MeteringAPICredentials.createServicePrincipal t i s
            | (None, None, None) -> ManagedIdentity
            | _ -> failwith $"The {nameof(MeteringAPICredentials)} configuration is incomplete."
            
        let infraStructureCredential = 
            match ("INFRA_TENANT_ID" |> get, "INFRA_CLIENT_ID" |> get, "INFRA_CLIENT_SECRET" |> get) with
            | (Some t, Some i, Some s) -> InfraStructureCredentials.createServicePrincipal t i s
            | (None, None, None) -> InfraStructureCredentials.createManagedIdentity()
            | _ -> failwith $"The {nameof(InfraStructureCredentials)} configuration is incomplete."
                    
        let containerClientWith (cred: TokenCredential) uri = new BlobContainerClient(blobContainerUri = new Uri(uri), credential = cred)

        let captureStorage =
            match ("INFRA_CAPTURE_CONTAINER" |> get, "INFRA_CAPTURE_FILENAME_FORMAT" |> get) with
            | (None, None) -> None
            | (Some c, Some f) -> 
                { Storage = c |> containerClientWith infraStructureCredential 
                  CaptureFileNameFormat = f }
                |> Some
            |  _ -> failwith $"The {nameof(CaptureStorage)} configuration is incomplete."

        let nsn = "INFRA_EVENTHUB_NAMESPACENAME" |> getRequired

        { MeteringAPICredentials = meteringApiCredential
          SnapshotStorage = "INFRA_SNAPSHOTS_CONTAINER" |> getRequired |> containerClientWith infraStructureCredential
          EventHubConfig = 
            { CheckpointStorage = "INFRA_CHECKPOINTS_CONTAINER" |> getRequired |> containerClientWith infraStructureCredential 
              CaptureStorage = captureStorage
              ConsumerGroupName = consumerGroupName
              EventHubName = EventHubName.create nsn ("INFRA_EVENTHUB_INSTANCENAME" |> getRequired)
              InfraStructureCredentials = infraStructureCredential } }

    let getFromEnvironmentWithSpecificConsumerGroup (consumerGroupName: string) =
        let configuration =
            // Doing this convoluted syntax as c# extension methods seem unavailable.
            EnvironmentVariablesExtensions.AddEnvironmentVariables(
                new ConfigurationBuilder(),
                prefix = environmentVariablePrefix).Build()

        let get key = 
            match configuration.Item(key) with
            | v when String.IsNullOrWhiteSpace(v) -> None
            | v -> Some v

        getFromConfig get EventHubConsumerClient.DefaultConsumerGroupName

    let getFromEnvironment () =
        getFromEnvironmentWithSpecificConsumerGroup EventHubConsumerClient.DefaultConsumerGroupName
    
    [<Extension>]
    let createEventHubConsumerClient (connections: MeteringConnections) : EventHubConsumerClient =
        new EventHubConsumerClient(
            consumerGroup = connections.EventHubConfig.ConsumerGroupName,
            fullyQualifiedNamespace = connections.EventHubConfig.EventHubName.FullyQualifiedNamespace,
            eventHubName = connections.EventHubConfig.EventHubName.InstanceName,
            credential = connections.EventHubConfig.InfraStructureCredentials)

    [<Extension>]
    let createEventHubProducerClient (connections: MeteringConnections) : EventHubProducerClient =
        new EventHubProducerClient(
            fullyQualifiedNamespace = connections.EventHubConfig.EventHubName.FullyQualifiedNamespace,
            eventHubName = connections.EventHubConfig.EventHubName.InstanceName,
            credential = connections.EventHubConfig.InfraStructureCredentials,
            clientOptions = new EventHubProducerClientOptions(
                ConnectionOptions = new EventHubConnectionOptions(
                    TransportType = EventHubsTransportType.AmqpTcp)))

    [<Extension>]
    let createEventProcessorClient (connections: MeteringConnections) : EventProcessorClient =
        new EventProcessorClient(
            checkpointStore = connections.EventHubConfig.CheckpointStorage,
            consumerGroup = connections.EventHubConfig.ConsumerGroupName,
            fullyQualifiedNamespace = connections.EventHubConfig.EventHubName.FullyQualifiedNamespace,
            eventHubName = connections.EventHubConfig.EventHubName.InstanceName,
            credential = connections.EventHubConfig.InfraStructureCredentials,
            clientOptions = new EventProcessorClientOptions(
                TrackLastEnqueuedEventProperties = true,
                PartitionOwnershipExpirationInterval = TimeSpan.FromMinutes(1),
                PrefetchCount = 100))
