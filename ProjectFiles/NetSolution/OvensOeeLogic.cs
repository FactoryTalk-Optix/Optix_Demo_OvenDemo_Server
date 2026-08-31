#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using FTOptix.Core;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.Store;
using UAManagedCore;
#endregion

public class OvensOeeLogic : BaseNetLogic
{
    public override void Start()
    {
        oeeTask = new PeriodicTask(UpdateOeeStats, 1000, LogicObject);
        oeeTask.Start();
    }

    public override void Stop()
    {
        oeeTask?.Dispose();
        oeeTask = null;
    }

    private void UpdateOeeStats()
    {
        var store = Project.Current.Get<Store>("DataStores/EmbeddedDatabase");
        var today = DateTime.UtcNow.Date;
        var todayStr = today.ToString("yyyy-MM-ddT00:00:00.0000000");
        var todayKey = today.ToString("yyyy-MM-dd");

        // Reset baselines when the day rolls over
        if (todayKey != lastTrackedDay)
        {
            baselines.Clear();
            lastTrackedDay = todayKey;
        }

        try
        {
            foreach (var oven in ovensFolder.Children.OfType<OptixOven>())
            {
                try
                {
                    string ovenId = oven.OvenId;
                    if (string.IsNullOrEmpty(ovenId))
                        continue;

                    uint ovenCycles = (uint)oven.Cycles;
                    uint ovenGas = (uint)oven.GasConsumption;
                    uint ovenElectricity = (uint)oven.PowerConsumption;
                    string ovenName = oven.OvenName ?? string.Empty;
                    string ovenLastUpdate = oven.LastUpdate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff");

                    // Query for an existing row for this oven today
                    store.Query(
                        $"SELECT ActiveSeconds, Cycles, GasConsumption, ElectricityConsumption, DailyCycles, DailyGasConsumption, DailyElectricityConsumption, OvenName FROM Oee WHERE OvenId = '{ovenId}' AND Timestamp = '{todayStr}'",
                        out string[] _,
                        out object[,] rows);

                    bool rowExists = rows != null && rows.GetLength(0) > 0;
                    bool isProducing = oven.CurrentPhase > 0;

                    if (rowExists)
                    {
                        uint storedActiveSeconds = Convert.ToUInt32(rows[0, 0]);
                        uint storedCycles = Convert.ToUInt32(rows[0, 1]);
                        uint storedGas = Convert.ToUInt32(rows[0, 2]);
                        uint storedElectricity = Convert.ToUInt32(rows[0, 3]);
                        uint storedDailyCycles = Convert.ToUInt32(rows[0, 4]);
                        uint storedDailyGas = Convert.ToUInt32(rows[0, 5]);
                        uint storedDailyElectricity = Convert.ToUInt32(rows[0, 6]);

                        if (!baselines.TryGetValue(ovenId, out var baseline))
                        {
                            // First time we see this oven after start/restart:
                            // record the oven's current raw values as the baseline offset so that
                            // baseline + ovenValue always continues from what is already in the DB.
                            baseline = new OvenBaseline
                            {
                                Cycles = storedCycles > ovenCycles ? storedCycles - ovenCycles : 0,
                                Gas = storedGas > ovenGas ? storedGas - ovenGas : 0,
                                Electricity = storedElectricity > ovenElectricity ? storedElectricity - ovenElectricity : 0,
                                StartOfDayCycles = storedCycles - storedDailyCycles,
                                StartOfDayGas = storedGas - storedDailyGas,
                                StartOfDayElectricity = storedElectricity - storedDailyElectricity
                            };
                            baselines[ovenId] = baseline;
                        }
                        else
                        {
                            // Detect a reset: oven counter dropped below what we last saw
                            if (ovenCycles < baseline.LastOvenCycles)
                                baseline.Cycles = storedCycles;
                            if (ovenGas < baseline.LastOvenGas)
                                baseline.Gas = storedGas;
                            if (ovenElectricity < baseline.LastOvenElectricity)
                                baseline.Electricity = storedElectricity;
                        }

                        baseline.LastOvenCycles = ovenCycles;
                        baseline.LastOvenGas = ovenGas;
                        baseline.LastOvenElectricity = ovenElectricity;

                        uint newActiveSeconds = storedActiveSeconds + (isProducing ? 1u : 0u);
                        uint newCycles = baseline.Cycles + ovenCycles;
                        uint newGas = baseline.Gas + ovenGas;
                        uint newElectricity = baseline.Electricity + ovenElectricity;
                        uint newDailyCycles = newCycles >= baseline.StartOfDayCycles ? newCycles - baseline.StartOfDayCycles : newCycles;
                        uint newDailyGas = newGas >= baseline.StartOfDayGas ? newGas - baseline.StartOfDayGas : newGas;
                        uint newDailyElectricity = newElectricity >= baseline.StartOfDayElectricity ? newElectricity - baseline.StartOfDayElectricity : newElectricity;

                        store.Query(
                            $"UPDATE Oee SET ActiveSeconds = {newActiveSeconds}, Cycles = {newCycles}, GasConsumption = {newGas}, ElectricityConsumption = {newElectricity}, DailyCycles = {newDailyCycles}, DailyGasConsumption = {newDailyGas}, DailyElectricityConsumption = {newDailyElectricity}, OvenName = '{ovenName}', LastUpdate = '{ovenLastUpdate}' WHERE OvenId = '{ovenId}' AND Timestamp = '{todayStr}'",
                            out _,
                            out _);
                    }
                    else
                    {
                        // No row for today � first record of the day, baselines start at zero
                        var baseline = new OvenBaseline
                        {
                            Cycles = 0,
                            Gas = 0,
                            Electricity = 0,
                            LastOvenCycles = ovenCycles,
                            LastOvenGas = ovenGas,
                            LastOvenElectricity = ovenElectricity,
                            StartOfDayCycles = ovenCycles,
                            StartOfDayGas = ovenGas,
                            StartOfDayElectricity = ovenElectricity
                        };
                        baselines[ovenId] = baseline;

                        string[] columns = { "Timestamp", "ActiveSeconds", "Cycles", "GasConsumption", "ElectricityConsumption", "DailyCycles", "DailyGasConsumption", "DailyElectricityConsumption", "OvenId", "OvenName", "LastUpdate" };
                        var values = new object[1, columns.Length];
                        values[0, 0] = today;
                        values[0, 1] = isProducing ? 1u : 0u;
                        values[0, 2] = ovenCycles;
                        values[0, 3] = ovenGas;
                        values[0, 4] = ovenElectricity;
                        values[0, 5] = 0u;
                        values[0, 6] = 0u;
                        values[0, 7] = 0u;
                        values[0, 8] = ovenId;
                        values[0, 9] = ovenName;
                        values[0, 10] = oven.LastUpdate;

                        store.Insert("Oee", columns, values);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Error updating OEE stats for oven {oven.OvenId}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error querying ovens or updating OEE stats: {ex.Message}");
        }
    }

    private class OvenBaseline
    {
        public uint Cycles;
        public uint Gas;
        public uint Electricity;
        public uint LastOvenCycles;
        public uint LastOvenGas;
        public uint LastOvenElectricity;
        public uint StartOfDayCycles;
        public uint StartOfDayGas;
        public uint StartOfDayElectricity;
    }

    private string lastTrackedDay = string.Empty;
    private readonly Dictionary<string, OvenBaseline> baselines = new();
    private PeriodicTask oeeTask;
    private IUANode ovensFolder = Project.Current.Get<Folder>("Model/Ovens");
}
