(* METADATA
{
	"v3Meta": {
		"objectGuid": "2939601a-39c9-43e0-ad3e-ab0b5e188b9c"
	}
}
*)
METHOD PRIVATE _M_StateMachine
// Alarm disabled
IF NOT Settings.Enable OR (NOT Condition AND State = E_AlarmState.Off) THEN 
	RETURN;
END_IF

// State Machine
CASE State OF 
	E_AlarmState.Off:
		IF DelayTimer.Q THEN 
			NextState := E_AlarmState.Active;
		END_IF
	E_AlarmState.Active: 
		IF DelayTimer.Q THEN 
			NextState := E_AlarmState.Off;
		END_IF
	ELSE 
		NextState := E_AlarmState.Off;
END_CASE

// Update State 
_M_UpdateState();
END_METHOD