using System;
using FTOptix.Core;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.OPCUAClient;
using UAManagedCore;
using FTOptix.MQTTClient;

public class OpcUaClientRuntimeHelper : BaseNetLogic
{
    private const string LogNode = "OpcUaClientRuntimeHelper";
    private const string OpcUaFolderName = "OPC-UA";
    private const string ClientsFolderName = "Clients";
    private const string ClientNamePrefix = "OPCUAClient_";

    public static void EnsureOpcUaClient(string edgeId, string endpointUrl)
    {
        if (string.IsNullOrWhiteSpace(edgeId))
        {
            Log.Warning(LogNode, "Cannot create OPC UA client because the EdgeId is empty.");
            return;
        }

        if (!TryValidateEndpointUrl(edgeId, endpointUrl, out var endpointUri))
            return;

        var safeEndpoint = GetSafeEndpointForLog(endpointUri);
        var failureStage = "locating OPC-UA/Clients";

        try
        {
            var clientsFolder = GetOrCreateClientsFolder();
            if (clientsFolder == null)
                return;

            var clientName = GetClientName(edgeId);
            failureStage = "resolving the client node";
            var existingNode = clientsFolder.Get(clientName);
            if (existingNode != null)
            {
                var existingClient = existingNode as OPCUAClient;
                if (existingClient == null)
                {
                    Log.Error(LogNode, $"Node '{clientName}' for EdgeId '{edgeId}' exists but is not an OPC UA Client; endpoint '{safeEndpoint}' was not configured.");
                    return;
                }

                failureStage = "updating the existing client";
                var endpointChanged = !string.Equals(existingClient.ServerEndpointURL, endpointUrl.Trim(), StringComparison.Ordinal);
                if (endpointChanged)
                {
                    existingClient.Enabled = false;
                    ConfigureClient(existingClient, endpointUrl.Trim());
                }

                Log.Info(LogNode, endpointChanged
                    ? $"Updated OPC UA client '{clientName}' for EdgeId '{edgeId}' to endpoint '{safeEndpoint}'."
                    : $"OPC UA client '{clientName}' already exists for EdgeId '{edgeId}' at endpoint '{safeEndpoint}'.");
                return;
            }

            failureStage = "creating the OPC UAClient object";
            var client = InformationModel.Make<OPCUAClient>(clientName);
            failureStage = "configuring the OPC UAClient object";
            ConfigureClient(client, endpointUrl.Trim());
            client.Enabled = false;
            failureStage = "adding the OPC UAClient to OPC-UA/Clients";
            clientsFolder.Add(client);
            Log.Info(LogNode, $"Created OPC UA client '{clientName}' for EdgeId '{edgeId}' at endpoint '{safeEndpoint}'; it is enabled while the edge is connected.");
        }
        catch (Exception ex)
        {
            Log.Error(LogNode, $"Failed to configure OPC UA client for EdgeId '{edgeId}' at endpoint '{safeEndpoint}' during {failureStage} ({ex.GetType().Name}).");
        }
    }

    // Driven by the edge presence (MQTT telemetry); a no-op when the client is missing or already in the requested state.
    public static void SetClientEnabled(string edgeId, bool enabled)
    {
        try
        {
            var client = GetClientsFolder()?.Get(GetClientName(edgeId)) as OPCUAClient;
            if (client == null || client.Enabled == enabled)
                return;

            client.Enabled = enabled;
            Log.Info(LogNode, $"OPC UA client '{client.BrowseName}' {(enabled ? "enabled" : "disabled")} (edge {(enabled ? "connected" : "disconnected")}).");
        }
        catch (Exception ex)
        {
            Log.Warning(LogNode, $"Failed to set OPC UA client for EdgeId '{edgeId}' to Enabled={enabled}: {ex.Message}");
        }
    }

    public static void RemoveOpcUaClient(string edgeId)
    {
        if (string.IsNullOrWhiteSpace(edgeId))
        {
            Log.Warning(LogNode, "Cannot remove OPC UA client because the EdgeId is empty.");
            return;
        }

        try
        {
            var clientsFolder = GetClientsFolder();
            if (clientsFolder == null)
            {
                Log.Warning(LogNode, $"Folder '{OpcUaFolderName}/{ClientsFolderName}' not found; no OPC UA client exists for EdgeId '{edgeId}'.");
                return;
            }

            var clientName = GetClientName(edgeId);
            var client = clientsFolder.Get(clientName);
            if (client == null)
            {
                Log.Warning(LogNode, $"OPC UA client '{clientName}' for EdgeId '{edgeId}' not found; nothing to remove.");
                return;
            }

            if (!(client is OPCUAClient))
            {
                Log.Warning(LogNode, $"Node '{clientName}' for EdgeId '{edgeId}' is not an OPC UA Client; it was not deleted.");
                return;
            }

            client.Delete();
            Log.Info(LogNode, $"Removed OPC UA client '{clientName}' for EdgeId '{edgeId}'.");
        }
        catch (Exception ex)
        {
            Log.Error(LogNode, $"Failed to remove OPC UA client for EdgeId '{edgeId}': {ex.Message}");
        }
    }

    private static bool TryValidateEndpointUrl(string edgeId, string endpointUrl, out Uri endpointUri)
    {
        endpointUri = null;
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            Log.Warning(LogNode, $"Onboarding Content for EdgeId '{edgeId}' is empty; no OPC UA client was created.");
            return false;
        }

        var trimmedEndpointUrl = endpointUrl.Trim();
        if (!Uri.IsWellFormedUriString(trimmedEndpointUrl, UriKind.Absolute) ||
            !Uri.TryCreate(trimmedEndpointUrl, UriKind.Absolute, out endpointUri) ||
            !string.Equals(endpointUri.Scheme, "opc.tcp", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(endpointUri.Host) ||
            !string.IsNullOrEmpty(endpointUri.UserInfo))
        {
            Log.Warning(LogNode, $"Onboarding Content for EdgeId '{edgeId}' is not a valid absolute opc.tcp:// URL; no OPC UA client was created.");
            endpointUri = null;
            return false;
        }

        return true;
    }

    private static Folder GetOrCreateClientsFolder()
    {
        var opcUaFolder = Project.Current.Get(OpcUaFolderName);
        if (opcUaFolder == null)
        {
            Log.Error(LogNode, $"Category folder '{OpcUaFolderName}' not found; cannot create OPC UA client.");
            return null;
        }

        var existingFolder = opcUaFolder.Get(ClientsFolderName);
        if (existingFolder != null)
        {
            var clientsFolder = existingFolder as Folder;
            if (clientsFolder == null)
                Log.Error(LogNode, $"Node '{ClientsFolderName}' under '{OpcUaFolderName}' is not a folder.");
            return clientsFolder;
        }

        var newFolder = InformationModel.Make<Folder>(ClientsFolderName);
        opcUaFolder.Add(newFolder);
        return newFolder;
    }

    private static Folder GetClientsFolder()
    {
        var clientsFolder = Project.Current.Get($"{OpcUaFolderName}/{ClientsFolderName}");
        return clientsFolder as Folder;
    }

    private static void ConfigureClient(OPCUAClient client, string endpointUrl)
    {
        client.ServerEndpointURL = endpointUrl;
        client.MinimumMessageSecurityMode = MessageSecurityMode.None;
        client.MinimumSecurityPolicy = SecurityPolicy.None;
        client.UserIdentityType = FTOptix.OPCUAClient.UserIdentityType.Anonymous;
    }

    private static string GetClientName(string edgeId)
    {
        var safeEdgeId = edgeId.Trim();
        var characters = safeEdgeId.ToCharArray();
        for (var i = 0; i < characters.Length; i++)
        {
            var character = characters[i];
            if (!((character >= 'A' && character <= 'Z') ||
                  (character >= 'a' && character <= 'z') ||
                  (character >= '0' && character <= '9') ||
                  character == '_' ||
                  character == '-'))
            {
                characters[i] = '_';
            }
        }

        return ClientNamePrefix + new string(characters);
    }

    private static string GetSafeEndpointForLog(Uri endpointUri)
    {
        var host = endpointUri.HostNameType == UriHostNameType.IPv6
            ? $"[{endpointUri.Host}]"
            : endpointUri.Host;
        var port = endpointUri.IsDefaultPort ? string.Empty : $":{endpointUri.Port}";
        return $"{endpointUri.Scheme}://{host}{port}";
    }
}
