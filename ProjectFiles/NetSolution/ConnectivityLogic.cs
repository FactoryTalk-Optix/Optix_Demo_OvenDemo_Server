#region Using directives
using System;
using System.IO;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.UI;
using FTOptix.HMIProject;
using FTOptix.NativeUI;
using FTOptix.NetLogic;
using FTOptix.WebUI;
using FTOptix.CoreBase;
using FTOptix.RecipeX;
using FTOptix.DataLogger;
using FTOptix.EventLogger;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.OPCUAServer;
using FTOptix.MQTTBroker;
using FTOptix.MQTTClient;
using FTOptix.Alarm;
using FTOptix.Core;
#endregion

public class ConnectivityLogic : BaseNetLogic
{
    public override void Start()
    {
        databaseSizeVariable = LogicObject.GetVariable("DatabaseSize");
        if (databaseSizeVariable == null)
        {
            Log.Error("ConnectivityLogic", "Missing DatabaseSize variable on NetLogic object");
            return;
        }

        databaseSizeTask = new PeriodicTask(UpdateDatabaseSize, 1000, LogicObject);
        databaseSizeTask.Start();

        UpdateDatabaseSize();
    }

    public override void Stop()
    {
        databaseSizeTask?.Dispose();
        databaseSizeTask = null;
        databaseSizeVariable = null;
    }

    private void UpdateDatabaseSize()
    {
        try
        {
            string dbPath = ResourceUri.FromApplicationRelativePath("ovenDemoDatabase.sqlite").Uri;
            string walPath = dbPath + "-wal";

            ulong totalBytes = 0;

            if (File.Exists(dbPath))
            {
                totalBytes += (ulong)new FileInfo(dbPath).Length;
            }

            if (File.Exists(walPath))
            {
                totalBytes += (ulong)new FileInfo(walPath).Length;
            }

            databaseSizeVariable.Value = totalBytes;
        }
        catch (Exception ex)
        {
            Log.Warning("ConnectivityLogic", $"Failed to update database size: {ex.Message}");
        }
    }

    private PeriodicTask databaseSizeTask;
    private IUAVariable databaseSizeVariable;
}
