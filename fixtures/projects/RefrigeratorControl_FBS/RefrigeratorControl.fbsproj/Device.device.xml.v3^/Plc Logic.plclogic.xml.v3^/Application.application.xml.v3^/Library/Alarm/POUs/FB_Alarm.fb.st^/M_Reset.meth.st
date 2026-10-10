(* METADATA
{
	"v3Meta": {
		"objectGuid": "3d32724a-fdac-4121-8e14-72e1e9de4668"
	}
}
*)
METHOD PUBLIC M_Reset : BOOL
// If Alarm is set to latching, reset state, condition and timer
IF Settings.Latch THEN 
	Condition := FALSE;
	NextState := E_AlarmState.Off;
	_M_UpdateState();
END_IF
END_METHOD