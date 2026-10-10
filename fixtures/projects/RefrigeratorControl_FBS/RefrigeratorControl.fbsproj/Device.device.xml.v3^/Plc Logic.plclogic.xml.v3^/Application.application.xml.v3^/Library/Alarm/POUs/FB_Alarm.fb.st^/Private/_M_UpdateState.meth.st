(* METADATA
{
	"v3Meta": {
		"objectGuid": "97b89cef-285a-4cde-a86d-08debff8ecbf"
	}
}
*)
METHOD PRIVATE _M_UpdateState : BOOL
// Update State
IF NextState <> State THEN 
	State := NextState;
	
	//ResetTimer
	DelayTimer(IN := FALSE);
	
	IF State = E_AlarmState.Off THEN 
		;//Configuration.Info := '';
	END_IF
END_IF
END_METHOD