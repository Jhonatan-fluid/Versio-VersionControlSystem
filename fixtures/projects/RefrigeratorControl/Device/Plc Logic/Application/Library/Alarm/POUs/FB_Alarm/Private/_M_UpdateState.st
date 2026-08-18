METHOD PRIVATE _M_UpdateState : BOOL

// --- BEGIN IMPLEMENTATION ---

// Update State
IF NextState <> State THEN 
	State := NextState;
	
	//ResetTimer
	DelayTimer(IN := FALSE);
	
	IF State = E_AlarmState.Off THEN 
		;//Configuration.Info := '';
	END_IF
END_IF
