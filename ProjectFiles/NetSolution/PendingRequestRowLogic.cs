#region Using directives
using System;
using System.Text.Json;
using System.Threading;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.MQTTClient;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NativeUI;
using FTOptix.NetLogic;
using FTOptix.MQTTBroker;
using FTOptix.Retentivity;
using FTOptix.CoreBase;
using FTOptix.Core;
using FTOptix.OPCUAServer;
using FTOptix.Alarm;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.EventLogger;
using FTOptix.OPCUAClient;
#endregion

public class PendingRequestRowLogic : BaseNetLogic
{
    private const string LogNode = "PendingRequestRowLogic";
    private const string FleetFolderName = "Fleet";
    private const string MqttBrokerName = "MQTTBroker";
    private const string MqttSupervisorClientName = "MQTTSupervisorClient";
    private const ushort DefaultLocalBrokerPort = 8883;
    private const string RowItemAliasName = "RowItem";

    // Static topic the edge project's OnboardingResultSubscriber listens on (Model/Onboarding/Result/OnboardingStatus).
    private const string OnboardingResultTopic = "OnboardingResult";

    public override void Start()
    {
        // Insert code to be executed when the user-defined logic is started
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined logic is stopped
    }

    [ExportMethod]
    public void AcceptRequest()
    {
        var sender = ReadSender();
        if (string.IsNullOrEmpty(sender))
            return;

        var endpointUrl = ReadEndpointUrl();
        Log.Info(LogNode, $"Onboarding request from '{sender}' accepted, provisioning device.");

        if (ProvisionDevice(sender, endpointUrl))
        {
            PublishOnboardingResult(sender, OnboardingStatus.Onboarded);
            FleetOverviewCollector.Record("onboarded", sender);
        }
        else
            Log.Error(LogNode, $"Provisioning failed for '{sender}', onboarding result not published.");
    }

    [ExportMethod]
    public void DiscardRequest()
    {
        var sender = ReadSender();
        if (string.IsNullOrEmpty(sender))
            return;

        Log.Info(LogNode, $"Onboarding request from '{sender}' discarded.");
        PublishOnboardingResult(sender, OnboardingStatus.Declined);
        FleetOverviewCollector.Record("declined", sender);
    }

    // Sender/Content/Timestamp live on the OnboardingRequestData instance the row points to via its
    // "RowItem" alias, not directly on the row itself, so the alias must be resolved first.
    private string ReadSender()
    {
        var rowItem = Owner.GetAlias(RowItemAliasName);
        if (rowItem == null)
        {
            Log.Error(LogNode, $"Alias '{RowItemAliasName}' not resolved under '{Owner.BrowseName}'.");
            return null;
        }

        var senderVariable = rowItem.GetVariable("Sender");
        if (senderVariable == null)
        {
            Log.Error(LogNode, $"Variable 'Sender' not found under '{RowItemAliasName}'.");
            return null;
        }

        var sender = (string)senderVariable.Value.Value;
        if (string.IsNullOrEmpty(sender))
        {
            Log.Error(LogNode, $"'{RowItemAliasName}' has an empty Sender, cannot process the request.");
            return null;
        }

        return sender;
    }

    private string ReadEndpointUrl()
    {
        var rowItem = Owner.GetAlias(RowItemAliasName);
        var contentVariable = rowItem?.GetVariable("Content");
        if (contentVariable == null)
        {
            Log.Warning(LogNode, $"Variable 'Content' not found under '{RowItemAliasName}', OPC UA client will not be created.");
            return null;
        }

        return (string)contentVariable.Value.Value;
    }

    // Broker/onboarding client are local, self-hosted infrastructure, so they're recreated with working
    // defaults if missing - unlike Fleet content, which only makes sense once real data exists.
    private bool ProvisionDevice(string sender, string endpointUrl)
    {
        if (!OvenMqttClientHelper.IsValidDeviceId(sender))
        {
            Log.Error(LogNode, $"Sender '{sender}' contains characters not allowed in MQTT topics (allowed: letters, digits, '_' and '-'), request not accepted.");
            return false;
        }

        lock (LocalFleetRuntime.OwnershipSync)
        {
            var fleet = GetOrCreateModelFolder(FleetFolderName);
            var managers = GetOrCreateModelFolder(OvenEdgeManagerBehavior.ManagersFolderName);
            if (fleet == null || managers == null)
                return false;
            LocalFleetRuntime.PrepareRemoteOwnership(sender);
            var device = RegisterDevice(fleet, sender);
            RegisterEdgeManager(managers, sender, device);
        }
        Log.Info(LogNode, $"Calling OPC UA client setup for onboarding sender '{sender}'.");
        OpcUaClientRuntimeHelper.EnsureOpcUaClient(sender, endpointUrl);
        Log.Info(LogNode, $"OPC UA client setup call completed for onboarding sender '{sender}'.");

        if (GetOrCreateMqttSupervisorClient() == null)
            return false;

        var ovenClient = OvenMqttClientHelper.EnsureOvenClient(sender, out var clientChanged);
        if (ovenClient == null)
            return false;
        if (clientChanged)
            OvenMqttClientHelper.Restart(ovenClient);

        return true;
    }

    private OvenType RegisterDevice(IUANode deviceRegistry, string sender)
    {
        var existingDevice = deviceRegistry.Get(sender) as OvenType;
        if (existingDevice != null)
        {
            Log.Info(LogNode, $"Device '{sender}' already present in Fleet.");
            return existingDevice;
        }

        var device = InformationModel.MakeObject<OvenType>(sender);
        device.DeviceId = sender;
        deviceRegistry.Add(device);
        Log.Info(LogNode, $"Created OvenType instance '{sender}' under Fleet.");
        return device;
    }

    // Pointer is set before Add so the behavior finds the telemetry as soon as it starts.
    private void RegisterEdgeManager(IUANode managersFolder, string sender, OvenType device)
    {
        var managerName = OvenEdgeManagerBehavior.GetManagerName(sender);
        var existingManager = managersFolder.Get(managerName) as OvenEdgeManager;
        if (existingManager != null)
        {
            OvenEdgeManagerBehavior.PrepareConnectionAlarm(existingManager);
            FleetAnalyticsCollector.EnsureVariable(existingManager, "ExecutionMode", OpcUa.DataTypes.Int32, 0).Value = 0;
            existingManager.OnboardingDateTime = DateTime.UtcNow;
            existingManager.TelemetryDataPointer = device.NodeId;
            Log.Info(LogNode, $"Edge manager '{managerName}' already present, telemetry pointer refreshed.");
            return;
        }

        var manager = InformationModel.MakeObject<OvenEdgeManager>(managerName);
        manager.TelemetryDataPointer = device.NodeId;
        manager.OnboardingDateTime = DateTime.UtcNow;
        FleetAnalyticsCollector.EnsureVariable(manager, "ExecutionMode", OpcUa.DataTypes.Int32, 0);
        OvenEdgeManagerBehavior.PrepareConnectionAlarm(manager);
        managersFolder.Add(manager);
        Log.Info(LogNode, $"Created OvenEdgeManager '{managerName}' under '{OvenEdgeManagerBehavior.ManagersFolderName}'.");
    }

    private IUANode GetOrCreateModelFolder(string folderName)
    {
        var modelRoot = Project.Current.Get("Model");
        if (modelRoot == null)
        {
            Log.Error(LogNode, $"'Model' category folder not found, cannot provision '{folderName}'.");
            return null;
        }

        var folder = modelRoot.Get(folderName);
        if (folder != null)
            return folder;

        folder = InformationModel.Make<Folder>(folderName);
        modelRoot.Add(folder);
        Log.Warning(LogNode, $"Folder '{folderName}' was missing under 'Model', created it.");
        return folder;
    }

    // Local broker is embedded in this same project, so missing pieces are recreated with known-good
    // local defaults (anonymous auth, fixed port) rather than treated as an unrecoverable error.
    private MQTTClient GetOrCreateMqttSupervisorClient()
    {
        var mqttRoot = Project.Current.Get("MQTT");
        if (mqttRoot == null)
        {
            Log.Error(LogNode, "'MQTT' category folder not found, cannot provision the local broker/client.");
            return null;
        }

        var broker = GetOrCreateLocalBroker(mqttRoot);

        var client = mqttRoot.Get(MqttSupervisorClientName) as MQTTClient;
        if (client != null)
            return client;

        client = InformationModel.Make<MQTTClient>(MqttSupervisorClientName);
        OvenMqttClientHelper.ConfigureLocalClient(client, MqttSupervisorClientName, broker.ListenerPort);
        mqttRoot.Add(client);
        Log.Warning(LogNode, $"MQTTClient '{MqttSupervisorClientName}' was missing under 'MQTT', created it pointing at the local broker (localhost:{broker.ListenerPort}).");
        return client;
    }

    private MQTTBroker GetOrCreateLocalBroker(IUANode mqttRoot)
    {
        var broker = mqttRoot.Get(MqttBrokerName) as MQTTBroker;
        if (broker != null)
            return broker;

        broker = InformationModel.Make<MQTTBroker>(MqttBrokerName);
        mqttRoot.Add(broker);
        broker.ListenerPort = DefaultLocalBrokerPort;
        broker.AnonymousAuthentication = true;
        Log.Warning(LogNode, $"MQTTBroker '{MqttBrokerName}' was missing under 'MQTT', created it (local, anonymous auth, port {DefaultLocalBrokerPort}).");
        return broker;
    }

    // Sends the accept/discard outcome back to the edge device on the static "OnboardingResult" topic
    // (matches the edge project's OnboardingResultSubscriber -> Model/Onboarding/Result/OnboardingStatus).
    private void PublishOnboardingResult(string sender, OnboardingStatus status)
    {
        var client = GetOrCreateMqttSupervisorClient();
        if (client == null)
        {
            Log.Error(LogNode, $"Cannot publish onboarding result for '{sender}': MQTTClient not available.");
            return;
        }

        var configJson = JsonSerializer.Serialize(new OnboardingResultConfig());
        var payloadJson = JsonSerializer.Serialize(new OnboardingResultPayload(status));
        client.Publish(configJson, payloadJson);
        Log.Info(LogNode, $"Published onboarding result for '{sender}': OnboardingStatus={status} on topic '{OnboardingResultTopic}'.");

        RemovePendingRequest(sender);
    }

    private void RemovePendingRequest(string sender)
    {
        var pendingRequests = Project.Current.Get("Model/Onboarding/PendingRequests");
        if (pendingRequests == null)
        {
            Log.Warning(LogNode, "Folder 'Model/Onboarding/PendingRequests' not found, cannot remove processed request.");
            return;
        }

        var request = pendingRequests.Get(sender);
        if (request == null)
        {
            Log.Warning(LogNode, $"Pending request '{sender}' not found under PendingRequests, nothing to remove.");
            return;
        }

        request.Delete();
        OnboardingLogic.UpdatePendingCounters();
        Log.Info(LogNode, $"Removed processed onboarding request '{sender}' from PendingRequests.");
    }

    private enum OnboardingStatus : byte
    {
        NoOnboarding = 0,
        Pending = 1,
        Onboarded = 2,
        Declined = 3,
        Timeout = 4
    }

    // Matches the JSON config expected by MQTTClient.Publish's first argument (Topic/QoS/Retain).
    private sealed class OnboardingResultConfig
    {
        public string Topic { get; } = OnboardingResultTopic;
        public int QoS { get; } = 0;
        public bool Retain { get; } = false;
    }

    // Matches FT Optix's Default JSON envelope (Timestamp + Records[TagName/Value]) so the subscriber
    // deserializes it automatically instead of requiring a flat, folder-shaped payload.
    private sealed class OnboardingResultPayload
    {
        public string Timestamp { get; } = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");
        public Record[] Records { get; }

        public OnboardingResultPayload(OnboardingStatus status)
        {
            Records = new[] { new Record("OnboardingStatus", (byte)status) };
        }

        public sealed class Record
        {
            public string TagName { get; }
            public object Value { get; }

            public Record(string tagName, object value)
            {
                TagName = tagName;
                Value = value;
            }
        }
    }
}
