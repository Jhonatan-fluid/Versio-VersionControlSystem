METHOD PUBLIC M_Reset : BOOL

// --- BEGIN IMPLEMENTATION ---

// If Alarm is set to latching, reset state, condition and timer
IF Settings.Latch THEN 
	Condition := FALSE;
	NextState := E_AlarmState.Off;
	_M_UpdateState();
END_IF
