PROGRAM PLC_PRG
VAR
	Alarm 		: FB_Alarm;
	Trigger 	: BOOL;
	OnTime		: TIME := T#2S;
	OffTime		: TIME := T#5S;
	Latch		: BOOL := FALSE;
END_VAR

// --- BEGIN IMPLEMENTATION ---

Alarm.P_Settings.Latch := Latch;
Alarm.P_Settings.OnDelayTime := OnTime;
Alarm.P_Settings.OffDelayTime := OffTime;
Alarm.Condition := Trigger;
Alarm();
