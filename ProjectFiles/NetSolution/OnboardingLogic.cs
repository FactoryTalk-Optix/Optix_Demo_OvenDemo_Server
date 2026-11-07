#region Using directives
using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.CoreBase;
using FTOptix.NativeUI;
using FTOptix.Retentivity;
using FTOptix.Core;
using FTOptix.NetLogic;
using FTOptix.MQTTBroker;
using FTOptix.MQTTClient;
using Optix_Demo_OvenDemo_Server;
using FTOptix.OPCUAServer;
using FTOptix.Alarm;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.EventLogger;
using FTOptix.OPCUAClient;
#endregion

public class OnboardingLogic : BaseNetLogic
{
    private const string PayloadVariableName = "OnboardingRequestPayload";
    private const string PendingRequestsFolderName = "PendingRequests";
    private const string MqttSupervisorClientName = "MQTTSupervisorClient";
    private const string OnboardingResultTopic = "OnboardingResult";
    private const string LogNode = "OnboardingLogic";

    // The embedded broker starts after the client has already tried to subscribe, so subscriptions stay inactive until a restart.
    private const int MqttStartupRestartDelayMs = 2000;
    private DelayedTask mqttRestartTask;

    public override void Start()
    {
        UpdatePendingCounters();
        mqttRestartTask = new DelayedTask(_ => RestartMqttClientAfterStartup(), MqttStartupRestartDelayMs, LogicObject);
        mqttRestartTask.Start();
    }

    // Drives the pending-requests badge (PendingRequestAvailable / PendingRequestCount); call after every change of PendingRequests.
    internal static void UpdatePendingCounters()
    {
        var onboarding = Project.Current.Get("Model/Onboarding");
        var count = onboarding?.Get(PendingRequestsFolderName)?.Children.Count() ?? 0;
        var countVariable = onboarding?.GetVariable("PendingRequestCount");
        var availableVariable = onboarding?.GetVariable("PendingRequestAvailable");
        if (countVariable == null || availableVariable == null)
        {
            Log.Warning(LogNode, "Model/Onboarding/PendingRequestCount or PendingRequestAvailable not found.");
            return;
        }
        countVariable.Value = count;
        availableVariable.Value = count > 0;
    }

    public override void Stop()
    {
        mqttRestartTask?.Dispose();
    }

    private void RestartMqttClientAfterStartup()
    {
        try
        {
            var client = Project.Current.Get("MQTT")?.Get(MqttSupervisorClientName) as MQTTClient;
            if (client == null)
                return;

            client.Stop();
            client.Start();
            Log.Info(LogNode, $"MQTTClient '{MqttSupervisorClientName}' restarted after startup to activate subscriptions.");

            OvenMqttClientHelper.ReconcileAll();
        }
        catch (Exception ex)
        {
            Log.Error(LogNode, $"Failed to restart MQTTClient '{MqttSupervisorClientName}' after startup: {ex.Message}");
        }
    }

    // Invoked by the OnboardRequestPayloadChanged event whenever OnboardingRequestPayload changes.
    [ExportMethod]
    public void PendingRequestHandler()
    {
        Log.Info(LogNode, "Onboarding request received.");

        var payloadVariable = Owner.GetVariable(PayloadVariableName);
        if (payloadVariable == null)
        {
            Log.Error(LogNode, $"Variable '{PayloadVariableName}' not found under '{Owner.BrowseName}'.");
            return;
        }

        var json = (string)payloadVariable.Value;
        if (string.IsNullOrWhiteSpace(json))
        {
            Log.Warning(LogNode, "Onboarding request payload is empty, ignoring.");
            return;
        }

        OnboardingRequestPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<OnboardingRequestPayload>(json);
        }
        catch (JsonException ex)
        {
            Log.Error(LogNode, $"Failed to parse onboarding request JSON payload: {ex.Message}");
            return;
        }

        if (payload == null)
        {
            Log.Error(LogNode, "Onboarding request JSON payload parsed to an empty object.");
            return;
        }

        if (string.IsNullOrEmpty(payload.Sender) ||
            string.IsNullOrEmpty(payload.Content) ||
            string.IsNullOrEmpty(payload.Timestamp))
        {
            Log.Warning(LogNode, $"Onboarding request validation failed: Sender, Content and Timestamp are all required (Sender='{payload.Sender}').");
            return;
        }

        if (!DateTime.TryParse(payload.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
        {
            Log.Warning(LogNode, $"Onboarding request validation failed: Timestamp '{payload.Timestamp}' is not a valid date/time (Sender='{payload.Sender}').");
            return;
        }

        var pendingRequestsFolder = Owner.GetObject(PendingRequestsFolderName);
        if (pendingRequestsFolder == null)
        {
            Log.Error(LogNode, $"Folder '{PendingRequestsFolderName}' not found under '{Owner.BrowseName}'.");
            return;
        }

        var browseName = SanitizeBrowseName(payload.Sender);
        var existingRequest = pendingRequestsFolder.Get<OnboardingRequestData>(browseName);

        if (existingRequest != null)
        {
            existingRequest.Sender = payload.Sender;
            existingRequest.Content = payload.Content;
            existingRequest.Timestamp = timestamp;
            Log.Info(LogNode, $"Pending onboarding request updated for sender '{payload.Sender}'.");
        }
        else
        {
            var newRequest = InformationModel.MakeObject<OnboardingRequestData>(browseName);
            newRequest.Sender = payload.Sender;
            newRequest.Content = payload.Content;
            newRequest.Timestamp = timestamp;
            pendingRequestsFolder.Add(newRequest);
            Log.Info(LogNode, $"Pending onboarding request created for sender '{payload.Sender}'.");
        }

        UpdatePendingCounters();
        FleetOverviewCollector.Record("onboardRequested", payload.Sender);
        PublishOnboardingResult(payload.Sender, OnboardingStatus.Pending);
    }

    // Lets the edge device know its request was received and is awaiting a decision.
    private void PublishOnboardingResult(string sender, OnboardingStatus status)
    {
        var client = Project.Current.Get("MQTT")?.Get(MqttSupervisorClientName) as MQTTClient;
        if (client == null)
        {
            Log.Error(LogNode, $"MQTTClient '{MqttSupervisorClientName}' not found, cannot notify '{sender}'.");
            return;
        }

        var configJson = JsonSerializer.Serialize(new OnboardingResultConfig());
        var payloadJson = JsonSerializer.Serialize(new OnboardingResultPayload(status));
        client.Publish(configJson, payloadJson);
        Log.Info(LogNode, $"Published onboarding result for '{sender}': OnboardingStatus={status} on topic '{OnboardingResultTopic}'.");
    }

    // OPC UA BrowseNames cannot safely contain arbitrary characters (e.g. '/'), so sanitize the SenderId.
    private static string SanitizeBrowseName(string senderId)
    {
        var chars = senderId.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-')
                chars[i] = '_';
        }
        return new string(chars);
    }

    // Field names must match the JSON keys published by the edge project's Payload template.
    private sealed class OnboardingRequestPayload
    {
        public string Sender { get; set; }
        public string Content { get; set; }
        public string Timestamp { get; set; }
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

    // Matches FT Optix's Default JSON envelope (Timestamp + Records[TagName/Value]).
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

/// <summary>
/// Manages one MQTT client per onboarded oven (<c>Sup_&lt;DeviceId&gt;</c>): a new oven restarts only its own client
/// and removing an oven is a single <c>Delete</c>.
/// </summary>
/// <remarks>
/// Channels are the subscriptions of the client's only subscriber: the telemetry (named after the DeviceId, writing
/// into <c>Model/Fleet</c>) and <c>Chat</c> (into <c>Model/Chat/Inbox</c>). The clients are not retentive: they are
/// rebuilt from the managers at every startup by <see cref="ReconcileAll"/>. The embedded broker accepts at most 25
/// connections, one of which is the onboarding client.
/// </remarks>
internal static class OvenMqttClientHelper
{
    private const string LogNode = "OvenMqttClientHelper";
    private const string ClientNamePrefix = "Sup_";
    private const string SubscriberName = "Subscribers";
    private const string BrokerName = "MQTTBroker";
    private const string OnboardingClientName = "MQTTSupervisorClient";
    private const string FleetFolderPath = "Model/Fleet";
    private const string ChatInboxPath = "Model/Chat/Inbox";
    private const string ChatSubscriptionName = "Chat";
    private const int ReconnectPollIntervalMs = 200;
    private const int ReconnectMaxAttempts = 15; // ~3 s

    /// <summary>Name of the MQTT client of an oven.</summary>
    public static string GetClientName(string deviceId) => ClientNamePrefix + deviceId;

    /// <summary>
    /// The DeviceId is part of MQTT topics: '/', '+', '#' and anything outside letters, digits, '_' and '-'
    /// would break them.
    /// </summary>
    public static bool IsValidDeviceId(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return false;

        foreach (var c in deviceId)
        {
            var valid = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';
            if (!valid)
                return false;
        }
        return true;
    }

    /// <summary>Finds the MQTT client of an oven; null if it does not exist.</summary>
    public static MQTTClient Find(string deviceId)
    {
        return Project.Current.Get("MQTT")?.Get(GetClientName(deviceId)) as MQTTClient;
    }

    /// <summary>
    /// Sets the connection to the local embedded broker without TLS. A new <c>MQTTClient</c> defaults to TLS and is
    /// validated as soon as it is added to the model, so this must be called <b>before</b> <c>Add</c>.
    /// </summary>
    public static void ConfigureLocalClient(MQTTClient client, string clientId, ushort brokerPort)
    {
        client.BrokerAddress = "localhost";
        client.BrokerPort = brokerPort;
        client.ClientId = clientId;
        client.SSLTLSEnabled = false;
        client.ValidateBrokerCertificate = false;
    }

    /// <summary>
    /// Creates or repairs the client of an oven together with its subscriptions.
    /// </summary>
    /// <param name="deviceId">The oven's DeviceId.</param>
    /// <param name="changed">True when the client was created or modified and must be (re)started with <see cref="Restart"/>.</param>
    /// <returns>The client, or null if the broker or <c>Model/Fleet</c> is missing.</returns>
    public static MQTTClient EnsureOvenClient(string deviceId, out bool changed)
    {
        changed = false;
        var mqttRoot = Project.Current.Get("MQTT");
        var broker = mqttRoot?.Get(BrokerName) as MQTTBroker;
        var fleet = Project.Current.Get(FleetFolderPath);
        if (broker == null || fleet == null)
        {
            Log.Error(LogNode, $"'MQTT/{BrokerName}' or '{FleetFolderPath}' not found, cannot create the client of '{deviceId}'.");
            return null;
        }

        var name = GetClientName(deviceId);
        var client = mqttRoot.Get(name) as MQTTClient;
        if (client == null)
        {
            client = InformationModel.Make<MQTTClient>(name);
            ConfigureLocalClient(client, name, broker.ListenerPort);
            var newSubscriber = InformationModel.Make<MQTTSubscriber>(SubscriberName);
            client.Add(newSubscriber);
            EnsureTelemetrySubscription(newSubscriber, deviceId, fleet);
            EnsureChatSubscription(newSubscriber, deviceId);
            mqttRoot.Add(client);
            changed = true;
            Log.Info(LogNode, $"Created MQTT client '{name}' (localhost:{broker.ListenerPort}).");
            return client;
        }

        if (client.BrokerAddress != "localhost" || client.BrokerPort != broker.ListenerPort || client.ClientId != name || client.SSLTLSEnabled || client.ValidateBrokerCertificate)
        {
            ConfigureLocalClient(client, name, broker.ListenerPort);
            Log.Warning(LogNode, $"MQTT client '{name}' had stale connection settings, repaired them.");
            changed = true;
        }

        var subscriber = client.Get(SubscriberName) as MQTTSubscriber;
        if (subscriber == null)
        {
            subscriber = InformationModel.Make<MQTTSubscriber>(SubscriberName);
            client.Add(subscriber);
            changed = true;
        }

        changed |= EnsureTelemetrySubscription(subscriber, deviceId, fleet);
        changed |= EnsureChatSubscription(subscriber, deviceId);
        return client;
    }

    /// <summary>
    /// Creates or repairs the telemetry subscription. Its Folder must be the Fleet container: the Default JSON
    /// subscription writes into the child of Folder whose BrowseName equals the subscription's own name, so the name
    /// must stay the DeviceId.
    /// </summary>
    private static bool EnsureTelemetrySubscription(MQTTSubscriber subscriber, string deviceId, IUANode fleet)
    {
        var existing = subscriber.Get("Subscriptions")?.Get(deviceId) as Subscription;
        if (existing == null)
        {
            var subscription = InformationModel.Make<Subscription>(deviceId);
            subscriber.Subscriptions.Add(subscription);
            subscription.Topic = deviceId;
            subscription.QoS = QoSLevel.AtMostOnce;
            subscription.Folder = fleet.NodeId;
            return true;
        }

        var changed = false;
        if (existing.Folder != fleet.NodeId)
        {
            existing.Folder = fleet.NodeId;
            changed = true;
        }
        if (existing.Topic != deviceId)
        {
            existing.Topic = deviceId;
            changed = true;
        }
        return changed;
    }

    /// <summary>Creates or repairs the <c>Chat</c> subscription (QoS 1, into <c>Model/Chat/Inbox</c>).</summary>
    private static bool EnsureChatSubscription(MQTTSubscriber subscriber, string deviceId)
    {
        var inbox = Project.Current.GetVariable(ChatInboxPath);
        if (inbox == null)
        {
            Log.Warning(LogNode, $"'{ChatInboxPath}' not found, chat subscription of '{deviceId}' skipped.");
            return false;
        }

        var topic = $"chat/{deviceId}/to-supervisor";
        var existing = subscriber.Get("Subscriptions")?.Get(ChatSubscriptionName) as SubscriptionPlainText;
        if (existing == null)
        {
            var subscription = InformationModel.Make<SubscriptionPlainText>(ChatSubscriptionName);
            subscriber.Subscriptions.Add(subscription);
            subscription.Topic = topic;
            subscription.QoS = QoSLevel.AtLeastOnce;
            subscription.Variable = inbox.NodeId;
            return true;
        }

        var changed = false;
        if (existing.Topic != topic)
        {
            existing.Topic = topic;
            changed = true;
        }
        if (existing.QoS != QoSLevel.AtLeastOnce)
        {
            existing.QoS = QoSLevel.AtLeastOnce;
            changed = true;
        }
        if (existing.Variable != inbox.NodeId)
        {
            existing.Variable = inbox.NodeId;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Restarts a client and waits up to about 3 s for the connection. New or changed subscriptions are only picked
    /// up after a restart, which reconnects asynchronously.
    /// </summary>
    public static void Restart(MQTTClient client)
    {
        client.Stop();
        client.Start();

        for (var attempt = 0; attempt < ReconnectMaxAttempts; attempt++)
        {
            if (client.Status == Status.Connected)
            {
                Log.Info(LogNode, $"MQTT client '{client.BrowseName}' connected after {attempt * ReconnectPollIntervalMs} ms.");
                return;
            }
            Thread.Sleep(ReconnectPollIntervalMs);
        }

        Log.Warning(LogNode, $"MQTT client '{client.BrowseName}' not connected within {ReconnectMaxAttempts * ReconnectPollIntervalMs} ms (Status={client.Status}).");
    }

    /// <summary>Stops and deletes the client of an oven.</summary>
    public static void RemoveOvenClient(string deviceId)
    {
        var client = Find(deviceId);
        if (client == null)
        {
            Log.Warning(LogNode, $"MQTT client '{GetClientName(deviceId)}' not found, nothing to remove.");
            return;
        }

        client.Stop();
        client.Delete();
        Log.Info(LogNode, $"Removed MQTT client '{GetClientName(deviceId)}'.");
    }

    /// <summary>
    /// Rebuilds the client of every oven that has a manager (managers are retentive, clients are not) and restarts the
    /// ones that were created or changed. Meant to run once the broker is up after a startup.
    /// </summary>
    public static void ReconcileAll()
    {
        var managers = Project.Current.Get($"Model/{OvenEdgeManagerBehavior.ManagersFolderName}");
        if (managers == null)
            return;

        foreach (var manager in managers.Children)
        {
            if (!manager.BrowseName.StartsWith(OvenEdgeManagerBehavior.NamePrefix, StringComparison.Ordinal))
                continue;

            var deviceId = OvenEdgeManagerBehavior.GetDeviceId(manager.BrowseName);
            if (LocalFleetRuntime.IsLocalManager(manager) || !LocalFleetRuntime.HasRemoteRegistration(deviceId, manager))
                continue;
            if (!IsValidDeviceId(deviceId))
            {
                Log.Warning(LogNode, $"DeviceId '{deviceId}' has characters not allowed in MQTT topics, client not created.");
                continue;
            }

            RemoveLegacySubscription(deviceId);
            var client = EnsureOvenClient(deviceId, out var changed);
            if (client != null && changed)
                Restart(client);
        }
    }

    /// <summary>
    /// Before the per-oven clients, the telemetry subscription lived on the onboarding client; if it were left there
    /// the telemetry would be written twice.
    /// </summary>
    private static void RemoveLegacySubscription(string deviceId)
    {
        var onboardingClient = Project.Current.Get("MQTT")?.Get(OnboardingClientName);
        foreach (var subscriberName in new[] { SubscriberName, "MQTTSubscriber" })
        {
            var legacy = onboardingClient?.Get(subscriberName)?.Get("Subscriptions")?.Get(deviceId);
            if (legacy == null)
                continue;

            legacy.Delete();
            Log.Info(LogNode, $"Removed legacy subscription '{deviceId}' from '{OnboardingClientName}'.");
        }
    }
}
