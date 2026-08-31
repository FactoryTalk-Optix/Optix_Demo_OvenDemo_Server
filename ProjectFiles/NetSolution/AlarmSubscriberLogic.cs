#region Using directives
using System;
using System.Collections.Generic;
using CoreBase = FTOptix.CoreBase;
using FTOptix.HMIProject;
using UAManagedCore;
using System.Linq;
using UAManagedCore.Logging;
using FTOptix.NetLogic;
using FTOptix.Core;
using FTOptix.Alarm;
#endregion

public class AlarmSubscriberLogic : BaseNetLogic
{
    public override void Start()
    {
        var context = LogicObject.Context;
        affinityId = context.AssignAffinityId();

        RegisterObserverOnLocalizedAlarmsContainer(context);
        RegisterObserverOnSessionLocaleIdChanged(context);
        RegisterObserverOnLocalizedAlarmsObject(context);
    }

    public override void Stop()
    {
        alarmEventRegistration?.Dispose();
        alarmEventRegistration2?.Dispose();
        localeIdRegistration?.Dispose();

        alarmEventRegistration = null;
        alarmEventRegistration2 = null;
        localeIdRegistration = null;
        alarmsNotificationObserver = null;
        retainedAlarmsObjectObserver = null;
    }

    /// <summary>
    /// Registers an observer on the localized alarms container for events related to forward reference additions.
    /// <example>
    /// For example:
    /// <code>
    /// RegisterObserverOnLocalizedAlarmsObject(new Context());
    /// </code>
    /// results in registering the observer for all contexts.
    /// </example>
    /// </summary>
    /// <param name="context">The context to register the observer on.</param>
    public void RegisterObserverOnLocalizedAlarmsObject(IContext context)
    {
        var retainedAlarms = context.GetNode(FTOptix.Alarm.Objects.RetainedAlarms);
        if(retainedAlarms == null)
        {
            Log.Info("AlarmNotificationLogic", "Missing retained alarms");
            return;
        }

        retainedAlarmsObjectObserver = new RetainedAlarmsObjectObserver((ctx) => RegisterObserverOnLocalizedAlarmsContainer(ctx));

        // observe ReferenceAdded of localized alarm containers
        alarmEventRegistration2 = retainedAlarms.RegisterEventObserver(
            retainedAlarmsObjectObserver, EventType.ForwardReferenceAdded, affinityId);
    }

    /// <summary>
    /// Registers an observer on the localized alarms container for event notifications.
    /// If the localized alarms are missing or empty, it logs an info message and returns.
    /// Otherwise, it initializes the alarms notification observer and registers it as an observer on the localized alarms container with specified event types and affinity ID.
    /// </summary>
    /// <param name="context">The context object containing node and variable references.</param>
    public void RegisterObserverOnLocalizedAlarmsContainer(IContext context)
    {
        var retainedAlarms = context.GetNode(FTOptix.Alarm.Objects.RetainedAlarms);
        if(retainedAlarms == null)
        {
            Log.Info("AlarmNotificationLogic", "Missing retained alarms");
            return;
        }
        var localizedAlarmsVariable = retainedAlarms.GetVariable("LocalizedAlarms");

        if (localizedAlarmsVariable.Value == null)
            return;

        var localizedAlarmsNodeId = (NodeId)localizedAlarmsVariable.Value;
        IUANode localizedAlarmsContainer = null;
        if (!localizedAlarmsNodeId.IsEmpty)
            localizedAlarmsContainer = context.GetNode(localizedAlarmsNodeId);

        if (alarmEventRegistration != null)
        {
            alarmEventRegistration.Dispose();
            alarmEventRegistration = null;
        }

        alarmsNotificationObserver = new AlarmsNotificationObserver(LogicObject);
        alarmsNotificationObserver.Initialize();

        alarmEventRegistration = localizedAlarmsContainer?.RegisterEventObserver(
            alarmsNotificationObserver,
            EventType.ForwardReferenceAdded | EventType.ForwardReferenceRemoved, affinityId);
    }

    /// <summary>
    /// Registers an observer on the Session Locale Id changed event.
    /// When this event occurs, it triggers the registration of an observer for Localized Alarms Container.
    /// </summary>
    /// <param name="context">The context object containing session information.</param>
    /// <remarks>
    /// The method retrieves the current session's Locale ID from the Context and then registers an observer that listens for Variable Value Changed events.
    /// Whenever there is a change in the Locale ID, it calls the RegisterObserverOnLocalizedAlarmsContainer method to register another observer for Localized Alarms Container.
    /// </remarks>
    public void RegisterObserverOnSessionLocaleIdChanged(IContext context)
    {
        var currentSessionLocaleId = context.Sessions.CurrentSessionInfo.SessionObject.Children["ActualLocaleId"];

        localeIdChangedObserver = new CallbackVariableChangeObserver((IUAVariable variable, UAValue newValue, UAValue oldValue, ElementAccess a, ulong aa) =>
        {
            RegisterObserverOnLocalizedAlarmsContainer(context);
        });

        localeIdRegistration = currentSessionLocaleId.RegisterEventObserver(
            localeIdChangedObserver, EventType.VariableValueChanged, affinityId);
    }

    private class RetainedAlarmsObjectObserver : IReferenceObserver
    {
        /// <summary>
        /// Initializes a new instance of the RetainedAlarmsObjectObserver class with the provided action callback.
        /// <example>
        /// For example:
        /// <code>
        /// RetainedAlarmsObjectObserver observer = new RetainedAlarmsObjectObserver(myAction);
        /// </code>
        /// where `myAction` is an implementation of Action<IContext>.
        /// </example>
        /// </summary>
        /// <param name="action">An Action delegate that will be called when the retained alarms object changes.</param>
        /// <returns>
        /// The newly created RetainedAlarmsObjectObserver instance.
        /// </returns>
        public RetainedAlarmsObjectObserver(Action<IContext> action)
        {
            registrationCallback = action;
        }

        /// <summary>
        /// This method handles the addition of a reference node by checking its Locale ID and calling a registration callback function.
        /// If no Locale ID is provided, it defaults to 'en-US'.
        /// </summary>
        /// <param name="sourceNode">The originating node.</param>
        /// <param name="targetNode">The target node with the added reference.</param>
        /// <param name="referenceTypeId">The type identifier for the reference.</param>
        /// <param name="senderId">The sender's unique identifier.</param>
        /// </param>
        public void OnReferenceAdded(IUANode sourceNode, IUANode targetNode, NodeId referenceTypeId, ulong senderId)
        {
            var localeId = targetNode.Context.Sessions.CurrentSessionHandler.ActualLocaleId;
            if (String.IsNullOrEmpty(localeId))
                localeId = "en-US";

            if (targetNode.BrowseName == localeId)
                registrationCallback(targetNode.Context);
        }

        /// <summary>
        /// This method is triggered when a reference type node is removed from the graph.
        /// It receives information about the node that was referenced (source), the node it was referenced by (target),
        /// the type ID of the reference, and the sender's identifier.
        /// </summary>
        /// <param name="sourceNode">The node that was previously referenced.</param>
        /// <param name="targetNode">The node that no longer references the previous node.</param>
        /// <param name="referenceTypeId">The type ID of the reference being removed.</param>
        /// <param name="senderId">The identifier of the entity sending the notification.</param>
        /// </remarks>
        public void OnReferenceRemoved(IUANode sourceNode, IUANode targetNode, NodeId referenceTypeId, ulong senderId)
        {
        }

        private Action<IContext> registrationCallback;
    }

    private class AlarmsNotificationObserver : IReferenceObserver
    {
        /// <summary>
        /// Initializes a new instance of the AlarmsNotificationObserver class with the specified logic node.
        /// <example>
        /// For example:
        /// <code>
        /// var observer = new AlarmsNotificationObserver(new Node());
        /// </code>
        /// creates an observer that uses the given Node as its logic node.
        /// </example>
        /// </summary>
        /// <param name="logicNode">The logic node for the notification observer.</param>
        /// <returns>
        /// The newly created AlarmsNotificationObserver instance.
        /// </returns>
        /// <remarks>
        /// This constructor initializes the logic node property of the observer.
        /// </remarks>
        public AlarmsNotificationObserver(IUANode logicNode)
        {
            this.logicNode = logicNode;
        }

        /// <summary>
        /// Initializes the alarm notification logic by retrieving variables from the context node.
        /// If the 'LocalizedAlarms' variable cannot be obtained, an error log is triggered.
        /// The last alarm node ID and count are updated based on the available data.
        /// </summary>
        /// <remarks>
        /// Retrieves the current alarm count and last alarm node ID from the context node.
        /// Checks for the existence of the 'LocalizedAlarms' variable and retrieves its value.
        /// Updates the last alarm node ID and alarm count accordingly.
        /// </remarks>
        public void Initialize()
        {
            retainedAlarmsCount = logicNode.GetVariable("AlarmCount");
            alarmCountSeverity1 = logicNode.GetVariable("AlarmCountSeverity1");
            alarmCountSeverity2 = logicNode.GetVariable("AlarmCountSeverity2");
            alarmCountSeverity3 = logicNode.GetVariable("AlarmCountSeverity3");
            lastAlarm = logicNode.GetVariable("LastAlarm");

            IContext context = logicNode.Context;
            var retainedAlarms = context.GetNode(FTOptix.Alarm.Objects.RetainedAlarms);
            var localizedAlarmsVariable = retainedAlarms.GetVariable("LocalizedAlarms");

            if (localizedAlarmsVariable.Value == null)
            {
                Log.Error("AlarmNotificationLogic", "Unable to obtain localized alarms variable");
                return;
            }

            var localizedAlarmsNodeId = (NodeId)localizedAlarmsVariable.Value;
            IUANode localizedAlarmsContainer = null;
            if (!localizedAlarmsNodeId.IsEmpty)
                localizedAlarmsContainer = context.GetNode(localizedAlarmsNodeId);

            RefreshCounters(localizedAlarmsContainer);
        }

        /// <summary>
        /// This method handles the addition of a reference node by checking its Locale ID and calling a registration callback function.
        /// If no Locale ID is provided, it defaults to 'en-US'.
        /// </summary>
        /// <param name="sourceNode">The originating node.</param>
        /// <param name="targetNode">The target node with the added reference.</param>
        /// <param name="referenceTypeId">The type identifier for the reference.</param>
        /// <param name="senderId">The sender's unique identifier.</param>
        /// </param>
        public void OnReferenceAdded(IUANode sourceNode, IUANode targetNode, NodeId referenceTypeId, ulong senderId)
        {
            PurgeStaleAlarms(sourceNode);

            var incomingOvenId = GetOvenId(targetNode);
            if (string.IsNullOrWhiteSpace(incomingOvenId))
                return;

            if (!string.IsNullOrWhiteSpace(lastProcessedOvenId) &&
                !string.Equals(lastProcessedOvenId, incomingOvenId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lastProcessedOvenId = incomingOvenId;
            RefreshCounters(sourceNode, targetNode, lastProcessedOvenId);
        }

        /// <summary>
        /// This method is triggered when a reference type node is removed from the graph.
        /// It receives information about the node that was referenced (source), the node it was referenced by (target),
        /// the type ID of the reference, and the sender's identifier.
        /// </summary>
        /// <param name="sourceNode">The node that was previously referenced.</param>
        /// <param name="targetNode">The node that no longer references the previous node.</param>
        /// <param name="referenceTypeId">The type ID of the reference being removed.</param>
        /// <param name="senderId">The identifier of the entity sending the notification.</param>
        public void OnReferenceRemoved(IUANode sourceNode, IUANode targetNode, NodeId referenceTypeId, ulong senderId)
        {
            IContext context = logicNode.Context;
            var retainedAlarms = context.GetNode(FTOptix.Alarm.Objects.RetainedAlarms);
            var localizedAlarmsVariable = retainedAlarms.GetVariable("LocalizedAlarms");
            var localizedAlarmsNodeId = (NodeId)localizedAlarmsVariable.Value;
            IUANode localizedAlarmsContainer = null;
            if (localizedAlarmsNodeId != null && !localizedAlarmsNodeId.IsEmpty)
                localizedAlarmsContainer = context.GetNode(localizedAlarmsNodeId);

            PurgeStaleAlarms(localizedAlarmsContainer);

            if (string.IsNullOrWhiteSpace(lastProcessedOvenId))
            {
                RefreshCounters(localizedAlarmsContainer);
                return;
            }

            var removedOvenId = GetOvenId(targetNode);
            if (!string.IsNullOrWhiteSpace(removedOvenId) &&
                !string.Equals(lastProcessedOvenId, removedOvenId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            RefreshCounters(localizedAlarmsContainer, null, lastProcessedOvenId);
        }

        private void RefreshCounters(IUANode localizedAlarmsContainer, IUANode newLastAlarmNode = null, string ovenIdFilter = null)
        {
            try
            {
                int severity1 = 0;
                int severity2 = 0;
                int severity3 = 0;

                var children = localizedAlarmsContainer?.Children;
                IEnumerable<IUANode> filteredChildren = children;
                if (children != null && !string.IsNullOrWhiteSpace(ovenIdFilter))
                    filteredChildren = children.Where(alarmNode => string.Equals(GetOvenId(alarmNode), ovenIdFilter, StringComparison.OrdinalIgnoreCase));

                if (filteredChildren != null)
                {
                    foreach (var alarmNode in filteredChildren)
                    {
                        var severity = GetSeverity(alarmNode);
                        if (severity == 1)
                            severity1++;
                        else if (severity == 2)
                            severity2++;
                        else if (severity == 3)
                            severity3++;
                    }
                }

                var alarmsList = filteredChildren?.ToList() ?? new List<IUANode>();

                retainedAlarmsCount.Value = alarmsList.Count;
                if (alarmCountSeverity1 != null)
                    alarmCountSeverity1.Value = severity1;
                if (alarmCountSeverity2 != null)
                    alarmCountSeverity2.Value = severity2;
                if (alarmCountSeverity3 != null)
                    alarmCountSeverity3.Value = severity3;

                if (newLastAlarmNode != null)
                {
                    var newLastAlarmOvenId = GetOvenId(newLastAlarmNode);
                    if (string.IsNullOrWhiteSpace(ovenIdFilter) || string.Equals(newLastAlarmOvenId, ovenIdFilter, StringComparison.OrdinalIgnoreCase))
                        lastAlarm.Value = newLastAlarmNode.NodeId;
                    else
                        lastAlarm.Value = alarmsList.LastOrDefault()?.NodeId ?? NodeId.Empty;
                }
                else
                {
                    lastAlarm.Value = alarmsList.LastOrDefault()?.NodeId ?? NodeId.Empty;
                }
            }
            catch (Exception)
            {
                lastAlarm.Value = NodeId.Empty;
                retainedAlarmsCount.Value = 0;
                if (alarmCountSeverity1 != null)
                    alarmCountSeverity1.Value = 0;
                if (alarmCountSeverity2 != null)
                    alarmCountSeverity2.Value = 0;
                if (alarmCountSeverity3 != null)
                    alarmCountSeverity3.Value = 0;
            }
        }

        private int GetSeverity(IUANode alarmNode)
        {
            try
            {
                var originalAlarm = InformationModel.Get<AlarmController>(alarmNode.GetVariable("ConditionId").Value) ?? throw new System.ArgumentException("Alarm not found");
                return originalAlarm.Severity;
            }
            catch (Exception ex)
            {
                Log.Error("AlarmNotificationLogic", $"Unable to get severity for alarm node {alarmNode.NodeId}: {ex.Message}");
                return 0;
            }
        }

        private void PurgeStaleAlarms(IUANode localizedAlarmsContainer)
        {
            var children = localizedAlarmsContainer?.Children;
            if (children == null)
                return;

            var nowUtc = DateTime.UtcNow;
            var staleNodes = children
                .Where(alarmNode => IsAlarmOlderThanRetention(alarmNode, nowUtc))
                .ToList();

            foreach (var staleNode in staleNodes)
            {
                try
                {
                    staleNode.Delete();
                }
                catch (Exception ex)
                {
                    Log.Warning("AlarmNotificationLogic", $"Unable to delete stale alarm {staleNode.NodeId}: {ex.Message}");
                }
            }
        }

        private bool IsAlarmOlderThanRetention(IUANode alarmNode, DateTime nowUtc)
        {
            if (!TryGetAlarmTimestampUtc(alarmNode, out var timestampUtc))
                return false;

            return (nowUtc - timestampUtc) > staleAlarmRetention;
        }

        private bool TryGetAlarmTimestampUtc(IUANode alarmNode, out DateTime timestampUtc)
        {
            timestampUtc = DateTime.MinValue;

            var timestampVariable = alarmNode?.GetVariable("Timestamp") ?? alarmNode?.GetVariable("Time");
            if (timestampVariable?.Value == null)
                return false;

            DateTime timestamp;
            try
            {
                timestamp = (DateTime)timestampVariable.Value;
            }
            catch
            {
                return false;
            }

            if (timestamp == DateTime.MinValue)
                return false;

            if (timestamp.Kind == DateTimeKind.Utc)
                timestampUtc = timestamp;
            else if (timestamp.Kind == DateTimeKind.Local)
                timestampUtc = timestamp.ToUniversalTime();
            else
                timestampUtc = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);

            return true;
        }

        private string GetOvenId(IUANode alarmNode)
        {
            try
            {
                var ovenIdVariable = alarmNode?.GetVariable("OvenId");
                return ovenIdVariable?.Value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private IUAVariable retainedAlarmsCount;
        private IUAVariable alarmCountSeverity1;
        private IUAVariable alarmCountSeverity2;
        private IUAVariable alarmCountSeverity3;
        private IUAVariable lastAlarm;
        private IUANode logicNode;
        private string lastProcessedOvenId;
        private static readonly TimeSpan staleAlarmRetention = TimeSpan.FromSeconds(10);
    }

    private uint affinityId = 0;
    private AlarmsNotificationObserver alarmsNotificationObserver;
    private RetainedAlarmsObjectObserver retainedAlarmsObjectObserver;
    private IEventRegistration alarmEventRegistration;
    private IEventRegistration alarmEventRegistration2;
    private IEventRegistration localeIdRegistration;
    private IEventObserver localeIdChangedObserver;
}
