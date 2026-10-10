(* METADATA
{
	"v3Meta": {
		"objectGuid": "6e8ed404-aa7c-4874-90d7-74d820d6423c"
	}
}
*)
{attribute 'linkalways'}
FUNCTION_BLOCK FB_Alarm
VAR PERSISTENT
	Settings 		: ST_AlarmSettings;
	Configuration	: ST_AlarmHmiConfiguration;
END_VAR
VAR_INPUT
	Condition 		: BOOL;			// Input Condition to trigger alarm
END_VAR
VAR
	// State Machine
	State 			: E_AlarmState := E_AlarmState.Off;
	NextState 		: E_AlarmState := E_AlarmState.Off;
	
	// Timer
	DelayTimer		: TON;
	
	// Object Interface
	{attribute 'instance-path'}
	{attribute 'no-init'}
	FullInstancePath		: STRING;
	InstancePath			: STRING;
	InstanceName			: STRING;
	
	
END_VAR

// Evaluate Inputs
	_M_EvaluateInputs();
// Evaluate Commands
	
// State Machine
	_M_StateMachine();
	
// Update Status
	_M_UpdateStatus();
	
// Global
	
// Evaluate Outputs
	
// Reset Latch
	IF NOT Settings.Latch THEN 
		Condition := FALSE;
	END_IF
END_FUNCTION_BLOCK