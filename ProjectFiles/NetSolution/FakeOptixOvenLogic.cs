#region Using directives
using System;
using FTOptix.NetLogic;
using UAManagedCore;
#endregion

public class FakeOptixOvenLogic : BaseNetLogic
{
    public override void Start()
    {
        thisOven = Owner as FakeOptixOven;

        if (string.IsNullOrEmpty(thisOven.OvenId))
            thisOven.OvenId = Guid.NewGuid().ToString()[..8];

        thisOven.CurrentRecipe = "DefaultRecipe";

        phases = [thisOven.OvenPhase1, thisOven.OvenPhase2, thisOven.OvenPhase3];

        phaseIndex = 0;
        phaseElapsed = 0;
        cycleElapsed = 0;
        cooldownTicks = 0;
        cooldownDurationSeconds = 0;
        inCooldown = false;
        currentTemperature = AmbientTemperature;
        heaterOn = false;

        thisOven.CurrentPhase = phaseIndex + 1;
        thisOven.CurrentTemperature = (int)currentTemperature;
        thisOven.LastJobStarted = DateTime.UtcNow;
        thisOven.LastUpdate = DateTime.UtcNow;

        ovenTask = new PeriodicTask(OvenMethod, TimeSpan.FromMilliseconds(250), LogicObject);
        ovenTask.Start();
    }

    public override void Stop()
    {
        ovenTask?.Dispose();
        ovenTask = null;
    }

    private void OvenMethod()
    {
        if (inCooldown)
        {
            RunCooldown();
            return;
        }

        var heatingPerTick = Random.Shared.NextDouble() * 9 + 1; // between 1 and 10 degrees per tick when the heater is on
        var coolingPerTick = Random.Shared.NextDouble() * 0.50 + 0.125; // between 0.125 and 0.625 degrees per tick when cooling (or when the heater is off)

        OvenPhase activePhase = phases[phaseIndex];

        // Bang-bang temperature controller with hysteresis deadband
        if (!heaterOn && currentTemperature < activePhase.Setpoint - BangDeadband)
            heaterOn = true;
        else if (heaterOn && currentTemperature >= activePhase.Setpoint + BangDeadband)
            heaterOn = false;

        // Simulate temperature dynamics
        if (heaterOn)
            currentTemperature = Math.Min(currentTemperature + heatingPerTick, activePhase.Setpoint + BangDeadband);
        else
            currentTemperature = Math.Max(currentTemperature - coolingPerTick, AmbientTemperature);

        // Gas consumption only while the heater is actively firing
        if (heaterOn)
            thisOven.GasConsumption += GasPerTick;

        // Power consumption proportional to total fan speed (input + output)
        int totalFanSpeed = activePhase.InputFan + activePhase.OutputFan;
        if (totalFanSpeed > 0)
            thisOven.PowerConsumption += (int)(totalFanSpeed * PowerPerFanUnit);

        thisOven.CurrentTemperature = (int)Math.Round(currentTemperature);
        thisOven.CurrentPhase = phaseIndex + 1;
        thisOven.LastUpdate = DateTime.UtcNow;
        UpdateCurrentStatus(activePhase);

        // Phase duration is measured in whole seconds; accumulate fractional ticks
        phaseElapsedTicks++;
        if (phaseElapsedTicks >= TicksPerSecond)
        {
            phaseElapsedTicks = 0;
            phaseElapsed++;
            cycleElapsed++;
            thisOven.CycleTime = cycleElapsed;
        }

        if (phaseElapsed >= activePhase.Duration)
        {
            phaseElapsed = 0;
            phaseElapsedTicks = 0;
            phaseIndex++;

            if (phaseIndex >= phases.Length)
            {
                thisOven.Cycles++;
                inCooldown = true;
                cooldownTicks = 0;
                cooldownDurationSeconds = Random.Shared.Next(CooldownMinSeconds, CooldownMaxSeconds + 1);
                heaterOn = false;
                thisOven.CurrentPhase = 0;
                thisOven.LastUpdate = DateTime.UtcNow;
                Log.Verbose1("FakeOptixOvenLogic", $"{thisOven.OvenName} completed cycle #{thisOven.Cycles}, entering cooldown ({cooldownDurationSeconds}s)");
            }
        }

        // Check for overflows
        if (thisOven.GasConsumption > 2000000000)
            thisOven.GasConsumption = 0;
        if (thisOven.PowerConsumption > 2000000000)
            thisOven.PowerConsumption = 0;
        if (thisOven.Cycles > 2000000000)
            thisOven.Cycles = 0;
    }

    private void RunCooldown()
    {
        // Passively cool the oven during the between-cycle idle period
        currentTemperature = Math.Max(currentTemperature - CooldownCoolingPerTick, AmbientTemperature);
        thisOven.CurrentTemperature = (int)Math.Round(currentTemperature);
        thisOven.LastUpdate = DateTime.UtcNow;
        UpdateCurrentStatus(null);

        cooldownTicks++;
        if (cooldownTicks >= cooldownDurationSeconds * TicksPerSecond)
        {
            inCooldown = false;
            cooldownTicks = 0;
            phaseIndex = 0;
            phaseElapsed = 0;
            phaseElapsedTicks = 0;
            cycleElapsed = 0;
            thisOven.CycleTime = 0;
            thisOven.CurrentPhase = phaseIndex + 1;
            thisOven.LastJobStarted = DateTime.UtcNow;
            thisOven.LastUpdate = DateTime.UtcNow;
            Log.Verbose1("FakeOptixOvenLogic", $"{thisOven.OvenName} cooldown complete, starting new cycle");
        }
    }

    private void UpdateCurrentStatus(OvenPhase activePhase)
    {
        if (activePhase == null)
        {
            thisOven.CurrentStatus.Heating = false;
            thisOven.CurrentStatus.InputFan = 0;
            thisOven.CurrentStatus.OutputFan = 0;
            thisOven.CurrentStatus.Duration = cooldownDurationSeconds;
            thisOven.CurrentStatus.Setpoint = (int)AmbientTemperature;
            return;
        }

        thisOven.CurrentStatus.Heating = activePhase.Setpoint > thisOven.CurrentTemperature;
        thisOven.CurrentStatus.InputFan = activePhase.InputFan;
        thisOven.CurrentStatus.OutputFan = activePhase.OutputFan;
        thisOven.CurrentStatus.Duration = activePhase.Duration;
        thisOven.CurrentStatus.Setpoint = activePhase.Setpoint;
    }

    private const double AmbientTemperature = 20.0;
    private const double BangDeadband = 2.0;
    private const double CooldownCoolingPerTick = 2.0;
    private const int GasPerTick = 1;
    private const double PowerPerFanUnit = 0.125;
    private const int TicksPerSecond = 4; // matches 250 ms interval
    private const int CooldownMinSeconds = 20;
    private const int CooldownMaxSeconds = 40;

    private FakeOptixOven thisOven;
    private PeriodicTask ovenTask;
    private OvenPhase[] phases;
    private int phaseIndex;
    private int phaseElapsed;
    private int phaseElapsedTicks;
    private int cycleElapsed;
    private double currentTemperature;
    private bool heaterOn;
    private bool inCooldown;
    private int cooldownTicks;
    private int cooldownDurationSeconds;
}
