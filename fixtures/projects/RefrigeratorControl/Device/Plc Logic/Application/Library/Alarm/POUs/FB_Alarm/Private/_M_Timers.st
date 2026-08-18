METHOD PRIVATE _M_Timers : BOOL

// --- BEGIN IMPLEMENTATION ---

DelayTimer.IN := SEL(State = E_AlarmState.Off, NOT (Condition OR Settings.Latch), Condition) AND Settings.Enable;
DelayTimer.PT := SEL(State = E_AlarmState.Off, Settings.OffDelayTime, Settings.OnDelayTime);
DelayTimer();
