#region Using directives
using System;
using System.Linq;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NativeUI;
using FTOptix.NetLogic;
using FTOptix.WebUI;
using FTOptix.CoreBase;
using FTOptix.Alarm;
using FTOptix.RecipeX;
using FTOptix.DataLogger;
using FTOptix.EventLogger;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.OPCUAServer;
using FTOptix.MQTTBroker;
using FTOptix.MQTTClient;
using FTOptix.Core;
#endregion

public class OvensOverviewLogic : BaseNetLogic
{
    public override void Start()
    {
        overviewTask = new PeriodicTask(UpdateOverview, 1000, LogicObject);
        overviewTask.Start();
    }

    public override void Stop()
    {
        overviewTask?.Dispose();
        overviewTask = null;
    }

    private void UpdateOverview()
    {
        // Connected ovens count
        var connectedOvens = ovensFolder.Children.OfType<OptixOven>().Count();
        LogicObject.GetVariable("ConnectedOvens").SetValue(connectedOvens);

        // Daily gas and electricity from OEE store
        var store = Project.Current.Get<Store>("DataStores/EmbeddedDatabase");
        var todayStr = DateTime.UtcNow.Date.ToString("yyyy-MM-ddT00:00:00.0000000");

        store.Query(
            $"SELECT SUM(DailyGasConsumption), SUM(DailyElectricityConsumption) FROM Oee WHERE Timestamp = '{todayStr}'",
            out string[] _,
            out object[,] rows);

        if (rows != null && rows.GetLength(0) > 0)
        {
            LogicObject.GetVariable("DailyGasConsumption").SetValue(Convert.ToUInt32(rows[0, 0]));
            LogicObject.GetVariable("DailyElectricityConsumption").SetValue(Convert.ToUInt32(rows[0, 1]));
        }
        else
        {
            LogicObject.GetVariable("DailyGasConsumption").SetValue(0);
            LogicObject.GetVariable("DailyElectricityConsumption").SetValue(0);
        }
    }

    private PeriodicTask overviewTask;
    private IUANode ovensFolder = Project.Current.Get("Model/Ovens");
}
