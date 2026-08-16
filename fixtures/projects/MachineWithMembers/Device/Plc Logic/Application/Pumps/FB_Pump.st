FUNCTION_BLOCK FB_Pump
VAR_INPUT
	enable : BOOL;
END_VAR
VAR_OUTPUT
	running : BOOL;
END_VAR
VAR
	speed : INT;
END_VAR

// --- BEGIN IMPLEMENTATION ---

running := enable;
