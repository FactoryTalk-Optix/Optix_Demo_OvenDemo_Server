#region Using directives
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.CoreBase;
using FTOptix.HMIProject;
using FTOptix.Store;
using FTOptix.UI;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FTOptix.OPCUAClient;
using FTOptix.MQTTClient;
using FTOptix.RecipeX;
#endregion

public class ProjectLogic : BaseNetLogic
{
    public override void Start()
    {
        GetNetworkInterfaces();
        localFleet = new LocalFleetRuntime();
        localFleetTask = new PeriodicTask(localFleet.Tick, 250, LogicObject);
        localFleetTask.Start();
        details = new OvenDetailsRuntime();
        detailsTask = new PeriodicTask(details.Tick, 1000, LogicObject);
        detailsTask.Start();
        analytics = new FleetAnalyticsCollector();
        analyticsTask = new PeriodicTask(analytics.Tick, 1000, LogicObject);
        analyticsTask.Start();
        overview = new FleetOverviewCollector();
        overviewTask = new PeriodicTask(overview.Tick, 1000, LogicObject);
        overviewTask.Start();
    }

    public override void Stop()
    {
        overviewTask?.Dispose();
        overviewTask = null;
        overview = null;
        detailsTask?.Dispose();
        detailsTask = null;
        details = null;
        localFleetTask?.Dispose();
        localFleetTask = null;
        localFleet?.Dispose();
        localFleet = null;
        analyticsTask?.Dispose();
        analyticsTask = null;
        analytics?.Flush();
    }

    private void GetNetworkInterfaces()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    Log.Info($"{nic.Name} -> {ua.Address}");
            }
        }
    }
    private PeriodicTask analyticsTask;
    private FleetAnalyticsCollector analytics;
    private PeriodicTask localFleetTask;
    private LocalFleetRuntime localFleet;
    private PeriodicTask detailsTask;
    private PeriodicTask overviewTask;
    private FleetOverviewCollector overview;
    private OvenDetailsRuntime details;

    [ExportMethod]
    public void ApplyLocalRecipe(string deviceId, string name, string version) => LocalFleetRuntime.Current?.Apply(deviceId, name, version);

    [ExportMethod]
    public void StartLocalCycle(string deviceId) => LocalFleetRuntime.Current?.StartCycle(deviceId);

    [ExportMethod]
    public void StopLocalCycle(string deviceId) => LocalFleetRuntime.Current?.StopCycle(deviceId);
}

internal sealed class LocalFleetRuntime : IDisposable
{
    internal static readonly object OwnershipSync = new object();
    private readonly object gate = OwnershipSync;
    private readonly Dictionary<string, LocalOvenController> ovens = new Dictionary<string, LocalOvenController>(StringComparer.Ordinal);
    private long nextDiscovery;
    private long nextCatalog;
    private long nextError;
    private bool disposed;

    internal static LocalFleetRuntime Current { get; private set; }
    internal IUANode RecipeChoices { get; private set; }

    public LocalFleetRuntime()
    {
        Current = this;
    }

    internal static IUANode Manager(string deviceId) => Project.Current.Get("Model/OvenEdgeManagers")?.Get(OvenEdgeManagerBehavior.GetManagerName(deviceId));

    internal static bool IsLocal(string deviceId) => IsLocalManager(Manager(deviceId));

    internal static bool IsLocalManager(IUANode manager) => manager?.GetVariable("ExecutionMode")?.Value.Value is int mode && mode == 1;

    internal static bool HasRemoteRegistration(string deviceId, IUANode manager)
    {
        if (manager?.GetVariable("ExecutionMode")?.Value.Value is int mode && (mode == 0 || mode == 1)) return mode == 0;
        if (OvenMqttClientHelper.Find(deviceId) != null) return true;
        if (manager?.GetVariable("OnboardingDateTime")?.Value.Value is DateTime date && date.Year >= 2000) return true;
        return !string.IsNullOrWhiteSpace(Convert.ToString(manager?.GetVariable("OnboardedBy")?.Value.Value));
    }

    public void Tick()
    {
        lock (gate)
        {
            if (disposed) return;
            try
            {
                if (Environment.TickCount64 >= nextDiscovery)
                {
                    nextDiscovery = Environment.TickCount64 + 1000;
                    try { Discover(); }
                    catch (Exception error) { Warn(error.Message); }
                }
                if (Environment.TickCount64 >= nextCatalog)
                {
                    nextCatalog = Environment.TickCount64 + 5000;
                    try { RefreshCatalog(); }
                    catch (Exception error) { Warn("Recipe catalog: " + error.Message); }
                }
                foreach (var oven in ovens.Values)
                {
                    try { oven.Tick(); }
                    catch (Exception error) { Warn(oven.DeviceId + ": " + error.Message); }
                }
            }
            catch (Exception error) { Warn(error.Message); }
        }
    }

    private void Discover()
    {
        var fleet = Project.Current.Get("Model/Fleet");
        if (fleet == null) return;
        var managers = FleetAnalyticsCollector.EnsureFolder(Project.Current.Get("Model"), "OvenEdgeManagers");
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var telemetry in fleet.Children.ToList())
        {
            var deviceId = telemetry.BrowseName;
            if (telemetry.GetVariable("Timestamp") == null || !OvenMqttClientHelper.IsValidDeviceId(deviceId)) continue;
            var manager = Manager(deviceId);
            if (HasRemoteRegistration(deviceId, manager))
            {
                if (manager != null) FleetAnalyticsCollector.EnsureVariable(manager, "ExecutionMode", OpcUa.DataTypes.Int32, 0).Value = 0;
                continue;
            }
            if (IsLocalManager(manager) && OvenMqttClientHelper.Find(deviceId) != null) OvenMqttClientHelper.RemoveOvenClient(deviceId);
            OpcUaClientRuntimeHelper.SetClientEnabled(deviceId, false);
            present.Add(deviceId);
            if (ovens.TryGetValue(deviceId, out var existing))
            {
                if (existing.TelemetryId == telemetry.NodeId && existing.ManagerId == manager?.NodeId) continue;
                existing.StopCycle();
                ovens.Remove(deviceId);
            }
            if (manager == null)
            {
                manager = InformationModel.MakeObject<OvenEdgeManager>(OvenEdgeManagerBehavior.GetManagerName(deviceId));
                manager.GetVariable("TelemetryDataPointer").Value = telemetry.NodeId;
                FleetAnalyticsCollector.EnsureVariable(manager, "ExecutionMode", OpcUa.DataTypes.Int32, 1);
                manager.GetVariable("IsMonitored").Value = true;
                OvenEdgeManagerBehavior.PrepareConnectionAlarm(manager);
                managers.Add(manager);
            }
            else FleetAnalyticsCollector.EnsureVariable(manager, "ExecutionMode", OpcUa.DataTypes.Int32, 1).Value = 1;
            manager.GetVariable("TelemetryDataPointer").Value = telemetry.NodeId;
            telemetry.GetVariable("DeviceId").Value = deviceId;
            if (manager.Get("LocalSimulation") == null) manager.GetVariable("IsMonitored").Value = true;
            ovens.Add(deviceId, new LocalOvenController(deviceId, telemetry, manager));
            Log.Info("LocalFleet", $"Local oven '{deviceId}' ready; network transports are not used.");
        }
        foreach (var deviceId in ovens.Keys.Where(deviceId => !present.Contains(deviceId)).ToList())
        {
            ovens[deviceId].StopCycle();
            ovens.Remove(deviceId);
            if (fleet.Get(deviceId) == null && IsLocal(deviceId)) Manager(deviceId)?.Delete();
        }
    }

    public void Apply(string deviceId, string name, string version)
    {
        lock (gate) { if (!disposed && ovens.TryGetValue(deviceId, out var oven)) oven.Apply(new RecipeId { Name = name, Version = version }); }
    }

    private void RefreshCatalog()
    {
        RecipeChoices ??= FleetAnalyticsCollector.EnsureFolder(Project.Current.Get("Model"), "LocalRecipeChoices");
        var schema = Project.Current.Get<RecipeSchema>("Recipes/RecipeSchema");
        if (schema == null) return;
        var result = schema.GetRecipes();
        if (result.ResultCode != GetRecipesResultCode.Success) return;
        var present = new HashSet<string>();
        foreach (var recipe in result.Recipes)
        {
            var id = recipe.RecipeId;
            var status = schema.GetRecipeMetadataValue(id, "Status");
            if (status.ResultCode != GetRecipeMetadataValueResultCode.Success || Convert.ToInt32(status.MetadataValue.Value) != 2) continue;
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id.Name + "\0" + id.Version));
            var key = "Recipe_" + Convert.ToHexString(hash);
            present.Add(key);
            var choice = RecipeChoices.Get(key);
            if (choice != null) continue;
            choice = InformationModel.MakeObject(key);
            FleetAnalyticsCollector.EnsureVariable(choice, "RecipeName", OpcUa.DataTypes.String, id.Name);
            FleetAnalyticsCollector.EnsureVariable(choice, "RecipeVersion", OpcUa.DataTypes.String, id.Version);
            FleetAnalyticsCollector.EnsureVariable(choice, "Caption", OpcUa.DataTypes.String, id.Name + " v" + id.Version);
            RecipeChoices.Add(choice);
        }
        foreach (var choice in RecipeChoices.Children.Where(choice => !present.Contains(choice.BrowseName)).ToList()) choice.Delete();
    }

    public void StartCycle(string deviceId)
    {
        lock (gate) { if (!disposed && ovens.TryGetValue(deviceId, out var oven)) oven.StartCycle(); }
    }

    public void StopCycle(string deviceId)
    {
        lock (gate) { if (!disposed && ovens.TryGetValue(deviceId, out var oven)) oven.StopCycle(); }
    }

    internal static void PrepareRemoteOwnership(string deviceId)
    {
        lock (OwnershipSync)
        {
            if (Current != null && Current.ovens.TryGetValue(deviceId, out var oven))
            {
                oven.StopCycle();
                Current.ovens.Remove(deviceId);
                Log.Info("LocalFleet", $"Local simulation of '{deviceId}' stopped for accepted remote onboarding.");
            }
            var manager = Manager(deviceId);
            if (manager == null) return;
            FleetAnalyticsCollector.EnsureVariable(manager, "ExecutionMode", OpcUa.DataTypes.Int32, 0).Value = 0;
            var fault = manager.GetVariable("ProcessFault");
            if (fault != null) fault.Value = false;
        }
    }

    private void Warn(string message)
    {
        if (Environment.TickCount64 < nextError) return;
        nextError = Environment.TickCount64 + 15000;
        Log.Warning("LocalFleet", message);
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            foreach (var oven in ovens.Values) oven.StopCycle();
            ovens.Clear();
            if (Current == this) Current = null;
        }
    }
}

internal sealed class LocalOvenController
{
    private readonly IUANode telemetry;
    private readonly IUANode manager;
    private readonly IUANode settings;
    private readonly IUANode target;
    private readonly LocalOvenSimulation simulation = new LocalOvenSimulation();
    private readonly DateTime started = DateTime.UtcNow;
    private readonly string epoch = Guid.NewGuid().ToString("N");
    private RecipeId pending;
    private RecipeId applied;
    private DateTime day = DateTime.UtcNow.Date;
    private double dayEnergy;
    private long dayCycles;
    private long nextCycle;
    private int ticks;
    private bool wasFaulted;

    public string DeviceId { get; }
    public NodeId TelemetryId => telemetry.NodeId;
    public NodeId ManagerId => manager.NodeId;

    public LocalOvenController(string deviceId, IUANode telemetry, IUANode manager)
    {
        DeviceId = deviceId;
        this.telemetry = telemetry;
        this.manager = manager;
        settings = FleetAnalyticsCollector.EnsureFolder(manager, "LocalSimulation");
        foreach (var name in new[] { "AutoCycle", "RejectNextCycle", "SimulateFault", "PauseTelemetry", "CycleRunning", "ApplyCommand", "StartCommand", "StopCommand" })
            FleetAnalyticsCollector.EnsureVariable(settings, name, OpcUa.DataTypes.Boolean).Value = false;
        FleetAnalyticsCollector.EnsureVariable(settings, "SelectedRecipe", OpcUa.DataTypes.NodeId);
        FleetAnalyticsCollector.EnsureVariable(settings, "RepeatDelaySeconds", OpcUa.DataTypes.Double, 5.0);
        FleetAnalyticsCollector.EnsureVariable(settings, "LastResult", OpcUa.DataTypes.String).Value = "Local - apply an approved recipe";
        FleetAnalyticsCollector.EnsureVariable(settings, "PendingRecipe", OpcUa.DataTypes.String).Value = string.Empty;
        target = settings.Get("RecipeTarget");
        if (target == null)
        {
            target = InformationModel.MakeObject("RecipeTarget", Optix_Demo_OvenDemo_Server.ObjectTypes.OvenRecipe);
            settings.Add(target);
        }
        FleetAnalyticsCollector.EnsureCounters(telemetry);
        FleetAnalyticsCollector.EnsureVariable(manager, "ProcessFault", OpcUa.DataTypes.Boolean).Value = false;
        var faultAlarm = manager.Get("ProcessError") as FTOptix.Alarm.DigitalAlarm;
        if (faultAlarm == null)
        {
            faultAlarm = InformationModel.MakeObject<FTOptix.Alarm.DigitalAlarm>("ProcessError");
            manager.Add(faultAlarm);
        }
        var faultMessage = $"Oven {DeviceId}: simulated process fault";
        faultAlarm.Message = faultMessage;
        faultAlarm.LocalizedMessage = new LocalizedText(faultMessage, "en-US");
        faultAlarm.Severity = 1;
        faultAlarm.NormalStateValue = 0.0;
        faultAlarm.InputValueVariable.SetDynamicLink(manager.GetVariable("ProcessFault"));
        Set(telemetry, "AppliedRecipe", new RecipeId { Name = string.Empty, Version = string.Empty });
        Publish();
    }

    private RecipeSchema Schema => Project.Current.Get<RecipeSchema>("Recipes/RecipeSchema");
    private bool Flag(string name) => settings.GetVariable(name)?.Value.Value is bool value && value;

    public void Apply(RecipeId recipe)
    {
        try
        {
            if (Flag("PauseTelemetry") || Flag("SimulateFault")) throw new InvalidOperationException("Oven is unavailable or faulted.");
            var phases = ReadRecipe(recipe);
            if (simulation.Running)
            {
                if (pending != null) Report(pending, RecipeOrchestratorLogic.State.Rejected, "Superseded by another recipe request");
                pending = recipe;
                Set(settings, "PendingRecipe", recipe.Name + " v" + recipe.Version);
                Report(recipe, RecipeOrchestratorLogic.State.Pending, "Applied at the cycle boundary");
                return;
            }
            var transfer = Schema.TransferFromStoreToTarget(recipe, target.NodeId, ErrorPolicy.Strict);
            if (transfer != TransferFromStoreToTargetResultCode.Success) throw new InvalidOperationException("Recipe transfer: " + transfer);
            simulation.Apply(phases);
            applied = recipe;
            Schema.SetRecipeMetadataValue(recipe, "Used", true);
            Set(telemetry, "AppliedRecipe", recipe);
            Report(recipe, RecipeOrchestratorLogic.State.Applied, "Applied locally");
        }
        catch (Exception error) { Report(recipe, RecipeOrchestratorLogic.State.Rejected, error.Message); }
    }

    private LocalOvenPhase[] ReadRecipe(RecipeId recipe)
    {
        var schema = Schema ?? throw new InvalidOperationException("Recipe schema is unavailable.");
        if (string.IsNullOrWhiteSpace(recipe?.Name) || string.IsNullOrWhiteSpace(recipe.Version)) throw new InvalidOperationException("Select a recipe.");
        var status = schema.GetRecipeMetadataValue(recipe, "Status");
        if (status.ResultCode != GetRecipeMetadataValueResultCode.Success || Convert.ToInt32(status.MetadataValue.Value) != 2)
            throw new InvalidOperationException("Only Approved recipes can be applied or started.");
        return new[] { "PreHeat", "Bake", "Cooldown" }.Select(phase => new LocalOvenPhase(
            Convert.ToDouble(ReadValue(schema, recipe, phase, "TemperatureSetpoint")),
            Convert.ToDouble(ReadValue(schema, recipe, phase, "Duration")),
            Convert.ToDouble(ReadValue(schema, recipe, phase, "FanInRamp")),
            Convert.ToDouble(ReadValue(schema, recipe, phase, "FanOutRamp")),
            Convert.ToBoolean(ReadValue(schema, recipe, phase, "Heating")))).ToArray();
    }

    internal static object ReadValue(RecipeSchema schema, RecipeId recipe, string phase, string field)
    {
        var result = schema.GetRecipeDataItemValue(recipe, new[] { phase }, new[] { field },
            new ElementAccessStruct { ArrayIndex = Array.Empty<uint>(), FieldIndexes = Array.Empty<FieldIndexStruct>() });
        if (result.ResultCode != GetRecipeDataItemValueResultCode.Success) throw new InvalidOperationException($"Cannot read {phase}/{field}.");
        return result.DataItemValue;
    }

    public void StartCycle()
    {
        try
        {
            if (simulation.Running) return;
            if (Flag("PauseTelemetry") || Flag("SimulateFault")) throw new InvalidOperationException("Oven is unavailable or faulted.");
            ReadRecipe(applied);
            if (!simulation.Start(Flag("RejectNextCycle"))) throw new InvalidOperationException("Apply a recipe first.");
            Set(settings, "RejectNextCycle", false);
            Set(settings, "LastResult", "Cycle running");
            Set(settings, "CycleRunning", true);
        }
        catch (Exception error)
        {
            Set(settings, "AutoCycle", false);
            Set(settings, "LastResult", error.Message);
        }
    }

    public void StopCycle()
    {
        simulation.Stop();
        Set(settings, "AutoCycle", false);
        Set(settings, "CycleRunning", false);
        Set(settings, "LastResult", "Stopped");
        if (pending != null)
        {
            Report(pending, RecipeOrchestratorLogic.State.Rejected, "Cancelled by stop");
            pending = null;
            Set(settings, "PendingRecipe", string.Empty);
        }
        if (!Flag("PauseTelemetry")) Publish();
    }

    public void Tick()
    {
        ConsumeCommands();
        var faulted = Flag("SimulateFault");
        Set(manager, "ProcessFault", faulted);
        if (faulted && !wasFaulted)
        {
            StopCycle();
            Set(settings, "LastResult", "Process fault - reset before restarting");
        }
        if (!faulted && wasFaulted) Set(settings, "LastResult", "Fault cleared - ready");
        wasFaulted = faulted;
        if (day != DateTime.UtcNow.Date)
        {
            day = DateTime.UtcNow.Date;
            dayEnergy = simulation.EnergyKwh;
            dayCycles = simulation.Cycles;
        }
        var running = simulation.Running;
        simulation.Tick(0.25);
        if (running && !simulation.Running)
        {
            Set(settings, "LastResult", "Cycle completed");
            var delay = Convert.ToDouble(settings.GetVariable("RepeatDelaySeconds").Value.Value);
            nextCycle = Environment.TickCount64 + (long)((double.IsFinite(delay) ? Math.Clamp(delay, 1, 3600) : 5) * 1000);
        }
        if (!simulation.Running && !faulted && !Flag("PauseTelemetry"))
        {
            if (pending != null)
            {
                var requested = pending;
                pending = null;
                Set(settings, "PendingRecipe", string.Empty);
                Apply(requested);
            }
            if (Flag("AutoCycle") && Environment.TickCount64 >= nextCycle) StartCycle();
        }
        Set(settings, "CycleRunning", simulation.Running);
        if (++ticks % 4 == 0 && !Flag("PauseTelemetry")) Publish();
    }

    // The dialog writes one-shot command bits and the selected catalog entry; they are consumed here.
    private void ConsumeCommands()
    {
        if (Flag("ApplyCommand"))
        {
            Set(settings, "ApplyCommand", false);
            var selected = settings.GetVariable("SelectedRecipe")?.Value.Value as NodeId;
            var choice = selected == null || selected.IsEmpty ? null : InformationModel.Get(selected);
            var name = Convert.ToString(choice?.GetVariable("RecipeName")?.Value.Value);
            if (string.IsNullOrEmpty(name)) Set(settings, "LastResult", "Select an approved recipe");
            else Apply(new RecipeId { Name = name, Version = Convert.ToString(choice.GetVariable("RecipeVersion").Value.Value) });
        }
        if (Flag("StartCommand"))
        {
            Set(settings, "StartCommand", false);
            StartCycle();
        }
        if (Flag("StopCommand"))
        {
            Set(settings, "StopCommand", false);
            StopCycle();
        }
    }

    private void Report(RecipeId recipe, RecipeOrchestratorLogic.State state, string reason)
    {
        Set(settings, "LastResult", state + ": " + reason);
        RecipeOrchestratorLogic.RecordLocalResult(DeviceId, recipe, state, reason);
    }

    private void Publish()
    {
        // Keys of the StateText/StateColor converters: 0 Idle, 1 Preheat, 2 Bake, 3 Cooldown, 5 Fault.
        Set(telemetry, "ExecutionState", Flag("SimulateFault") ? 5 : simulation.Phase);
        Set(telemetry, "Temperature", Math.Round(simulation.Temperature, 1));
        Set(telemetry, "TemperatureSetpoint", simulation.Setpoint);
        Set(telemetry, "CurrentBatchId", simulation.Running ? $"{DeviceId}-{simulation.Cycles + 1:D5}" : string.Empty);
        Set(telemetry, "CurrentPhase", simulation.Phase);
        Set(telemetry, "PhaseElapsedSeconds", simulation.PhaseElapsed);
        Set(telemetry, "DoorOpen", false);
        Set(telemetry, "ActiveAlarmCount", Flag("SimulateFault") ? 1u : 0u);
        Set(telemetry, "PowerDraw", simulation.PowerKw);
        Set(telemetry, "CyclesCompletedToday", (uint)(simulation.Cycles - dayCycles));
        Set(telemetry, "EnergyConsumedToday", simulation.EnergyKwh - dayEnergy);
        Set(telemetry, "EnergyConsumedTotal", simulation.EnergyKwh);
        Set(telemetry, "CyclesCompletedTotal", (double)simulation.Cycles);
        Set(telemetry, "GoodCyclesTotal", (double)simulation.GoodCycles);
        Set(telemetry, "RejectedCyclesTotal", (double)simulation.RejectedCycles);
        Set(telemetry, "RunningSecondsTotal", simulation.RunningSeconds);
        Set(telemetry, "IdealProductionSecondsTotal", simulation.IdealSeconds);
        Set(telemetry, "CounterEpoch", epoch);
        Set(telemetry, "UptimeSeconds", (int)(DateTime.UtcNow - started).TotalSeconds);
        Set(telemetry, "Timestamp", DateTime.UtcNow);
    }

    private static void Set(IUANode node, string name, object value)
    {
        var variable = node.GetVariable(name);
        if (variable != null) variable.Value = new UAValue(value);
    }
}

internal sealed class FleetAnalyticsCollector
{
    private const string Category = "FleetAnalytics";
    private const string TableName = "FleetAnalyticsDaily";
    private static readonly string[] CounterNames =
    {
        "EnergyConsumedTotal", "CyclesCompletedTotal", "GoodCyclesTotal",
        "RejectedCyclesTotal", "RunningSecondsTotal", "IdealProductionSecondsTotal"
    };
    private static readonly string[] TextFields =
    {
        "DeviceId", "State", "EnergyText", "CyclesText", "GoodText", "RejectedText", "RunTimeText",
        "AvailabilityText", "PerformanceText", "QualityText", "OeeText"
    };
    private readonly object gate = new object();
    private readonly Dictionary<string, FleetAnalyticsDay> days = new Dictionary<string, FleetAnalyticsDay>();
    private readonly HashSet<string> stored = new HashSet<string>();
    private Store store;
    private IUANode model;
    private IUANode rows;
    private IUANode rowType;
    private IUAVariable plannedHours;
    private string day;
    private DateTime lastFlush;
    private DateTime lastError;
    private DateTime lastChartWrite;
    private string lastChartJson;

    public void Tick()
    {
        lock (gate)
        {
            try
            {
                var now = DateTime.UtcNow;
                if (model == null) Initialize(now);
                var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (day != today) RotateDay(today, now);

                var managers = Project.Current.Get("Model/OvenEdgeManagers")?.Children.ToList();
                var connected = 0;
                var registered = 0;
                var alarms = 0.0;
                var power = 0.0;
                var currentIds = new HashSet<string>();
                if (managers != null)
                {
                    foreach (var manager in managers)
                    {
                        if (!manager.BrowseName.StartsWith(OvenEdgeManagerBehavior.NamePrefix, StringComparison.Ordinal)) continue;
                        registered++;
                        var deviceId = OvenEdgeManagerBehavior.GetDeviceId(manager.BrowseName);
                        currentIds.Add(deviceId);
                        if (!days.TryGetValue(deviceId, out var record))
                        {
                            record = new FleetAnalyticsDay { DayUtc = day, DeviceId = deviceId, PeriodStartUtc = now };
                            days.Add(deviceId, record);
                        }

                        var telemetry = Project.Current.Get("Model/Fleet")?.Get(deviceId);
                        var online = manager.GetVariable("Connection")?.Value.Value is bool value && value && telemetry != null;
                        if (manager.GetVariable("ConnectionAlarmActive")?.Value.Value is bool disconnected && disconnected) alarms++;
                        var local = LocalFleetRuntime.IsLocalManager(manager);
                        if (local && manager.GetVariable("ProcessFault")?.Value.Value is bool faulted && faulted) alarms++;
                        if (telemetry != null) EnsureCounters(telemetry);
                        if (online)
                        {
                            connected++;
                            power += NonNegative(Read(telemetry, "PowerDraw"));
                            if (!local) alarms += NonNegative(Read(telemetry, "ActiveAlarmCount"));
                            Observe(record, telemetry, now);
                        }
                        record.State = online ? "Online" : "Offline";
                    }
                }

                var hours = ReadHours();
                foreach (var record in days.Values)
                {
                    if (!currentIds.Contains(record.DeviceId)) record.State = "Removed";
                    record.PlannedSeconds = record.HasBaseline
                        ? Math.Min(Math.Max(0, (now - record.PeriodStartUtc).TotalSeconds), hours * 3600.0)
                        : 0;
                    WriteRow(record);
                }
                WriteSummary(registered, connected, power, alarms, now);
                try { WriteChartData(now); }
                catch (Exception error)
                {
                    if ((DateTime.UtcNow - lastError).TotalSeconds >= 15)
                    {
                        Log.Warning(Category, "Chart data write failed: " + error.Message);
                        lastError = DateTime.UtcNow;
                    }
                }
                if ((now - lastFlush).TotalSeconds >= 5) FlushCore();
            }
            catch (Exception error)
            {
                if ((DateTime.UtcNow - lastError).TotalSeconds >= 15)
                {
                    Log.Warning(Category, error.Message);
                    lastError = DateTime.UtcNow;
                }
            }
        }
    }

    private void Initialize(DateTime now)
    {
        store = Project.Current.Get<Store>("DataStores/EmbeddedDatabase");
        if (store == null) throw new InvalidOperationException("EmbeddedDatabase is not available.");
        if (store.Tables.Get(TableName) == null) store.AddTable(TableName);
        var table = store.Tables.Get(TableName);
        foreach (var name in new[] { "DayUtc", "DeviceId", "StateJson" })
            if (table.Columns.Get(name) == null) table.AddColumn(name, OpcUa.DataTypes.String);
        foreach (var name in new[] { "EnergyKwh", "Cycles", "GoodCycles", "RejectedCycles", "RunningSeconds", "IdealSeconds", "PlannedSeconds" })
            if (table.Columns.Get(name) == null) table.AddColumn(name, OpcUa.DataTypes.Double);

        day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        store.Query($"SELECT StateJson FROM {TableName} WHERE DayUtc = '{day}'", out _, out var saved);
        for (var index = 0; index < saved.GetLength(0); index++)
        {
            var record = JsonSerializer.Deserialize<FleetAnalyticsDay>(Convert.ToString(saved[index, 0]));
            if (record == null || !OvenMqttClientHelper.IsValidDeviceId(record.DeviceId) || record.Baselines?.Length != CounterNames.Length) continue;
            record.State = "Offline";
            days[record.DeviceId] = record;
            stored.Add(record.DeviceId);
        }

        model = EnsureModel(out rows, out rowType, out plannedHours);
        Log.Info(Category, $"Daily fleet collection ready for {day} UTC; restored {days.Count} records.");
    }

    internal static IUANode EnsureModel(out IUANode rows, out IUANode rowType, out IUAVariable plannedHours)
    {
        var root = Project.Current.Get("Model");
        var model = EnsureFolder(root, "FleetAnalytics");
        rows = EnsureFolder(model, "Rows");
        rowType = root.Get("Types/FleetAnalyticsRow");
        if (rowType == null)
        {
            rowType = InformationModel.MakeObjectType("FleetAnalyticsRow");
            root.Get("Types").Add(rowType);
        }
        foreach (var name in TextFields) EnsureVariable(rowType, name, OpcUa.DataTypes.String);
        foreach (var name in new[] { "TotalOvens", "ConnectedOvens", "ActiveAlarms", "CompletedCycles" })
            EnsureVariable(model, name, OpcUa.DataTypes.Int32);
        foreach (var name in new[] { "CurrentPowerKw", "EnergyTodayKwh", "Availability", "Performance", "Quality", "Oee" })
            EnsureVariable(model, name, OpcUa.DataTypes.Double);
        foreach (var name in new[] { "ConnectedText", "ConnectedRatioText", "AlarmText", "PowerText", "EnergyText", "AvailabilityText", "PerformanceText", "QualityText", "OeeText", "DayText", "CoverageText", "UpdatedText" })
            EnsureVariable(model, name, OpcUa.DataTypes.String);
        plannedHours = EnsureVariable(root.Get("HMISettings"), "AnalyticsPlannedHours", OpcUa.DataTypes.Double, 8.0);
        EnsureCounters(root.Get("Types/OvenType"));
        return model;
    }

    private void WriteChartData(DateTime now)
    {
        var ovens = days.Values.OrderBy(record => record.DeviceId, StringComparer.Ordinal).Select(record =>
        {
            var metrics = FleetOeeMetrics.Calculate(record.PlannedSeconds, record.RunningSeconds, record.IdealSeconds, record.Cycles, record.GoodCycles, record.HasOeeCounters);
            return new
            {
                id = record.DeviceId,
                state = record.State,
                availability = Round(metrics.Availability, 1),
                performance = Round(metrics.Performance, 1),
                quality = Round(metrics.Quality, 1),
                oee = Round(metrics.Oee, 1),
                energyKwh = Math.Round(record.EnergyKwh, 3),
                cycles = record.Cycles
            };
        }).ToList();
        var json = JsonSerializer.Serialize(new { ovens });
        if (json == lastChartJson && (now - lastChartWrite).TotalSeconds < 10) return;

        var directory = ResourceUri.FromProjectRelativePath("eCharts").Uri;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "oven-analytics-data.js");
        var temp = path + ".tmp";
        File.WriteAllText(temp, "window.OVEN_DATA = " + json + ";");
        File.Move(temp, path, true);
        lastChartJson = json;
        lastChartWrite = now;
    }

    private static double? Round(double? value, int digits) => value.HasValue ? Math.Round(value.Value, digits) : (double?)null;

    private static void Observe(FleetAnalyticsDay record, IUANode telemetry, DateTime now)
    {
        if (!(telemetry.GetVariable("Timestamp")?.Value.Value is DateTime timestamp) || timestamp.Year < 2000) return;
        var epoch = Convert.ToString(telemetry.GetVariable("CounterEpoch")?.Value.Value) ?? string.Empty;
        if (record.HasBaseline && record.LastTimestamp == timestamp.Ticks && record.Epoch == epoch) return;
        var uptimeValue = Read(telemetry, "UptimeSeconds");
        if (!double.IsFinite(uptimeValue) || uptimeValue < 0) return;
        var uptime = (long)uptimeValue;
        if (record.HasBaseline && epoch.Length > 0 && record.Epoch == epoch && uptime < record.LastUptime) return;

        var complete = !string.IsNullOrWhiteSpace(epoch);
        var values = CounterNames.Select(name => Read(telemetry, name)).ToArray();
        if (!complete)
        {
            values[0] = Read(telemetry, "EnergyConsumedToday");
            values[1] = Read(telemetry, "CyclesCompletedToday");
        }
        if (values.Any(value => !double.IsFinite(value) || value < 0)) return;
        if (!(telemetry.GetVariable("Timestamp")?.Value.Value is DateTime confirmedTimestamp) || confirmedTimestamp != timestamp) return;
        if (complete && Math.Abs(values[2] + values[3] - values[1]) > 0.001) return;

        var deltas = new double[CounterNames.Length];
        for (var index = 0; index < CounterNames.Length; index++)
        {
            var accumulator = new FleetCounterAccumulator();
            if (record.HasBaseline) accumulator.Restore(record.LastTimestamp, record.Baselines[index], record.LastUptime);
            deltas[index] = record.HasBaseline && record.Epoch != epoch && !string.IsNullOrEmpty(record.Epoch)
                ? values[index]
                : record.HasBaseline && record.Epoch != epoch
                ? 0
                : accumulator.Observe(timestamp.Ticks, values[index], uptime);
        }
        if (!record.HasBaseline) record.PeriodStartUtc = now;
        record.EnergyKwh += deltas[0];
        record.Cycles += deltas[1];
        record.GoodCycles += deltas[2];
        record.RejectedCycles += deltas[3];
        record.RunningSeconds += deltas[4];
        record.IdealSeconds += deltas[5];
        if (complete && record.HasBaseline && !record.HasOeeCounters)
        {
            record.Cycles = 0;
            record.GoodCycles = 0;
            record.RejectedCycles = 0;
            record.RunningSeconds = 0;
            record.IdealSeconds = 0;
            record.PeriodStartUtc = now;
        }
        record.HasOeeCounters = complete;
        record.HasBaseline = true;
        record.Baselines = values;
        record.LastTimestamp = timestamp.Ticks;
        record.LastUptime = uptime;
        record.Epoch = epoch;
        record.LastSeenUtc = now;
    }

    private void RotateDay(string today, DateTime now)
    {
        FlushCore();
        var previous = days.Values.ToList();
        days.Clear();
        stored.Clear();
        foreach (var record in previous)
        {
            days[record.DeviceId] = new FleetAnalyticsDay
            {
                DayUtc = today, DeviceId = record.DeviceId, PeriodStartUtc = now.Date,
                HasBaseline = record.HasBaseline, HasOeeCounters = record.HasOeeCounters,
                Baselines = record.Baselines, LastTimestamp = record.LastTimestamp,
                LastUptime = record.LastUptime, Epoch = record.Epoch, LastSeenUtc = record.LastSeenUtc
            };
        }
        day = today;
        foreach (var child in rows.Children.ToList()) child.Delete();
    }

    private void WriteRow(FleetAnalyticsDay record)
    {
        var row = rows.Get(record.DeviceId);
        if (row == null)
        {
            row = InformationModel.MakeObject(record.DeviceId, rowType.NodeId);
            rows.Add(row);
        }
        var metrics = FleetOeeMetrics.Calculate(record.PlannedSeconds, record.RunningSeconds, record.IdealSeconds, record.Cycles, record.GoodCycles, record.HasOeeCounters);
        Set(row, "DeviceId", record.DeviceId);
        Set(row, "State", record.State);
        Set(row, "EnergyText", record.EnergyKwh.ToString("0.000", CultureInfo.InvariantCulture));
        Set(row, "CyclesText", record.Cycles.ToString("0", CultureInfo.InvariantCulture));
        Set(row, "GoodText", record.HasOeeCounters ? record.GoodCycles.ToString("0", CultureInfo.InvariantCulture) : "--");
        Set(row, "RejectedText", record.HasOeeCounters ? record.RejectedCycles.ToString("0", CultureInfo.InvariantCulture) : "--");
        Set(row, "RunTimeText", record.HasOeeCounters ? TimeSpan.FromSeconds(record.RunningSeconds).ToString(@"hh\:mm\:ss") : "--");
        Set(row, "AvailabilityText", Percent(metrics.Availability));
        Set(row, "PerformanceText", Percent(metrics.Performance));
        Set(row, "QualityText", Percent(metrics.Quality));
        Set(row, "OeeText", Percent(metrics.Oee));
    }

    private void WriteSummary(int registered, int connected, double power, double alarms, DateTime now)
    {
        var records = days.Values.ToList();
        var measured = records.Where(record => record.HasOeeCounters && record.HasBaseline).ToList();
        var metrics = FleetOeeMetrics.Calculate(measured.Sum(record => record.PlannedSeconds), measured.Sum(record => record.RunningSeconds), measured.Sum(record => record.IdealSeconds), measured.Sum(record => record.Cycles), measured.Sum(record => record.GoodCycles), measured.Count > 0);
        var energy = records.Sum(record => record.EnergyKwh);
        Set(model, "TotalOvens", registered);
        Set(model, "ConnectedOvens", connected);
        Set(model, "ActiveAlarms", (int)alarms);
        Set(model, "CompletedCycles", (int)records.Sum(record => record.Cycles));
        Set(model, "CurrentPowerKw", power);
        Set(model, "EnergyTodayKwh", energy);
        Set(model, "ConnectedText", registered == 0 ? "No ovens registered" : $"{connected} of {registered} ovens online");
        Set(model, "ConnectedRatioText", "/" + registered);
        Set(model, "AlarmText", "Process and connection alarms");
        Set(model, "PowerText", power.ToString("0.00", CultureInfo.InvariantCulture));
        Set(model, "EnergyText", energy.ToString("0.000", CultureInfo.InvariantCulture));
        Set(model, "DayText", day + " UTC");
        Set(model, "CoverageText", $"OEE coverage: {measured.Count} / {records.Count} ovens");
        Set(model, "UpdatedText", now.ToString("HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        Set(model, "Availability", metrics.Availability ?? 0);
        Set(model, "Performance", metrics.Performance ?? 0);
        Set(model, "Quality", metrics.Quality ?? 0);
        Set(model, "Oee", metrics.Oee ?? 0);
        Set(model, "AvailabilityText", Percent(metrics.Availability));
        Set(model, "PerformanceText", Percent(metrics.Performance));
        Set(model, "QualityText", Percent(metrics.Quality));
        Set(model, "OeeText", Percent(metrics.Oee));
    }

    public void Flush()
    {
        lock (gate)
        {
            try { if (model != null) FlushCore(); }
            catch (Exception error) { Log.Warning(Category, "Checkpoint failed: " + error.Message); }
        }
    }

    private void FlushCore()
    {
        foreach (var record in days.Values)
        {
            var json = JsonSerializer.Serialize(record);
            if (!stored.Contains(record.DeviceId))
            {
                store.Insert(TableName,
                    new[] { "DayUtc", "DeviceId", "StateJson", "EnergyKwh", "Cycles", "GoodCycles", "RejectedCycles", "RunningSeconds", "IdealSeconds", "PlannedSeconds" },
                    new object[,] { { record.DayUtc, record.DeviceId, json, record.EnergyKwh, record.Cycles, record.GoodCycles, record.RejectedCycles, record.RunningSeconds, record.IdealSeconds, record.PlannedSeconds } });
                stored.Add(record.DeviceId);
            }
            else
            {
                var updates = $"StateJson = '{Escape(json)}', EnergyKwh = {Number(record.EnergyKwh)}, Cycles = {Number(record.Cycles)}, GoodCycles = {Number(record.GoodCycles)}, RejectedCycles = {Number(record.RejectedCycles)}, RunningSeconds = {Number(record.RunningSeconds)}, IdealSeconds = {Number(record.IdealSeconds)}, PlannedSeconds = {Number(record.PlannedSeconds)}";
                store.Query($"UPDATE {TableName} SET {updates} WHERE DayUtc = '{record.DayUtc}' AND DeviceId = '{Escape(record.DeviceId)}'", out _, out _);
            }
        }
        lastFlush = DateTime.UtcNow;
    }

    internal static void EnsureCounters(IUANode target)
    {
        if (target == null) return;
        foreach (var name in CounterNames) EnsureVariable(target, name, OpcUa.DataTypes.Double);
        EnsureVariable(target, "CounterEpoch", OpcUa.DataTypes.String);
    }

    internal static IUANode EnsureFolder(IUANode parent, string name)
    {
        var folder = parent.Get(name);
        if (folder != null) return folder;
        folder = InformationModel.MakeObject<Folder>(name);
        parent.Add(folder);
        return folder;
    }

    internal static IUAVariable EnsureVariable(IUANode parent, string name, NodeId dataType, object initial = null)
    {
        var variable = parent.GetVariable(name);
        if (variable != null) return variable;
        variable = InformationModel.MakeVariable(name, dataType);
        if (initial != null) variable.Value = new UAValue(initial);
        parent.Add(variable);
        return variable;
    }

    private double ReadHours()
    {
        var hours = Convert.ToDouble(plannedHours.Value.Value, CultureInfo.InvariantCulture);
        return double.IsFinite(hours) ? Math.Clamp(hours, 0.01, 24) : 8;
    }

    private static double Read(IUANode node, string name) => Convert.ToDouble(node.GetVariable(name)?.Value.Value ?? 0, CultureInfo.InvariantCulture);
    private static double NonNegative(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Escape(string value) => value.Replace("'", "''");
    private static string Percent(double? value) => value.HasValue ? value.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "--";
    private static void Set(IUANode node, string name, object value) => node.GetVariable(name).Value = new UAValue(value);
}

/// <summary>
/// Feeds the Fleet Overview panel: fleet state counts and the latest oven events, written as a script next to the
/// pages that render them (one page per theme and locale, so the texts follow the session language).
/// </summary>
internal sealed class FleetOverviewCollector
{
    private const string Category = "FleetOverview";
    private const int Capacity = 40;
    private const long WarmUpMs = 15000;
    private static readonly string[] Locales = { "en-US", "es-ES", "it-IT", "fr-FR", "de-DE", "zh-CN" };
    private readonly Dictionary<string, OvenSnapshot> previous = new Dictionary<string, OvenSnapshot>(StringComparer.Ordinal);
    private readonly List<OverviewEvent> events = new List<OverviewEvent>();
    private readonly long armedAt = Environment.TickCount64 + WarmUpMs;
    private static readonly System.Collections.Concurrent.ConcurrentQueue<OverviewEvent> Queued = new System.Collections.Concurrent.ConcurrentQueue<OverviewEvent>();
    private string directory;
    private string published;
    private long nextError;

    private sealed class OvenSnapshot
    {
        public bool Online;
        public bool Fault;
        public bool Running;
        public string Recipe = string.Empty;
        public double Cycles;
        public double Good;
    }

    private sealed class OverviewEvent
    {
        public long t { get; set; }
        public string k { get; set; }
        public string id { get; set; }
        public string a { get; set; }
    }

    // Thread-safe: the feed picks the event up on its next tick. Kinds are the keys of fleet-overview.js (the id is shown in bold).
    public static void Record(string kind, string id, string argument = null) =>
        Queued.Enqueue(new OverviewEvent { t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), k = kind, id = id, a = string.IsNullOrEmpty(argument) ? null : argument });

    public void Tick()
    {
        try
        {
            var managers = Project.Current.Get("Model/" + OvenEdgeManagerBehavior.ManagersFolderName)?.Children.ToList();
            var fleet = Project.Current.Get(OvenEdgeManagerBehavior.TelemetryFolderPath);
            if (managers == null || fleet == null) return;
            if (directory == null) Prepare();
            while (Queued.TryDequeue(out var queued)) Insert(queued);

            // Events start after the warm-up: every restart would otherwise log each oven as freshly connected.
            var armed = Environment.TickCount64 >= armedAt;
            var counts = new Dictionary<string, int> { ["running"] = 0, ["idle"] = 0, ["offline"] = 0, ["fault"] = 0 };
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var manager in managers)
            {
                if (!manager.BrowseName.StartsWith(OvenEdgeManagerBehavior.NamePrefix, StringComparison.Ordinal)) continue;
                var id = OvenEdgeManagerBehavior.GetDeviceId(manager.BrowseName);
                if (!OvenMqttClientHelper.IsValidDeviceId(id)) continue;
                present.Add(id);
                var now = Observe(manager, fleet.Get(id));
                counts[!now.Online ? "offline" : now.Fault ? "fault" : now.Running ? "running" : "idle"]++;
                if (previous.TryGetValue(id, out var before))
                {
                    if (armed) Diff(id, before, now);
                }
                previous[id] = now;
            }
            foreach (var id in previous.Keys.Where(id => !present.Contains(id)).ToList())
                previous.Remove(id);
            Publish(present.Count, counts);
        }
        catch (Exception error) { Warn(error.Message); }
    }

    private static OvenSnapshot Observe(IUANode manager, IUANode telemetry)
    {
        var online = manager.GetVariable("Connection")?.Value.Value is bool connected && connected;
        var processFault = manager.GetVariable("ProcessFault")?.Value.Value is bool fault && fault;
        var reported = telemetry == null ? 0 : (int)Number(telemetry, "ActiveAlarmCount");
        var phase = telemetry == null ? 0 : (int)Number(telemetry, "CurrentPhase");
        var state = telemetry == null ? 0 : (int)Number(telemetry, "ExecutionState");
        var recipe = telemetry?.GetVariable("AppliedRecipe")?.Value.Value as RecipeId;
        return new OvenSnapshot
        {
            Online = online,
            Fault = processFault || (!LocalFleetRuntime.IsLocalManager(manager) && reported > 0),
            Running = online && phase >= 1 && state != 0 && state != 5,
            Recipe = recipe == null || string.IsNullOrEmpty(recipe.Name) ? string.Empty : $"{recipe.Name} v{recipe.Version}",
            Cycles = telemetry == null ? 0 : Number(telemetry, "CyclesCompletedTotal"),
            Good = telemetry == null ? 0 : Number(telemetry, "GoodCyclesTotal")
        };
    }

    private void Diff(string id, OvenSnapshot before, OvenSnapshot now)
    {
        if (before.Online != now.Online) Add(now.Online ? "connected" : "disconnected", id, null);
        if (before.Fault != now.Fault) Add(now.Fault ? "fault" : "faultCleared", id, null);
        if (!before.Running && now.Running) Add("cycleStarted", id, now.Recipe);
        else if (before.Running && !now.Running && now.Online)
        {
            if (now.Cycles > before.Cycles) Add(now.Good > before.Good ? "cycleCompleted" : "cycleRejected", id, now.Recipe);
            else Add("cycleStopped", id, null);
        }
        if (now.Recipe.Length > 0 && now.Recipe != before.Recipe) Add("recipeApplied", id, now.Recipe);
    }

    private void Add(string kind, string id, string argument) =>
        Insert(new OverviewEvent { t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), k = kind, id = id, a = string.IsNullOrEmpty(argument) ? null : argument });

    private void Insert(OverviewEvent overviewEvent)
    {
        events.Insert(0, overviewEvent);
        if (events.Count > Capacity) events.RemoveRange(Capacity, events.Count - Capacity);
    }

    private void Publish(int total, Dictionary<string, int> counts)
    {
        var json = JsonSerializer.Serialize(new { total, counts, events });
        if (json == published) return;

        var path = Path.Combine(directory, "fleet-overview-data.js");
        var temp = path + ".tmp";
        File.WriteAllText(temp, "window.FLEET_OVERVIEW = " + json + ";");
        File.Move(temp, path, true);
        published = json;
    }

    private void Prepare()
    {
        directory = ResourceUri.FromProjectRelativePath("eCharts").Uri;
        Directory.CreateDirectory(directory);
        foreach (var theme in new[] { "light", "dark" })
        {
            foreach (var locale in Locales)
            {
                var path = Path.Combine(directory, $"fleet-overview-{theme}-{locale}.html");
                var page = Page(theme, locale);
                if (!File.Exists(path) || File.ReadAllText(path) != page) File.WriteAllText(path, page);
            }
        }

        // The last published events survive a restart.
        var data = Path.Combine(directory, "fleet-overview-data.js");
        if (!File.Exists(data)) return;
        try
        {
            var text = File.ReadAllText(data);
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            using var document = JsonDocument.Parse(text.Substring(start, end - start + 1));
            foreach (var item in document.RootElement.GetProperty("events").EnumerateArray())
            {
                events.Add(new OverviewEvent
                {
                    t = item.GetProperty("t").GetInt64(),
                    k = item.GetProperty("k").GetString(),
                    id = item.GetProperty("id").GetString(),
                    a = item.TryGetProperty("a", out var argument) && argument.ValueKind == JsonValueKind.String ? argument.GetString() : null
                });
            }
        }
        catch (Exception error) { Warn("Previous events ignored: " + error.Message); }
    }

    private static string Page(string theme, string locale) =>
        $"<!DOCTYPE html>\n<html lang=\"{locale}\">\n<head>\n<meta charset=\"utf-8\">\n<link rel=\"stylesheet\" href=\"./fleet-overview.css\">\n</head>\n" +
        $"<body data-theme=\"{theme}\" data-lang=\"{locale}\">\n<div id=\"root\">\n" +
        "<section class=\"panel status\"><header><h2 id=\"statusTitle\"></h2><span id=\"onlineText\" class=\"muted\"></span></header>" +
        "<div class=\"body\"><div class=\"donut\"><div id=\"chart\"></div><div id=\"center\"><span id=\"total\"></span><span id=\"totalLabel\"></span></div></div><ul id=\"legend\"></ul></div></section>\n" +
        "<section class=\"panel activity\"><header><h2 id=\"activityTitle\"></h2></header><ul id=\"feed\"></ul><div id=\"empty\"></div></section>\n" +
        "</div>\n<script src=\"./echarts.min.js\"></script>\n<script src=\"./fleet-overview.js\"></script>\n</body>\n</html>\n";

    private static double Number(IUANode node, string name)
    {
        var value = node.GetVariable(name)?.Value.Value;
        if (value == null) return 0;
        var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return double.IsFinite(number) ? number : 0;
    }

    private void Warn(string message)
    {
        if (Environment.TickCount64 < nextError) return;
        nextError = Environment.TickCount64 + 15000;
        Log.Warning(Category, message);
    }
}

public sealed class FleetAnalyticsDay
{
    public string DayUtc { get; set; }
    public string DeviceId { get; set; }
    public string State { get; set; } = "Offline";
    public string Epoch { get; set; } = string.Empty;
    public DateTime PeriodStartUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public bool HasBaseline { get; set; }
    public bool HasOeeCounters { get; set; }
    public long LastTimestamp { get; set; }
    public long LastUptime { get; set; }
    public double[] Baselines { get; set; } = new double[6];
    public double EnergyKwh { get; set; }
    public double Cycles { get; set; }
    public double GoodCycles { get; set; }
    public double RejectedCycles { get; set; }
    public double RunningSeconds { get; set; }
    public double IdealSeconds { get; set; }
    public double PlannedSeconds { get; set; }
}

public sealed class FleetCounterAccumulator
{
    public bool Initialized { get; private set; }
    public long SampleTimestamp { get; private set; }
    public double Counter { get; private set; }
    public long UptimeSeconds { get; private set; }

    public void Restore(long timestamp, double counter, long uptimeSeconds)
    {
        if (!double.IsFinite(counter) || counter < 0 || uptimeSeconds < 0)
            return;

        Initialized = true;
        SampleTimestamp = timestamp;
        Counter = counter;
        UptimeSeconds = uptimeSeconds;
    }

    public double Observe(long timestamp, double counter, long uptimeSeconds)
    {
        if (!double.IsFinite(counter) || counter < 0 || uptimeSeconds < 0)
            return 0;
        if (Initialized && timestamp == SampleTimestamp)
            return 0;

        var delta = !Initialized ? 0 :
            uptimeSeconds < UptimeSeconds || counter < Counter ? counter : counter - Counter;
        Restore(timestamp, counter, uptimeSeconds);
        return delta;
    }
}

public sealed class FleetOeeMetrics
{
    public double? Availability { get; private set; }
    public double? Performance { get; private set; }
    public double? Quality { get; private set; }
    public double? Oee { get; private set; }

    public static FleetOeeMetrics Calculate(double plannedSeconds, double runningSeconds, double idealSeconds, double totalCycles, double goodCycles, bool complete)
    {
        var result = new FleetOeeMetrics();
        if (!complete || !double.IsFinite(plannedSeconds) || !double.IsFinite(runningSeconds) || !double.IsFinite(idealSeconds) || !double.IsFinite(totalCycles) || !double.IsFinite(goodCycles)) return result;
        if (plannedSeconds < 0 || runningSeconds < 0 || idealSeconds < 0 || totalCycles < 0 || goodCycles < 0 || goodCycles > totalCycles) return result;
        if (plannedSeconds > 0) result.Availability = System.Math.Clamp(runningSeconds / plannedSeconds, 0, 1) * 100;
        if (runningSeconds > 0 && totalCycles > 0) result.Performance = System.Math.Clamp(idealSeconds / runningSeconds, 0, 1) * 100;
        if (totalCycles > 0) result.Quality = goodCycles / totalCycles * 100;
        if (result.Availability.HasValue && result.Performance.HasValue && result.Quality.HasValue)
            result.Oee = result.Availability.Value * result.Performance.Value * result.Quality.Value / 10000;
        return result;
    }
}

public sealed class LocalOvenPhase
{
    public LocalOvenPhase(double temperature, double duration, double fanIn, double fanOut, bool heating)
    {
        if (!double.IsFinite(temperature) || temperature < 0 || temperature > 512 ||
            !double.IsFinite(duration) || duration < 1 || duration > 7200 ||
            !double.IsFinite(fanIn) || fanIn < 0 || fanIn > 100 ||
            !double.IsFinite(fanOut) || fanOut < 0 || fanOut > 100)
            throw new System.ArgumentOutOfRangeException(nameof(duration), "Invalid recipe phase.");
        Temperature = temperature;
        Duration = duration;
        FanIn = fanIn;
        FanOut = fanOut;
        Heating = heating;
    }

    public double Temperature { get; }
    public double Duration { get; }
    public double FanIn { get; }
    public double FanOut { get; }
    public bool Heating { get; }
}

public sealed class LocalOvenSimulation
{
    private LocalOvenPhase[] recipe;
    private int phaseIndex;
    private bool heaterOn;
    private bool rejectCycle;
    private double idealCycleSeconds;

    public bool Running { get; private set; }
    public bool HasRecipe => recipe != null;
    public double Temperature { get; private set; } = 20;
    public double Setpoint => Running ? recipe[phaseIndex].Temperature : 20;
    public int Phase => Running ? phaseIndex + 1 : 0;
    public double PhaseElapsed { get; private set; }
    public double PowerKw { get; private set; }
    public double EnergyKwh { get; private set; }
    public long Cycles { get; private set; }
    public long GoodCycles { get; private set; }
    public long RejectedCycles { get; private set; }
    public double RunningSeconds { get; private set; }
    public double IdealSeconds { get; private set; }

    public void Apply(LocalOvenPhase[] phases)
    {
        if (Running) throw new System.InvalidOperationException("A cycle is running.");
        if (phases == null || phases.Length != 3 || System.Array.Exists(phases, phase => phase == null))
            throw new System.ArgumentException("PreHeat, Bake and Cooldown are required.");
        recipe = (LocalOvenPhase[])phases.Clone();
    }

    public bool Start(bool reject)
    {
        if (Running || recipe == null) return false;
        rejectCycle = reject;
        idealCycleSeconds = 0;
        foreach (var phase in recipe) idealCycleSeconds += phase.Duration;
        phaseIndex = 0;
        PhaseElapsed = 0;
        Running = true;
        return true;
    }

    public void Stop()
    {
        Running = false;
        PhaseElapsed = 0;
        heaterOn = false;
        PowerKw = 0;
    }

    public void Tick(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 1)
            throw new System.ArgumentOutOfRangeException(nameof(seconds));
        if (!Running)
        {
            Temperature = System.Math.Max(20, Temperature - 8 * seconds);
            PowerKw = 0;
            return;
        }

        var phase = recipe[phaseIndex];
        var elapsed = System.Math.Min(seconds, phase.Duration - PhaseElapsed);
        if (phase.Heating)
        {
            if (Temperature < phase.Temperature - 2) heaterOn = true;
            else if (Temperature >= phase.Temperature + 2) heaterOn = false;
            Temperature = heaterOn
                ? System.Math.Min(phase.Temperature + 2, Temperature + 30 * elapsed)
                : System.Math.Max(20, Temperature - elapsed);
        }
        else
        {
            heaterOn = false;
            Temperature = System.Math.Max(System.Math.Max(20, phase.Temperature), Temperature - 8 * elapsed);
        }
        PowerKw = (heaterOn ? 12 : 0) + (phase.FanIn + phase.FanOut) * 0.02;
        EnergyKwh += PowerKw * elapsed / 3600;
        RunningSeconds += elapsed;
        PhaseElapsed += elapsed;
        if (PhaseElapsed < phase.Duration) return;
        PhaseElapsed = 0;
        phaseIndex++;
        if (phaseIndex < recipe.Length) return;
        Cycles++;
        if (rejectCycle) RejectedCycles++;
        else GoodCycles++;
        IdealSeconds += idealCycleSeconds;
        Stop();
    }
}

public sealed class OvenCycleTraceStore
{
    private const int MaxSamples = 7200;
    private readonly object sync = new object();
    private readonly System.Collections.Generic.Dictionary<string, Trace> traces = new System.Collections.Generic.Dictionary<string, Trace>(System.StringComparer.Ordinal);

    private sealed class Trace
    {
        public readonly System.Collections.Generic.List<double[]> Samples = new System.Collections.Generic.List<double[]>();
        public bool Running;
    }

    // The trace restarts when a cycle begins and is kept after it ends.
    public void Record(string ovenId, bool running, double cycleElapsedSeconds, double temperature)
    {
        lock (sync)
        {
            if (!traces.TryGetValue(ovenId, out var trace)) traces[ovenId] = trace = new Trace();
            if (running && !trace.Running) trace.Samples.Clear();
            trace.Running = running;
            if (!running) return;
            trace.Samples.Add(new[] { System.Math.Round(cycleElapsedSeconds, 1), System.Math.Round(temperature, 1) });
            if (trace.Samples.Count > MaxSamples) trace.Samples.RemoveAt(0);
        }
    }

    public double[][] Get(string ovenId)
    {
        lock (sync) return traces.TryGetValue(ovenId, out var trace) ? trace.Samples.ToArray() : new double[0][];
    }
}

public sealed class OvenCycleSnapshotPhase
{
    public OvenCycleSnapshotPhase(string name, double start, double end, double setpoint)
    {
        Name = name;
        Start = start;
        End = end;
        Setpoint = setpoint;
    }

    public string Name { get; }
    public double Start { get; }
    public double End { get; }
    public double Setpoint { get; }
}

public static class OvenCycleSnapshot
{
    public static double Elapsed(OvenCycleSnapshotPhase[] phases, int phase, double phaseElapsed, double fallbackSeconds)
    {
        if (phases == null || phase < 1 || phase > phases.Length) return fallbackSeconds;
        return phases[phase - 1].Start + phaseElapsed;
    }

    public static string Build(string deviceId, bool online, bool running, int phase, double temperature, double setpoint, double cycleElapsed, string recipe, OvenCycleSnapshotPhase[] phases, double[][] actual)
    {
        phases = phases ?? new OvenCycleSnapshotPhase[0];
        var total = phases.Length == 0 ? 0 : phases[phases.Length - 1].End;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            deviceId,
            online,
            state = running ? 1 : 0,
            phase = running ? phase : 0,
            temperature = System.Math.Round(temperature, 1),
            setpoint = running ? setpoint : 0.0,
            cycleElapsed = running ? System.Math.Round(cycleElapsed, 1) : 0.0,
            total,
            recipe = recipe ?? string.Empty,
            phases = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(phases, p => new { name = p.Name, start = p.Start, end = p.End, setpoint = p.Setpoint })),
            actual = actual ?? new double[0][]
        });
    }
}
