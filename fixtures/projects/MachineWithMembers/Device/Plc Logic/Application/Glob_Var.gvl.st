{attribute 'qualified_only'}
VAR_GLOBAL
//Inputs
	TempActual: REAL:=8.0;
	TempSet: REAL:=8.0;
	DoorOpen: BOOL;
	AlarmThreshold:TIME:=T#10S;
	DoorOpenThreshold:TIME:=T#10S;

//Outputs
	Compressor: BOOL;
	Signal: BOOL;
	Lamp: BOOL;
END_VAR
