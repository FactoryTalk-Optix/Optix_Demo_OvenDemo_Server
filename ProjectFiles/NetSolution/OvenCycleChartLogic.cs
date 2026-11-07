#region Using directives
using System;
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

/// <summary>Points the sibling WebBrowser "Chart" at the cycle page of the oven referenced by the "Oven" variable (the manager NodeId).</summary>
public class OvenCycleChartLogic : BaseNetLogic
{
    public override void Start()
    {
        browser = Owner.Get<WebBrowser>("Chart");
        oven = Owner.GetVariable("Oven");
        theme = Session?.GetVariable("Theme");
        if (browser == null || oven == null)
        {
            Log.Warning(LogCategory, $"'Chart' or 'Oven' not found under '{Owner.BrowseName}'.");
            return;
        }

        oven.VariableChange += OnChanged;
        if (theme != null) theme.VariableChange += OnChanged;
        Refresh();
    }

    public override void Stop()
    {
        if (oven != null) oven.VariableChange -= OnChanged;
        if (theme != null) theme.VariableChange -= OnChanged;
    }

    private void OnChanged(object sender, VariableChangeEventArgs e) => Refresh();

    private void Refresh()
    {
        var manager = oven.Value.Value is NodeId id && !id.IsEmpty ? InformationModel.Get(id) : null;
        if (manager == null || !manager.BrowseName.StartsWith(OvenEdgeManagerBehavior.NamePrefix, StringComparison.Ordinal)) return;

        var deviceId = OvenEdgeManagerBehavior.GetDeviceId(manager.BrowseName);
        var dark = theme != null && Convert.ToInt32(theme.Value.Value) == 1;
        var path = $"eCharts/cycle/{deviceId}-{(dark ? "dark" : "light")}.html";
        if (path == current) return;

        current = path;
        browser.URL = ResourceUri.FromProjectRelativePath(path);
    }

    private const string LogCategory = "OvenCycleChartLogic";
    private WebBrowser browser;
    private IUAVariable oven;
    private IUAVariable theme;
    private string current;
}
