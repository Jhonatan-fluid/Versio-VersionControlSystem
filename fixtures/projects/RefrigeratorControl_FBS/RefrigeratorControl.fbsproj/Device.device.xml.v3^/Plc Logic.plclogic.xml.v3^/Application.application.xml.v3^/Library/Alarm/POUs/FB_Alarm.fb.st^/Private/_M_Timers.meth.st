(* METADATA
{
	"v3Meta": {
		"objectGuid": "16b38c5d-f9c4-4a53-9840-9063bff02cb9"
	}
}
*)
METHOD PRIVATE _M_Timers : BOOL
DelayTimer.IN := SEL(State = E_AlarmState.Off, NOT (Condition OR Settings.Latch), Condition) AND Settings.Enable;
DelayTimer.PT := SEL(State = E_AlarmState.Off, Settings.OffDelayTime, Settings.OnDelayTime);
DelayTimer();
END_METHOD