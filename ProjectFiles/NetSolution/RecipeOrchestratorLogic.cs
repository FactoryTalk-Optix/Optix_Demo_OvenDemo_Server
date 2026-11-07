#region Using directives
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.Core;
using FTOptix.CoreBase;
using FTOptix.HMIProject;
using FTOptix.MQTTClient;
using FTOptix.NetLogic;
using FTOptix.RecipeX;
using FTOptix.SQLiteStore;
using FTOptix.Store;
#endregion

/// <summary>
/// Recipe distribution from the supervisor to the ovens. <see cref="SendToFleet"/> broadcasts a released recipe to
/// every onboarded oven, where it is only stored: the supervisor never applies a recipe, each oven decides on its own and
/// may report it (<c>recipe.applyResult</c>, information only). Every step is recorded in the SQLite table
/// <c>RecipeDeliveries</c> (one row per oven, recipe and kind: 0 delivery, 1 applied on the oven), which the UI uses as a
/// deployment matrix. An oven that reconnects announces its catalog (<c>recipe.hello</c>) and receives what it is missing.
/// Topics: <c>recipes/&lt;DeviceId&gt;/to-edge</c> and <c>recipes/&lt;DeviceId&gt;/to-supervisor</c>.
/// </summary>
public class RecipeOrchestratorLogic : BaseNetLogic
{
    private const string Cat = "RecipeOrchestratorLogic";
    private const string SchemaPath = "Recipes/RecipeSchema";
    private const string StoreName = "EmbeddedDatabase";
    private const string TableName = "RecipeDeliveries";
    private const string FolderName = "RecipeFleet";
    private const string SubscriberName = "Subscribers";
    private const string SubscriptionName = "Recipes";
    private const int StatusApproved = 2;
    private const int StatusDeprecated = 3;

    private static readonly string[] PhaseNames = { "PreHeat", "Bake", "Cooldown" };
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    private static readonly object DbLock = new object();
    private static RecipeOrchestratorLogic instance;

    private RecipeSchema schema;
    private IUAVariable inbox;
    private IUAVariable revision;
    private PeriodicTask task;
    private long nextId;

    /// <summary>Kind of journal row.</summary>
    public enum Kind { Send = 0, Applied = 1 }

    /// <summary>State shown in the deployment matrix.</summary>
    public enum State { Queued = 0, Sent = 1, Delivered = 2, Rejected = 3, Applied = 4, Pending = 5, Unreachable = 6 }

    public override void Start()
    {
        schema = Project.Current.Get<RecipeSchema>(SchemaPath);
        if (schema == null)
        {
            Log.Error(Cat, $"Recipe schema '{SchemaPath}' not found, orchestrator disabled.");
            return;
        }

        EnsureLink();
        EnsureTable();
        inbox.VariableChange += OnInbox;
        instance = this;
        task = new PeriodicTask(EnsureSubscriptions, 10000, LogicObject);
        task.Start();
    }

    public override void Stop()
    {
        instance = null;
        task?.Dispose();
        task = null;
        if (inbox != null) inbox.VariableChange -= OnInbox;
    }

    #region Public API (ExportMethod)

    /// <summary>Broadcasts a released (Approved or Deprecated) recipe to every oven.</summary>
    [ExportMethod]
    public void SendToFleet(string name, string version)
    {
        var message = BuildSendMessage(new RecipeId { Name = name, Version = version }, out var error);
        if (message == null) { Log.Warning(Cat, $"SendToFleet {name} v{version}: {error}"); return; }

        foreach (var deviceId in Devices())
            SendTo(deviceId, message);
        Log.Info(Cat, $"{name} v{version} sent to the fleet");
        FleetOverviewCollector.Record("recipeSent", $"{name} v{version}");
    }

    /// <summary>Resends the recipe to one oven (for example after a rejection that has been fixed).</summary>
    [ExportMethod]
    public void ResendToOven(string deviceId, string name, string version)
    {
        var message = BuildSendMessage(new RecipeId { Name = name, Version = version }, out var error);
        if (message == null) { Log.Warning(Cat, $"ResendToOven {name} v{version}: {error}"); return; }
        SendTo(deviceId, message);
    }

    /// <summary>
    /// Publishes an Approved recipe to the fleet, or pushes a Deprecated status to ovens that already received it.
    /// </summary>
    public static void NotifyStatusChanged(string name, string version)
    {
        var self = instance;
        if (self == null) return;

        var id = new RecipeId { Name = name, Version = version };
        var status = self.GetMeta(id, "Status");
        if (status == null) return;

        var workflowStatus = Convert.ToUInt32(status);
        if (workflowStatus == StatusDeprecated && !self.WasSent(name, version))
            return;
        if (workflowStatus != StatusApproved && workflowStatus != StatusDeprecated)
            return;

        self.SendToFleet(name, version);
    }

    #endregion

    #region Sending

    private void SendTo(string deviceId, RecipeMessage template)
    {
        if (LocalFleetRuntime.IsLocal(deviceId))
        {
            Upsert(deviceId, template.Name, template.Version, Kind.Send, State.Delivered, "Available in the local catalog", string.Empty, template.Hash);
            Bump();
            return;
        }
        var message = new RecipeMessage
        {
            Type = "recipe.send", DeviceId = deviceId, MsgId = Guid.NewGuid().ToString(), Name = template.Name, Version = template.Version,
            Hash = template.Hash, Status = template.Status, Author = template.Author, Description = template.Description, Phases = template.Phases
        };

        var reachable = IsReachable(deviceId);
        Upsert(deviceId, template.Name, template.Version, Kind.Send, reachable ? State.Sent : State.Queued, reachable ? string.Empty : "Oven offline: sent when it reconnects", string.Empty, template.Hash);
        if (reachable && !Publish(deviceId, message))
            Upsert(deviceId, template.Name, template.Version, Kind.Send, State.Queued, "Not connected: sent when it reconnects", string.Empty, template.Hash);
        Bump();
    }

    #endregion

    #region Incoming messages

    internal static void RecordLocalResult(string deviceId, RecipeId recipe, State state, string reason)
    {
        var self = instance;
        if (self == null || recipe == null || string.IsNullOrWhiteSpace(recipe.Name)) return;
        self.Upsert(deviceId, recipe.Name, recipe.Version, Kind.Applied, state, reason, string.Empty, string.Empty);
        self.Bump();
    }

    private void OnInbox(object sender, VariableChangeEventArgs e)
    {
        var json = e.NewValue.Value as string;
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            var m = JsonSerializer.Deserialize<RecipeMessage>(json, Json);
            if (m == null || !OvenMqttClientHelper.IsValidDeviceId(m.DeviceId ?? string.Empty))
            {
                Log.Warning(Cat, $"Discarded recipe message without a valid DeviceId: {json}");
                return;
            }

            switch (m.Type)
            {
                case "recipe.ack":
                    Upsert(m.DeviceId, m.Name, m.Version, Kind.Send, m.Result == "Stored" ? State.Delivered : State.Rejected, m.Reason ?? string.Empty, string.Empty, m.Hash);
                    break;
                case "recipe.applyResult":
                    OnApplyResult(m);
                    break;
                case "recipe.hello":
                    OnHello(m);
                    break;
                default:
                    Log.Warning(Cat, $"Unknown message type '{m.Type}' from {m.DeviceId}");
                    break;
            }
            Bump();
        }
        catch (Exception ex)
        {
            Log.Error(Cat, $"Message handling failed: {ex.Message}");
        }
    }

    private void OnApplyResult(RecipeMessage m)
    {
        var state = m.Result == "Applied" ? State.Applied : m.Result == "Pending" ? State.Pending : State.Rejected;
        Upsert(m.DeviceId, m.Name, m.Version, Kind.Applied, state, m.Reason ?? string.Empty, m.CommandId ?? string.Empty, string.Empty);
        if (state == State.Applied) SetMeta(new RecipeId { Name = m.Name, Version = m.Version }, "Used", true);
    }

    /// <summary>The oven announced its catalog: sends every fleet recipe it lacks or holds with different content or status.</summary>
    private void OnHello(RecipeMessage hello)
    {
        var held = (hello.Recipes ?? new List<RecipeInfo>()).ToDictionary(r => r.Name + "@" + r.Version, r => r);
        foreach (var (name, version) in FleetCatalog())
        {
            var message = BuildSendMessage(new RecipeId { Name = name, Version = version }, out _);
            if (message == null) continue;

            if (held.TryGetValue(name + "@" + version, out var info) && info.Hash == message.Hash && info.Status == message.Status)
            {
                Upsert(hello.DeviceId, name, version, Kind.Send, State.Delivered, string.Empty, string.Empty, message.Hash);
                continue;
            }
            SendTo(hello.DeviceId, message);
        }
        Log.Info(Cat, $"Reconciled recipe catalog of {hello.DeviceId}");
    }

    #endregion

    #region Devices and MQTT

    private static IEnumerable<string> Devices()
    {
        var managers = Project.Current.Get($"Model/{OvenEdgeManagerBehavior.ManagersFolderName}");
        if (managers == null) yield break;
        foreach (var manager in managers.Children.ToList())
        {
            var id = OvenEdgeManagerBehavior.GetDeviceId(manager.BrowseName);
            if (OvenMqttClientHelper.IsValidDeviceId(id)) yield return id;
        }
    }

    private static bool IsReachable(string deviceId)
    {
        var manager = Project.Current.Get($"Model/{OvenEdgeManagerBehavior.ManagersFolderName}")?.Get(OvenEdgeManagerBehavior.GetManagerName(deviceId));
        var connected = manager?.GetVariable("Connection")?.Value.Value is bool value && value;
        var client = OvenMqttClientHelper.Find(deviceId);
        return connected && client != null && client.Status == Status.Connected;
    }

    private static bool Publish(string deviceId, RecipeMessage message)
    {
        var client = OvenMqttClientHelper.Find(deviceId);
        if (client == null || client.Status != Status.Connected) return false;

        var config = JsonSerializer.Serialize(new { Topic = $"recipes/{deviceId}/to-edge", QoS = 1, Retain = false });
        client.Publish(config, JsonSerializer.Serialize(message, Json));
        return true;
    }

    /// <summary>Adds the <c>recipes/&lt;id&gt;/to-supervisor</c> subscription to each oven client (needs a client restart to become active).</summary>
    private void EnsureSubscriptions()
    {
        try
        {
            foreach (var deviceId in Devices())
            {
                if (LocalFleetRuntime.IsLocal(deviceId)) continue;
                var client = OvenMqttClientHelper.Find(deviceId);
                if (client == null) continue;

                var subscriber = client.Get(SubscriberName) as MQTTSubscriber;
                if (subscriber == null) continue;

                var topic = $"recipes/{deviceId}/to-supervisor";
                var existing = subscriber.Get("Subscriptions")?.Get(SubscriptionName) as SubscriptionPlainText;
                if (existing != null && existing.Topic == topic && existing.Variable == inbox.NodeId) continue;

                if (existing == null)
                {
                    var s = InformationModel.Make<SubscriptionPlainText>(SubscriptionName);
                    subscriber.Subscriptions.Add(s);
                    s.Topic = topic;
                    s.QoS = QoSLevel.AtLeastOnce;
                    s.Variable = inbox.NodeId;
                }
                else
                {
                    existing.Topic = topic;
                    existing.QoS = QoSLevel.AtLeastOnce;
                    existing.Variable = inbox.NodeId;
                }
                OvenMqttClientHelper.Restart(client);
                Log.Info(Cat, $"Recipe subscription '{topic}' ready");
            }
        }
        catch (Exception ex)
        {
            Log.Error(Cat, $"Subscription setup failed: {ex.Message}");
        }
    }

    #endregion

    #region Recipe content

    private RecipeMessage BuildSendMessage(RecipeId id, out string error)
    {
        error = null;
        var raw = GetMeta(id, "Status");
        if (raw == null) { error = "Recipe not found"; return null; }

        var status = Convert.ToInt32(raw);
        if (status != StatusApproved && status != StatusDeprecated) { error = "Only Approved or Deprecated recipes can be sent"; return null; }

        var phases = ReadPhases(id);
        if (phases == null) { error = "Recipe values could not be read"; return null; }

        return new RecipeMessage
        {
            Name = id.Name, Version = id.Version, Status = status == StatusApproved ? "Approved" : "Deprecated",
            Author = Convert.ToString(GetMeta(id, "Author")) ?? string.Empty,
            Description = Convert.ToString(GetMeta(id, "Description")) ?? string.Empty,
            Phases = phases, Hash = ComputeHash(id.Name, id.Version, phases)
        };
    }

    private Dictionary<string, PhaseDto> ReadPhases(RecipeId id)
    {
        var phases = new Dictionary<string, PhaseDto>();
        foreach (var n in PhaseNames)
        {
            var sp = ReadValue(id, n, "TemperatureSetpoint");
            var du = ReadValue(id, n, "Duration");
            var fi = ReadValue(id, n, "FanInRamp");
            var fo = ReadValue(id, n, "FanOutRamp");
            var he = ReadValue(id, n, "Heating");
            if (sp == null || du == null || fi == null || fo == null || he == null) return null;
            phases[n] = new PhaseDto
            {
                TemperatureSetpoint = Convert.ToDouble(sp, CultureInfo.InvariantCulture), Duration = Convert.ToUInt32(du),
                FanInRamp = Convert.ToByte(fi), FanOutRamp = Convert.ToByte(fo), Heating = Convert.ToBoolean(he)
            };
        }
        return phases;
    }

    private string StoredHash(RecipeId id)
    {
        var phases = ReadPhases(id);
        return phases == null ? string.Empty : ComputeHash(id.Name, id.Version, phases);
    }

    // The default struct has null arrays, which the store rejects ("Invalid range type"): use empty ones.
    private static FTOptix.Core.ElementAccessStruct NoElementAccess() =>
        new FTOptix.Core.ElementAccessStruct { ArrayIndex = new uint[0], FieldIndexes = new FTOptix.Core.FieldIndexStruct[0] };

    private object ReadValue(RecipeId id, string phase, string field)
    {
        var r = schema.GetRecipeDataItemValue(id, new[] { phase }, new[] { field }, NoElementAccess());
        return r.ResultCode == GetRecipeDataItemValueResultCode.Success ? r.DataItemValue : null;
    }

    private object GetMeta(RecipeId id, string name)
    {
        var r = schema.GetRecipeMetadataValue(id, name);
        return r.ResultCode == GetRecipeMetadataValueResultCode.Success ? r.MetadataValue.Value : null;
    }

    private void SetMeta(RecipeId id, string name, object value)
    {
        var code = schema.SetRecipeMetadataValue(id, name, value);
        if (code != SetRecipeMetadataValueResultCode.Success) Log.Warning(Cat, $"Metadata '{name}' of {id.Name} v{id.Version}: {code}");
    }

    /// <summary>Canonical content hash shared with the oven (first 16 hex chars of SHA-256).</summary>
    private static string ComputeHash(string name, string version, Dictionary<string, PhaseDto> phases)
    {
        var sb = new StringBuilder($"{name}|{version}");
        foreach (var n in PhaseNames)
        {
            var p = phases[n];
            sb.Append('|').Append(p.TemperatureSetpoint.ToString("0.###", CultureInfo.InvariantCulture))
              .Append(',').Append(p.Duration).Append(',').Append(p.FanInRamp).Append(',').Append(p.FanOutRamp).Append(',').Append(p.Heating ? 1 : 0);
        }
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())).Take(8).Select(b => b.ToString("x2")));
    }

    #endregion

    #region Journal (SQLite)

    private void EnsureLink()
    {
        var model = Project.Current.Get("Model");
        var folder = model.Get(FolderName);
        if (folder == null)
        {
            folder = InformationModel.MakeObject<Folder>(FolderName);
            model.Add(folder);
        }
        inbox = folder.GetVariable("Inbox") ?? AddVariable(folder, "Inbox", OpcUa.DataTypes.String);
        revision = folder.GetVariable("Revision") ?? AddVariable(folder, "Revision", OpcUa.DataTypes.Int32);
    }

    private static IUAVariable AddVariable(IUANode folder, string name, NodeId dataType)
    {
        var v = InformationModel.MakeVariable(name, dataType);
        folder.Add(v);
        return v;
    }

    private void Bump()
    {
        lock (DbLock) revision.Value = Convert.ToInt32(revision.Value.Value) + 1;
    }

    private void EnsureTable()
    {
        lock (DbLock)
        {
            var store = GetStore();
            if (store.Tables.Get(TableName) == null) store.AddTable(TableName);

            var table = store.Tables.Get(TableName);
            EnsureColumn(table, "Id", OpcUa.DataTypes.Int64);
            EnsureColumn(table, "DeviceId", OpcUa.DataTypes.String);
            EnsureColumn(table, "Name", OpcUa.DataTypes.String);
            EnsureColumn(table, "Version", OpcUa.DataTypes.String);
            EnsureColumn(table, "Kind", OpcUa.DataTypes.Int32);
            EnsureColumn(table, "State", OpcUa.DataTypes.Int32);
            EnsureColumn(table, "Reason", OpcUa.DataTypes.String);
            EnsureColumn(table, "CommandId", OpcUa.DataTypes.String);
            EnsureColumn(table, "Hash", OpcUa.DataTypes.String);
            EnsureColumn(table, "UpdatedUtc", OpcUa.DataTypes.String);

            Query($"SELECT MAX(Id) FROM {TableName}", out var result);
            var max = result.GetLength(0) > 0 && result[0, 0] != null && !(result[0, 0] is DBNull) ? Convert.ToInt64(result[0, 0]) : 0;
            nextId = max + 1;
        }
    }

    private static void EnsureColumn(Table table, string name, NodeId dataType)
    {
        if (table.Columns.Get(name) == null) table.AddColumn(name, dataType);
    }

    private void Upsert(string deviceId, string name, string version, Kind kind, State state, string reason, string commandId, string hash)
    {
        lock (DbLock)
        {
            var where = $"DeviceId = '{Esc(deviceId)}' AND Name = '{Esc(name)}' AND Version = '{Esc(version)}' AND Kind = {(int)kind}";
            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            Query($"SELECT COUNT(*) FROM {TableName} WHERE {where}", out var count);
            if (count.GetLength(0) > 0 && Convert.ToInt64(count[0, 0]) > 0)
            {
                var set = $"State = {(int)state}, Reason = '{Esc(reason)}', UpdatedUtc = '{now}'";
                if (!string.IsNullOrEmpty(commandId)) set += $", CommandId = '{Esc(commandId)}'";
                if (!string.IsNullOrEmpty(hash)) set += $", Hash = '{Esc(hash)}'";
                Execute($"UPDATE {TableName} SET {set} WHERE {where}");
                return;
            }

            var columns = new[] { "Id", "DeviceId", "Name", "Version", "Kind", "State", "Reason", "CommandId", "Hash", "UpdatedUtc" };
            var values = new object[1, columns.Length];
            values[0, 0] = nextId++;
            values[0, 1] = deviceId;
            values[0, 2] = name;
            values[0, 3] = version;
            values[0, 4] = (int)kind;
            values[0, 5] = (int)state;
            values[0, 6] = reason ?? string.Empty;
            values[0, 7] = commandId ?? string.Empty;
            values[0, 8] = hash ?? string.Empty;
            values[0, 9] = now;
            GetStore().Insert(TableName, columns, values);
        }
    }

    private bool WasSent(string name, string version)
    {
        lock (DbLock)
        {
            Query($"SELECT COUNT(*) FROM {TableName} WHERE Name = '{Esc(name)}' AND Version = '{Esc(version)}' AND Kind = {(int)Kind.Send}", out var r);
            return r.GetLength(0) > 0 && Convert.ToInt64(r[0, 0]) > 0;
        }
    }

    private List<(string Name, string Version)> FleetCatalog()
    {
        lock (DbLock)
        {
            Query($"SELECT DISTINCT Name, Version FROM {TableName} WHERE Kind = {(int)Kind.Send}", out var r);
            var list = new List<(string, string)>();
            for (var i = 0; i < r.GetLength(0); i++) list.Add((Convert.ToString(r[i, 0]), Convert.ToString(r[i, 1])));
            return list;
        }
    }

    private static Store GetStore()
    {
        var store = Project.Current.Get<Store>($"DataStores/{StoreName}");
        if (store == null) throw new InvalidOperationException($"Store 'DataStores/{StoreName}' not found.");
        return store;
    }

    private static void Query(string sql, out object[,] result) => GetStore().Query(sql, out _, out result);
    private static void Execute(string sql) => GetStore().Query(sql, out _, out _);
    private static string Esc(string value) => (value ?? string.Empty).Replace("'", "''");

    #endregion

    #region Wire types

    private sealed class PhaseDto
    {
        public double TemperatureSetpoint { get; set; }
        public uint Duration { get; set; }
        public byte FanInRamp { get; set; }
        public byte FanOutRamp { get; set; }
        public bool Heating { get; set; }
    }

    private sealed class RecipeInfo
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string Hash { get; set; }
        public string Status { get; set; }
    }

    private sealed class RecipeMessage
    {
        public string Type { get; set; }
        public string DeviceId { get; set; }
        public string MsgId { get; set; }
        public string CommandId { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Hash { get; set; }
        public string Status { get; set; }
        public string Author { get; set; }
        public string Description { get; set; }
        public Dictionary<string, PhaseDto> Phases { get; set; }
        public string Result { get; set; }
        public string Reason { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public List<RecipeInfo> Recipes { get; set; }
    }

    #endregion
}
