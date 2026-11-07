#region Using directives
using System;
using System.Text.Json;
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

public class DeviceRegistryRowLogic : BaseNetLogic
{
    private const string LogNode = "DeviceRegistryRowLogic";
    private const string MqttSupervisorClientName = "MQTTSupervisorClient";
    private const string OnboardingResultTopic = "OnboardingResult";
    private const string RowItemAliasName = "RowItem";

    public override void Start()
    {
        WatchChatSelection();
    }

    public override void Stop()
    {
        if (selection != null) selection.VariableChange -= OnChatStateChanged;
        if (unread != null) unread.VariableChange -= OnChatStateChanged;
    }

    // The Chat page's oven list reuses this row logic: while its oven is the selected one the conversation is on
    // screen, so its unread counter is kept at zero. Registry rows have no selection alias and do nothing.
    private void WatchChatSelection()
    {
        var manager = Owner.GetAlias(RowItemAliasName);
        selection = FindAncestorVariable(Owner, ChatSelectionAliasName);
        unread = manager?.GetVariable("Chat/UnreadCount");
        if (selection == null || unread == null)
            return;

        selection.VariableChange += OnChatStateChanged;
        unread.VariableChange += OnChatStateChanged;
        MarkReadIfSelected();
    }

    private void OnChatStateChanged(object sender, VariableChangeEventArgs e) => MarkReadIfSelected();

    private void MarkReadIfSelected()
    {
        var manager = Owner.GetAlias(RowItemAliasName);
        if (manager != null && selection.Value.Value is NodeId selected && selected.Equals(manager.NodeId))
            ChatLogic.MarkRead(manager);
    }

    private static IUAVariable FindAncestorVariable(IUANode from, string name)
    {
        for (var node = from; node != null; node = node.Owner)
            if (node.GetVariable(name) is IUAVariable found)
                return found;
        return null;
    }

    private IUAVariable selection;
    private IUAVariable unread;
    private const string ChatSelectionAliasName = "SelectedFleetElementAlias";

    // Kicks the device out of the fleet: tells the edge device it's no longer onboarded, then removes
    // its MQTT client, its OPC UA client and its OvenType instance from Fleet.
    [ExportMethod]
    public void RemoveDevice()
    {
        var rowItem = Owner.GetAlias(RowItemAliasName);
        if (rowItem == null)
        {
            Log.Error(LogNode, $"Alias '{RowItemAliasName}' not resolved under '{Owner.BrowseName}'.");
            return;
        }

        // Matched by name, not by cast: runtime-restored nodes may not pass an "is OvenEdgeManager" check.
        // The name (prefix + DeviceId), not telemetry's DeviceId variable: the latter gets overwritten by the
        // device's own MQTT telemetry and can end up blank, while the name is the immutable key used for the
        // MQTT topic and subscription name.
        var manager = rowItem;
        if (!manager.BrowseName.StartsWith(OvenEdgeManagerBehavior.NamePrefix, StringComparison.Ordinal))
        {
            Log.Error(LogNode, $"'{RowItemAliasName}' ('{manager.BrowseName}') is not an OvenEdgeManager, cannot remove device.");
            return;
        }

        var deviceId = OvenEdgeManagerBehavior.GetDeviceId(manager.BrowseName);
        if (LocalFleetRuntime.IsLocalManager(manager))
        {
            LocalFleetRuntime.Current?.StopCycle(deviceId);
            Log.Warning(LogNode, "Local ovens are removed from Model/Fleet at design time, not through remote offboarding.");
            return;
        }
        var telemetry = FindTelemetry(manager, deviceId);
        PublishOnboardingResult(deviceId, OnboardingStatus.Declined);
        OvenMqttClientHelper.RemoveOvenClient(deviceId);
        ChatLogic.DeleteConversation(deviceId);
        OpcUaClientRuntimeHelper.RemoveOpcUaClient(deviceId);

        manager.Delete();
        telemetry?.Delete();
        ChatLogic.RefreshUnreadTotal();
        FleetOverviewCollector.Record("offboarded", deviceId);
        Log.Info(LogNode, $"Removed device '{deviceId}' from Fleet.");
    }

    private static IUANode FindTelemetry(IUANode manager, string deviceId)
    {
        var pointer = manager.GetVariable("TelemetryDataPointer")?.Value.Value as NodeId;
        if (pointer != null && !pointer.IsEmpty)
        {
            var linked = InformationModel.Get(pointer);
            if (linked != null)
                return linked;
        }

        return Project.Current.Get(OvenEdgeManagerBehavior.TelemetryFolderPath)?.Get(deviceId);
    }

    private void PublishOnboardingResult(string deviceId, OnboardingStatus status)
    {
        var client = Project.Current.Get("MQTT")?.Get(MqttSupervisorClientName) as MQTTClient;
        if (client == null)
        {
            Log.Error(LogNode, $"MQTTClient '{MqttSupervisorClientName}' not found, cannot notify '{deviceId}'.");
            return;
        }

        var configJson = JsonSerializer.Serialize(new OnboardingResultConfig());
        var payloadJson = JsonSerializer.Serialize(new OnboardingResultPayload(status));
        client.Publish(configJson, payloadJson);
        Log.Info(LogNode, $"Published onboarding result for '{deviceId}': OnboardingStatus={status} on topic '{OnboardingResultTopic}'.");
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
