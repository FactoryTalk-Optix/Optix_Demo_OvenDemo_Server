#region Using directives
using FTOptix.NetLogic;
using System;
using UAManagedCore;
#endregion

public class alarmSimulationLogic : BaseNetLogic
{
    public override void Start()
    {
        alarmSimulationTask = new PeriodicTask(AlarmSimulation, 1000, LogicObject);
        alarmSimulationTask.Start();
    }

    public override void Stop()
    {
        alarmSimulationTask.Dispose();
    }

    private void AlarmSimulation()
    {
        if (enabled)
        {
            var alarms = simulatedAlarms;
            Random rnd = new Random();
            for (int i = 0; i < alarms.Length; i++)
            {
                if (rnd.NextDouble() < 0.1)
                {
                    alarms[i] = !alarms[i];
                }
            }
            simulatedAlarms = alarms;
        }
    }

    PeriodicTask alarmSimulationTask;
    private bool[] simulatedAlarms
    {
        get => LogicObject.GetVariable("alarmSimulationArray").Value;
        set => LogicObject.GetVariable("alarmSimulationArray").Value = value;
    }
    private bool enabled => LogicObject.GetVariable("enableSimulation").Value;
}
