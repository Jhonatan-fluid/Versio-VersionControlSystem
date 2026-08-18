METHOD PRIVATE _M_EvaluateCommands : BOOL

// --- BEGIN IMPLEMENTATION ---

// Evaluate Hmi Commands
(*IF Hmi.Cmd.Reset THEN 
	// Log it 
	// F_LogTrace(F_Global(Global).P_Logger, InstancePath, 'Operator Command: Reset Triggered');
	M_Reset();
END_IF*)
// Hmi.Cmd.Reset := FALSE;
