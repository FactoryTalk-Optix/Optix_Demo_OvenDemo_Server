#region Using directives
using System;
using System.Linq;
using System.Text.Json;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NativeUI;
using FTOptix.WebUI;
using FTOptix.RecipeX;
using FTOptix.Alarm;
using FTOptix.DataLogger;
using FTOptix.EventLogger;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.OPCUAServer;
using FTOptix.MQTTBroker;
using FTOptix.MQTTClient;
using FTOptix.NetLogic;
using FTOptix.CoreBase;
using FTOptix.Core;
#endregion

public class IncomingMessagesLogic : BaseNetLogic
{
    private IUAVariable incomingMessagesVariable;

    private DialogType notificationDialogType;

    public override void Start()
    {
        notificationDialogType = Project.Current.Get<DialogType>("UI/Types/Dialogs/NotificationDialog");
        if (notificationDialogType == null)
        {
            Log.Warning("IncomingMessagesLogic", "UI/Types/Dialogs/NotificationDialog not found");
            return;
        }

        // Subscribe to changes on the IncomingMessages variable
        incomingMessagesVariable = Project.Current.GetVariable("Model/IncomingMessages");
        if (incomingMessagesVariable == null)
        {
            Log.Warning("IncomingMessagesLogic", "Model/IncomingMessages variable not found");
            return;
        }

        incomingMessagesVariable.VariableChange += OnIncomingMessageChanged;
    }

    public override void Stop()
    {
        // Unsubscribe from the event
        if (incomingMessagesVariable != null)
        {
            incomingMessagesVariable.VariableChange -= OnIncomingMessageChanged;
        }
    }

    private void OnIncomingMessageChanged(object sender, VariableChangeEventArgs e)
    {
        try
        {
            var messageJson = e.NewValue?.Value?.ToString();
            if (string.IsNullOrEmpty(messageJson))
                return;

            // Parse the incoming message
            var payload = JsonSerializer.Deserialize<SendMessageToOvenLogic.OvenMessagePayload>(messageJson);
            if (payload == null || string.IsNullOrEmpty(payload.OvenId))
            {
                Log.Warning("IncomingMessagesLogic", "Invalid message payload received");
                return;
            }

            // Check if the OvenId matches any oven in Model/Ovens starting with "OptixOven"
            var ovensFolder = Project.Current.Get<Folder>("Model/Ovens");
            if (ovensFolder == null)
            {
                Log.Warning("IncomingMessagesLogic", "Model/Ovens folder not found");
                return;
            }

            //var matchingOven = ovensFolder.Children
            //    .FirstOrDefault(oven => oven.BrowseName.StartsWith("OptixOven") && oven.GetVariable("OvenId").Value == payload.OvenId);

            var matchingOven = ovensFolder.Children
                .FirstOrDefault(oven => oven.GetVariable("OvenId").Value == payload.OvenId);

            if (matchingOven == null)
            {
                matchingOven = ovensFolder.Children
                .FirstOrDefault(oven => oven.GetVariable("OvenId").Value == payload.Source);
            }

            if (matchingOven.BrowseName.StartsWith("OptixOven") || payload.OvenId == "Server")
            {
                // Open the notification dialog and display the message, passing the oven NodeId for reply
                ShowNotificationDialog($"Received at: {payload.Timestamp} UTC:\n\n{payload.Message}", payload.OvenId, matchingOven.GetVariable("OvenId").NodeId);
            }

            Log.Verbose1("IncomingMessagesLogic", $"Message received for non-matching oven: {payload.OvenId}");
            return;
        }
        catch (JsonException ex)
        {
            Log.Warning("IncomingMessagesLogic", $"Failed to parse incoming message: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error("IncomingMessagesLogic", $"Error processing incoming message: {ex.Message}");
        }
    }

    private void ShowNotificationDialog(string message, string sender, NodeId targetOvenNodeId)
    {
        try
        {
            // Get the NotificationDialog type
            

            // Create alias array to pass the TargetOvenId to the dialog's IncomingTargetOvenId alias
            var aliasArray = new NodeId[] { targetOvenNodeId };

            // Open dialog on Owner (the parent graphical container) with the alias
            var dialogInstance = UICommands.OpenDialog(Owner, notificationDialogType, aliasArray);
            if (dialogInstance == null)
            {
                Log.Warning("IncomingMessagesLogic", "Failed to open NotificationDialog");
                return;
            }

            // Set the title with the sender ID
            var titleVariable = dialogInstance.Get<Label>("NotificationCard1/Title");
            if (titleVariable != null)
            {
                titleVariable.Text = $"Message from: {sender}";
            }
            else
            {
                Log.Warning("IncomingMessagesLogic", "Title variable not found in dialog");
            }

            // Set the message text in the dialog
            var messageTextVariable = dialogInstance.Get<Label>("NotificationCard1/MessageScrollView/Message");
            if (messageTextVariable != null)
            {
                messageTextVariable.Text = message;
            }
            else
            {
                Log.Warning("IncomingMessagesLogic", "Message text variable not found in dialog");
            }
        }
        catch (Exception ex)
        {
            Log.Error("IncomingMessagesLogic", $"Failed to show notification dialog: {ex.Message}");
        }
    }
}
