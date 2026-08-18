METHOD PRIVATE _M_StateMachine

// --- BEGIN IMPLEMENTATION ---

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
