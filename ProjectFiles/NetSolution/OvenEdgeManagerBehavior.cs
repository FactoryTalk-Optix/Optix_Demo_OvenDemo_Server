#region Using directives
using System;
using System.Linq;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.CoreBase;
using FTOptix.NativeUI;
using FTOptix.NetLogic;
using FTOptix.WebUI;
using FTOptix.RecipeX;
using FTOptix.EventLogger;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.OPCUAServer;
using FTOptix.MQTTBroker;
using FTOptix.MQTTClient;
using FTOptix.Retentivity;
using FTOptix.OPCUAClient;
using FTOptix.Alarm;
using FTOptix.Core;
#endregion

[CustomBehavior]
public class OvenEdgeManagerBehavior : BaseNetBehavior
{
    private const string LogNode = "OvenEdgeManagerBehavior";
    private const int TickPeriodMs = 1000;

    public const string NamePrefix = "OvenEdgeManager_";
    public const string ManagersFolderName = "OvenEdgeManagers";
    public const string TelemetryFolderPath = "Model/Fleet";

    // Manager and telemetry share the DeviceId: manager = prefix + id, telemetry = id.
    public static string GetManagerName(string deviceId) => NamePrefix + deviceId;

    public static string GetDeviceId(string managerName) =>
        managerName.StartsWith(NamePrefix, StringComparison.Ordinal) ? managerName.Substring(NamePrefix.Length) : managerName;

    internal static void PrepareConnectionAlarm(IUANode manager)
    {
        var id = GetDeviceId(manager.BrowseName);
        SetPlainAlarmMessage(manager, "ConnectionError", $"Oven {id} is disconnected", true);
        // The local simulation writes its own fault text, so ProcessError is only filled while it still carries the type's formatter.
        SetPlainAlarmMessage(manager, "ProcessError", $"Oven {id}: process fault", false);
        var input = manager.GetVariable("ConnectionError/InputValue");
        if (input != null)
        {
            var driven = input.Children.Where(child => child is Converter || child is DynamicLink).ToList();
            foreach (var child in driven) child.Delete();
            // Legacy managers still carry the evaluator on Connection, which has already pushed a transient 1 into the input.
            if (driven.Count > 0) input.Value = 0.0;
        }
    }

    // The type's StringFormatter resolves the session language; read by the retentivity thread of a new instance it dereferences a null session and kills the runtime.
    private static void SetPlainAlarmMessage(IUANode manager, string alarm, string text, bool always)
    {
        var message = manager.GetVariable(alarm + "/Message");
        if (message == null) return;
        var driven = message.Children.Where(child => child is Converter || child is DynamicLink).ToList();
        foreach (var child in driven) child.Delete();
        if (always || driven.Count > 0)
            message.Value = new UAValue(new LocalizedText(text, "en-US"));
    }

    public override void Start()
    {
        PrepareConnectionAlarm(Node);
        startedTick = Environment.TickCount64;
        wasLocal = LocalFleetRuntime.IsLocalManager(Node);
        connectionAlarmActive = FleetAnalyticsCollector.EnsureVariable(Node, "ConnectionAlarmActive", OpcUa.DataTypes.Boolean);
        connectionAlarmActive.Value = false;
        connectionAlarmInput = Node.GetVariable("ConnectionError/InputValue");
        // The first read is only a baseline: a retained, stale Timestamp must not count as a live edge.
        baseline = ReadTimestamp(ResolveTelemetry());
        lastChangeTick = null;
        SetConnected(false, "starting");

        Log.Info(LogNode, $"'{Node.BrowseName}' started, connection timeout {Node.ConnectionTimeout} ms.");
        task = new PeriodicTask(Tick, TickPeriodMs, Node);
        task.Start();
    }

    public override void Stop()
    {
        task?.Dispose();
        task = null;
    }

    private void Tick()
    {
        try
        {
            var telemetry = ResolveTelemetry();
            if (telemetry == null)
            {
                SetConnected(false, "telemetry node not found");
                return;
            }

            var timestamp = ReadTimestamp(telemetry);
            var local = LocalFleetRuntime.IsLocalManager(Node);
            if (local != wasLocal)
            {
                wasLocal = local;
                baseline = timestamp;
                lastChangeTick = null;
                startedTick = Environment.TickCount64;
                SetConnected(false, "execution mode changed; waiting for fresh telemetry");
                return;
            }
            if (timestamp == null)
            {
                SetConnected(false, "telemetry has no readable Timestamp");
                return;
            }

            // Only "did it change" matters; the edge clock is never compared with ours.
            if (baseline == null)
            {
                baseline = timestamp;
            }
            else if (timestamp != baseline)
            {
                baseline = timestamp;
                lastChangeTick = Environment.TickCount64;
            }

            if (lastChangeTick == null)
                SetConnected(false, "Timestamp has not changed since start");
            else if (Environment.TickCount64 - lastChangeTick.Value > Node.ConnectionTimeout)
                SetConnected(false, "Timestamp not updated within the timeout");
            else
                SetConnected(true, null);
        }
        catch (Exception ex)
        {
            Log.Warning(LogNode, $"'{Node.BrowseName}' tick failed: {ex.Message}");
        }
    }

    // Untyped on purpose: runtime-restored nodes may not pass an "is OvenType" check.
    private IUANode ResolveTelemetry()
    {
        var pointer = Node.TelemetryDataPointer;
        if (pointer != null && !pointer.IsEmpty)
        {
            var linked = InformationModel.Get(pointer);
            if (linked != null)
                return linked;
        }

        // Pointer empty or stale (e.g. after a restart): re-link by the shared DeviceId.
        var byName = Project.Current.Get(TelemetryFolderPath)?.Get(GetDeviceId(Node.BrowseName));
        if (byName != null)
            Node.TelemetryDataPointer = byName.NodeId;
        return byName;
    }

    private static DateTime? ReadTimestamp(IUANode telemetry)
    {
        return telemetry?.GetVariable("Timestamp")?.Value.Value is DateTime timestamp ? timestamp : (DateTime?)null;
    }

    // Logs every transition, plus a new reason while staying disconnected.
    private void SetConnected(bool connected, string reason)
    {
        var changed = Node.Connection != connected;
        if (changed)
            Node.Connection = connected;

        if (changed || (!connected && reason != lastReason))
            Log.Info(LogNode, $"'{Node.BrowseName}' is {(connected ? "connected" : "disconnected")}{(reason == null ? "" : $" ({reason})")}.");
        lastReason = reason;

        var graceElapsed = Environment.TickCount64 - startedTick >= Node.ConnectionTimeout;
        var alarmActive = !connected && graceElapsed;
        connectionAlarmActive.Value = alarmActive;
        // During the startup grace the retained alarm state is left alone, otherwise every restart logs an inactive and an active event.
        if (connectionAlarmInput != null && (connected || graceElapsed)) connectionAlarmInput.Value = alarmActive ? 1.0 : 0.0;
        if (!LocalFleetRuntime.IsLocalManager(Node))
            OpcUaClientRuntimeHelper.SetClientEnabled(GetDeviceId(Node.BrowseName), connected);
    }

    private PeriodicTask task;
    private long startedTick;
    private bool wasLocal;
    private IUAVariable connectionAlarmActive;
    private IUAVariable connectionAlarmInput;
    private DateTime? baseline;
    private long? lastChangeTick;
    private string lastReason;
#region Auto-generated code, do not edit!
    protected new OvenEdgeManager Node => (OvenEdgeManager)base.Node;
#endregion
}
