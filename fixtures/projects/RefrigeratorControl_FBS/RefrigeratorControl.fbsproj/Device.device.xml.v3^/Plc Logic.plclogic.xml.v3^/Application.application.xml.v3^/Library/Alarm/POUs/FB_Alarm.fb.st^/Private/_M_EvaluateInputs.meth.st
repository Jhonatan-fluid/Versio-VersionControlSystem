(* METADATA
{
	"v3Meta": {
		"objectGuid": "8322d789-b37e-4ca0-abe3-2ebac5bf988b"
	}
}
*)
METHOD PRIVATE _M_EvaluateInputs : BOOL
VAR_INST
	EnableRTrig	: R_TRIG;
	EnableFTrig	: F_TRIG;
END_VAR
// Initialized
IF Settings.ShortDesc <> '' AND NOT Configuration.Initialized THEN 
	Configuration.Initialized := TRUE;
END_IF

// Enable State change
EnableRTrig(CLK := Settings.Enable);
EnableFTrig(CLK := Settings.Enable);
IF EnableRTrig.Q OR EnableFTrig.Q THEN 
	NextState := E_AlarmState.Off;
	_M_UpdateState();
END_IF

// Delays
_M_Timers();
END_METHOD