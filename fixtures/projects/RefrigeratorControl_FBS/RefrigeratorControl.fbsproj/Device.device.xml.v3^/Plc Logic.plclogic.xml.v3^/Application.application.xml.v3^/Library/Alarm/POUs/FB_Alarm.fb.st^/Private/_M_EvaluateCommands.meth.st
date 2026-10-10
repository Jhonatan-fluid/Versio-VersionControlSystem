(* METADATA
{
	"v3Meta": {
		"objectGuid": "1b6999ce-aebb-4733-886d-c9adf1f6d328"
	}
}
*)
METHOD PRIVATE _M_EvaluateCommands : BOOL
// Evaluate Hmi Commands
(*IF Hmi.Cmd.Reset THEN 
	// Log it 
	// F_LogTrace(F_Global(Global).P_Logger, InstancePath, 'Operator Command: Reset Triggered');
	M_Reset();
END_IF*)
// Hmi.Cmd.Reset := FALSE;
END_METHOD