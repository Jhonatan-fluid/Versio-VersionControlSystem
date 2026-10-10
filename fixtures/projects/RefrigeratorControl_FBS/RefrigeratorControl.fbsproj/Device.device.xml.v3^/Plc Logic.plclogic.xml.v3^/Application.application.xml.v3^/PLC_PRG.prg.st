(* METADATA
{
	"v3Meta": {
		"objectGuid": "e5cd9370-6160-4e13-a6ef-514133c47037"
	}
}
*)
PROGRAM PLC_PRG
VAR
	Alarm 		: FB_Alarm;
	Trigger 	: BOOL;
	OnTime		: TIME := T#2S;
	OffTime		: TIME := T#5S;
	Latch		: BOOL := FALSE;
END_VAR

Alarm.P_Settings.Latch := Latch;
Alarm.P_Settings.OnDelayTime := OnTime;
Alarm.P_Settings.OffDelayTime := OffTime;
Alarm.Condition := Trigger;
Alarm();
END_PROGRAM