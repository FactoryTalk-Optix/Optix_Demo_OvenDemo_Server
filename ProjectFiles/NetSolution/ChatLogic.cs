#region Using directives
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
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

/// <summary>
/// Supervisor side of the supervisor/oven chat.
/// </summary>
/// <remarks>
/// <para>
/// History lives in the <c>ChatMessages</c> table of <c>DataStores/EmbeddedDatabase</c>, created at runtime.
/// Messages travel as JSON on <c>chat/&lt;DeviceId&gt;/to-edge</c> (published through the oven's own
/// <c>Sup_&lt;DeviceId&gt;</c> client) and arrive through the <c>Chat</c> subscription of that client into
/// <c>Model/Chat/Inbox</c>.
/// </para>
/// <para>
/// Delivery is acknowledged by the application: a sent message is queued (<c>Status 0</c>), published when the oven
/// is reachable (<c>Status 1</c>) and delivered once the edge acks it (<c>Status 2</c>). Duplicates are discarded by
/// <c>MessageId</c>. Every change of the table increments <c>Model/Chat/Revision</c>, which the UI uses to refresh.
/// </para>
/// <para>See docs/OvenDemoNextGen_SupervisorEdgeChat_Design.md (sections 5 and 6).</para>
/// </remarks>
public class ChatLogic : BaseNetLogic
{
    private const string LogNode = "ChatLogic";
    private const string StoreName = "EmbeddedDatabase";
    private const string TableName = "ChatMessages";
    private const string InboxPath = "Model/Chat/Inbox";
    private const string RevisionPath = "Model/Chat/Revision";

    /// <summary>Path of the unread counter under an <c>OvenEdgeManager</c>.</summary>
    private const string UnreadCountPath = "Chat/UnreadCount";

    private const int MaxMessagesPerConversation = 500;
    private const int FlushPeriodMs = 2000;

    /// <summary>Values of the payload <c>Type</c> field.</summary>
    private const string TypeMessage = "msg";
    private const string TypeAck = "ack";

    /// <summary>Values of the <c>Direction</c> column, from the point of view of this node.</summary>
    private const int DirectionReceived = 0;
    private const int DirectionSent = 1;

    /// <summary>Values of the <c>Status</c> column (only meaningful for sent messages).</summary>
    private const int StatusQueued = 0;
    private const int StatusSent = 1;
    private const int StatusDelivered = 2;

    private static readonly object DbLock = new object();
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static long nextId;

    private IUAVariable inbox;
    private PeriodicTask flushTask;

    public override void Start()
    {
        try
        {
            EnsureTable();
            inbox = Project.Current.GetVariable(InboxPath);
            if (inbox == null)
            {
                Log.Error(LogNode, $"'{InboxPath}' not found, chat reception disabled.");
                return;
            }

            inbox.VariableChange += OnInboxChanged;
            flushTask = new PeriodicTask(FlushQueued, FlushPeriodMs, LogicObject);
            flushTask.Start();
            RefreshUnreadTotal();
            Log.Info(LogNode, $"Chat started, table '{TableName}' ready (next Id {nextId}).");
        }
        catch (Exception ex)
        {
            Log.Error(LogNode, $"Chat start failed: {ex.Message}");
        }
    }

    public override void Stop()
    {
        if (inbox != null)
            inbox.VariableChange -= OnInboxChanged;
        flushTask?.Dispose();
        flushTask = null;
    }

    /// <summary>
    /// Stores a message for an oven and publishes it right away if the oven is reachable; otherwise it stays queued
    /// until <see cref="FlushQueued"/> finds the oven reachable again. Called by the Send button.
    /// </summary>
    /// <param name="oven">The DeviceId, or the BrowseName of the oven's manager (the prefix is stripped).</param>
    /// <param name="text">The message text; empty or whitespace-only text is ignored.</param>
    [ExportMethod]
    public void SendMessage(string oven, string text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        var deviceId = OvenEdgeManagerBehavior.GetDeviceId(oven ?? string.Empty);
        var manager = OvenMqttClientHelper.IsValidDeviceId(deviceId) ? FindManager(deviceId) : null;
        if (manager == null)
        {
            Log.Warning(LogNode, $"SendMessage: no oven '{oven}' found.");
            return;
        }
        if (LocalFleetRuntime.IsLocalManager(manager))
        {
            Log.Warning(LogNode, "Chat is available only for remote edges.");
            return;
        }

        var messageId = Guid.NewGuid().ToString("D");
        var timestamp = DateTime.UtcNow;
        try
        {
            lock (DbLock)
            {
                InsertRow(deviceId, messageId, DirectionSent, text, timestamp, StatusQueued);
                TrimConversation(deviceId);
                if (IsReachable(manager, deviceId) && PublishMessage(deviceId, messageId, text, timestamp))
                    SetStatus(messageId, deviceId, StatusSent);
            }
            BumpRevision();
        }
        catch (Exception ex)
        {
            Log.Error(LogNode, $"SendMessage to '{deviceId}' failed: {ex.Message}");
        }
    }

    /// <summary>Deletes the whole history of an oven. Called when the oven is removed from the fleet.</summary>
    internal static void DeleteConversation(string deviceId)
    {
        try
        {
            lock (DbLock)
                Execute($"DELETE FROM {TableName} WHERE DeviceId = '{EscapeLiteral(deviceId)}'");
            BumpRevision();
            RefreshUnreadTotal();
            Log.Info(LogNode, $"Deleted chat history of '{deviceId}'.");
        }
        catch (Exception ex)
        {
            Log.Warning(LogNode, $"Could not delete chat history of '{deviceId}': {ex.Message}");
        }
    }

    /// <summary>
    /// Handles a JSON payload written into <c>Model/Chat/Inbox</c> by one of the <c>Chat</c> subscriptions.
    /// Payloads that are not valid JSON, have an invalid DeviceId/Id, or come from an unknown oven are logged and discarded.
    /// </summary>
    private void OnInboxChanged(object sender, VariableChangeEventArgs e)
    {
        var json = (string)e.NewValue;
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            var payload = JsonSerializer.Deserialize<ChatPayload>(json);
            if (payload == null || !OvenMqttClientHelper.IsValidDeviceId(payload.DeviceId) || !Guid.TryParse(payload.Id, out _))
            {
                Log.Warning(LogNode, $"Discarded chat payload with missing or invalid DeviceId/Id: {json}");
                return;
            }

            var manager = FindManager(payload.DeviceId);
            if (manager == null)
            {
                Log.Warning(LogNode, $"Discarded chat payload from unknown device '{payload.DeviceId}'.");
                return;
            }

            if (payload.Type == TypeAck)
                HandleAck(payload);
            else if (payload.Type == TypeMessage)
                HandleMessage(manager, payload);
            else
                Log.Warning(LogNode, $"Discarded chat payload with unknown Type '{payload.Type}'.");
        }
        catch (JsonException ex)
        {
            Log.Warning(LogNode, $"Discarded chat payload that is not valid JSON ({ex.Message}): {json}");
        }
        catch (Exception ex)
        {
            Log.Error(LogNode, $"Chat reception failed: {ex.Message}");
        }
    }

    /// <summary>Marks a sent message as delivered.</summary>
    private void HandleAck(ChatPayload payload)
    {
        lock (DbLock)
            SetStatus(Guid.Parse(payload.Id).ToString("D"), payload.DeviceId, StatusDelivered);
        BumpRevision();
    }

    /// <summary>Stores a received message, acknowledges it and increments the oven's unread counter.</summary>
    private void HandleMessage(IUANode manager, ChatPayload payload)
    {
        var messageId = Guid.Parse(payload.Id).ToString("D");
        var isNew = false;
        lock (DbLock)
        {
            if (!RowExists(messageId))
            {
                InsertRow(payload.DeviceId, messageId, DirectionReceived, payload.Text ?? string.Empty, ParseTimestampUtc(payload.Timestamp), StatusQueued);
                TrimConversation(payload.DeviceId);
                isNew = true;
            }
        }

        // A duplicate (QoS 1 redelivery) only regenerates the ack.
        PublishAck(payload.DeviceId, messageId);
        if (isNew)
            IncrementUnread(manager);
        BumpRevision();
    }

    /// <summary>Publishes the queued messages of every oven that is reachable again (runs periodically).</summary>
    private void FlushQueued()
    {
        try
        {
            var flushed = false;
            lock (DbLock)
            {
                Query($"SELECT DISTINCT DeviceId FROM {TableName} WHERE Direction = {DirectionSent} AND Status = {StatusQueued}", out var devices);
                for (var i = 0; i < devices.GetLength(0); i++)
                {
                    var deviceId = Convert.ToString(devices[i, 0]);
                    var manager = FindManager(deviceId);
                    if (manager == null || !IsReachable(manager, deviceId))
                        continue;

                    Query($"SELECT MessageId, Text, Timestamp FROM {TableName} WHERE DeviceId = '{EscapeLiteral(deviceId)}' AND Direction = {DirectionSent} AND Status = {StatusQueued} ORDER BY Id", out var rows);
                    for (var r = 0; r < rows.GetLength(0); r++)
                    {
                        var messageId = Convert.ToString(rows[r, 0]);
                        var timestamp = ParseTimestampUtc(Convert.ToString(rows[r, 2], CultureInfo.InvariantCulture));
                        if (!PublishMessage(deviceId, messageId, Convert.ToString(rows[r, 1]), timestamp))
                            break;

                        SetStatus(messageId, deviceId, StatusSent);
                        flushed = true;
                    }
                }
            }

            if (flushed)
                BumpRevision();
        }
        catch (Exception ex)
        {
            Log.Warning(LogNode, $"Flush of queued chat messages failed: {ex.Message}");
        }
    }

    /// <summary>Parses an ISO 8601 timestamp to UTC; falls back to the current time when it is missing or invalid.</summary>
    private static DateTime ParseTimestampUtc(string value)
    {
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.UtcNow;
    }

    /// <summary>
    /// An oven is reachable when its manager reports <c>Connection</c> (fresh telemetry) and the supervisor's
    /// client for that oven is connected to the broker.
    /// </summary>
    private static bool IsReachable(IUANode manager, string deviceId)
    {
        var connected = manager.GetVariable("Connection")?.Value.Value is bool value && value;
        var client = OvenMqttClientHelper.Find(deviceId);
        return connected && client != null && client.Status == Status.Connected;
    }

    private static IUANode FindManager(string deviceId)
    {
        return Project.Current.Get($"Model/{OvenEdgeManagerBehavior.ManagersFolderName}")?.Get(OvenEdgeManagerBehavior.GetManagerName(deviceId));
    }

    private static bool PublishMessage(string deviceId, string messageId, string text, DateTime timestamp)
    {
        return Publish(deviceId, new ChatPayload
        {
            Type = TypeMessage,
            DeviceId = deviceId,
            Id = messageId,
            Text = text,
            Timestamp = timestamp.ToString("o", CultureInfo.InvariantCulture)
        });
    }

    private static void PublishAck(string deviceId, string messageId)
    {
        Publish(deviceId, new ChatPayload { Type = TypeAck, DeviceId = deviceId, Id = messageId });
    }

    /// <summary>Publishes a payload to the oven (QoS 1, not retained); returns false when its client is not connected.</summary>
    private static bool Publish(string deviceId, ChatPayload payload)
    {
        var client = OvenMqttClientHelper.Find(deviceId);
        if (client == null || client.Status != Status.Connected)
            return false;

        client.Publish(JsonSerializer.Serialize(new PublishConfig($"chat/{deviceId}/to-edge")), JsonSerializer.Serialize(payload, JsonOptions));
        return true;
    }

    /// <summary>
    /// Increments the unread counter on the manager's <c>Chat</c> group. Managers restored from retentivity before
    /// the group existed do not have it and need to be accepted again.
    /// </summary>
    private static void IncrementUnread(IUANode manager)
    {
        var unread = manager.GetVariable(UnreadCountPath);
        if (unread == null)
        {
            Log.Warning(LogNode, $"'{manager.BrowseName}' has no '{UnreadCountPath}': re-accept the oven to get the Chat group.");
            return;
        }

        lock (DbLock)
            unread.Value = Convert.ToInt32(unread.Value.Value) + 1;
        RefreshUnreadTotal();
    }

    /// <summary>Zeroes the unread counter of an oven whose conversation is on screen.</summary>
    internal static void MarkRead(IUANode manager)
    {
        var unread = manager?.GetVariable(UnreadCountPath);
        if (unread == null || Convert.ToInt32(unread.Value.Value) == 0)
            return;

        lock (DbLock)
            unread.Value = 0;
        RefreshUnreadTotal();
    }

    /// <summary>
    /// Writes the unread messages of all ovens to <c>Model/Chat/UnreadCount</c> and <c>UnreadAvailable</c>,
    /// which drive the notification of the Chat menu entry.
    /// </summary>
    internal static void RefreshUnreadTotal()
    {
        try
        {
            var chat = Project.Current.Get("Model/Chat");
            var managers = Project.Current.Get($"Model/{OvenEdgeManagerBehavior.ManagersFolderName}");
            if (chat == null || managers == null)
                return;

            var total = 0;
            foreach (var manager in managers.Children)
                total += Convert.ToInt32(manager.GetVariable(UnreadCountPath)?.Value.Value ?? 0);

            lock (DbLock)
            {
                FleetAnalyticsCollector.EnsureVariable(chat, "UnreadCount", OpcUa.DataTypes.Int32).Value = total;
                FleetAnalyticsCollector.EnsureVariable(chat, "UnreadAvailable", OpcUa.DataTypes.Boolean).Value = total > 0;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(LogNode, $"Could not refresh the unread total: {ex.Message}");
        }
    }

    /// <summary>Signals to the UI that the history changed (the ListViews refresh on this variable).</summary>
    private static void BumpRevision()
    {
        var revision = Project.Current.GetVariable(RevisionPath);
        if (revision == null)
            return;

        lock (DbLock)
            revision.Value = Convert.ToInt32(revision.Value.Value) + 1;
    }

    #region Store access

    private static Store GetStore()
    {
        var store = Project.Current.Get<Store>($"DataStores/{StoreName}");
        if (store == null)
            throw new InvalidOperationException($"Store 'DataStores/{StoreName}' not found.");
        return store;
    }

    /// <summary>
    /// Creates the table and its columns if missing and reads the last used <c>Id</c>. The table cannot be created at
    /// design time (AddTable is a runtime-only method) and the code assigns the <c>Id</c> itself: this logic is the
    /// only writer of the table.
    /// </summary>
    private static void EnsureTable()
    {
        lock (DbLock)
        {
            var store = GetStore();
            if (store.Tables.Get(TableName) == null)
            {
                store.AddTable(TableName);
                Log.Info(LogNode, $"Created table '{TableName}'.");
            }

            var table = store.Tables.Get(TableName);
            EnsureColumn(table, "Id", OpcUa.DataTypes.Int64);
            EnsureColumn(table, "MessageId", OpcUa.DataTypes.String);
            EnsureColumn(table, "DeviceId", OpcUa.DataTypes.String);
            EnsureColumn(table, "Direction", OpcUa.DataTypes.Int32);
            EnsureColumn(table, "Text", OpcUa.DataTypes.String);
            EnsureColumn(table, "Timestamp", OpcUa.DataTypes.DateTime);
            EnsureColumn(table, "Status", OpcUa.DataTypes.Int32);

            Query($"SELECT MAX(Id) FROM {TableName}", out var result);
            var max = result.GetLength(0) > 0 && result[0, 0] != null && !(result[0, 0] is DBNull) ? Convert.ToInt64(result[0, 0]) : 0;
            nextId = max + 1;
        }
    }

    private static void EnsureColumn(Table table, string name, NodeId dataType)
    {
        if (table.Columns.Get(name) != null)
            return;

        table.AddColumn(name, dataType);
        Log.Info(LogNode, $"Added column '{name}' to '{table.BrowseName}'.");
    }

    private static void InsertRow(string deviceId, string messageId, int direction, string text, DateTime timestamp, int status)
    {
        var columns = new[] { "Id", "MessageId", "DeviceId", "Direction", "Text", "Timestamp", "Status" };
        var values = new object[1, columns.Length];
        values[0, 0] = nextId++;
        values[0, 1] = messageId;
        values[0, 2] = deviceId;
        values[0, 3] = direction;
        values[0, 4] = text;
        values[0, 5] = timestamp;
        values[0, 6] = status;
        GetStore().Insert(TableName, columns, values);
    }

    /// <summary>Keeps only the newest <see cref="MaxMessagesPerConversation"/> rows of the conversation.</summary>
    private static void TrimConversation(string deviceId)
    {
        var device = EscapeLiteral(deviceId);
        Execute($"DELETE FROM {TableName} WHERE DeviceId = '{device}' AND Id <= (SELECT Id FROM {TableName} WHERE DeviceId = '{device}' ORDER BY Id DESC LIMIT 1 OFFSET {MaxMessagesPerConversation})");
    }

    /// <summary>Sets the status of a sent message.</summary>
    private static void SetStatus(string messageId, string deviceId, int status)
    {
        Execute($"UPDATE {TableName} SET Status = {status} WHERE MessageId = '{EscapeLiteral(messageId)}' AND DeviceId = '{EscapeLiteral(deviceId)}' AND Direction = {DirectionSent}");
    }

    private static bool RowExists(string messageId)
    {
        Query($"SELECT COUNT(*) FROM {TableName} WHERE MessageId = '{EscapeLiteral(messageId)}'", out var result);
        return result.GetLength(0) > 0 && Convert.ToInt64(result[0, 0]) > 0;
    }

    private static void Query(string sql, out object[,] result)
    {
        GetStore().Query(sql, out _, out result);
    }

    private static void Execute(string sql)
    {
        GetStore().Query(sql, out _, out _);
    }

    private static string EscapeLiteral(string value) => (value ?? string.Empty).Replace("'", "''");

    #endregion

    /// <summary>Chat message or acknowledgement; the same JSON travels in both directions. Text and Timestamp are omitted for acks.</summary>
    private sealed class ChatPayload
    {
        public string Type { get; set; }
        public string DeviceId { get; set; }
        public string Id { get; set; }
        public string Text { get; set; }
        public string Timestamp { get; set; }
    }

    /// <summary>JSON config expected as first argument of <c>MQTTClient.Publish</c> (Topic/QoS/Retain).</summary>
    private sealed class PublishConfig
    {
        public string Topic { get; }
        public int QoS { get; } = 1;
        public bool Retain { get; } = false;

        public PublishConfig(string topic)
        {
            Topic = topic;
        }
    }
}
