(* METADATA
{
	"v3Meta": {
		"objectGuid": "980a4bef-b0b7-4f16-816b-1f41192a0ee0"
	}
}
*)
PROGRAM Simulation
VAR
	T1: TON;					//The temperature is decreased on a time delay, when the comepressor has been activated
	P_Cooling: TIME:=T#500MS;			
	xReduceTemp: BOOL;			//Signal for dereasing the temperature
	T2: TON;					//The temperature is increased on a time delay, when the compressor has been activated 
	P_Environment: TIME:=T#2S;			//Delay time when the door is closed
	P_EnvironmentDoorOpen: TIME:=T#1S;	//Delay time when the door is open
	xRaiseTemp: BOOL;					//Signal for increasing the temperature
	tTemp: TIME;						//Delay time
	TestTag : BOOL;
	TestTAg2: BOOL;
END_VAR

// After the compressor has been activated due to TempActual being too high, the temperature decreases.
// The temperature is decremented by 0.1°C per cycle after a delay of P_Cooling
IF Glob_Var.Compressor THEN
	T1(IN:= Glob_Var.Compressor, PT:= P_Cooling, Q=>xReduceTemp);
	IF xReduceTemp THEN
		Glob_Var.TempActual := Glob_Var.TempActual-0.1;
		T1(IN:=FALSE);
	END_IF
END_IF

//If the door is open, the warming occurs faster; SEL selects P_EnvironmentDoorOpen
tTemp:=SEL(Glob_Var.DoorOpen, P_Environment, P_EnvironmentDoorOpen);

//If the compressor is not in operation, then the cooling chamber becomes warmer.
//The  temperature is incremented by 0.1°C per cycle after a delay of tTemp
T2(IN:= TRUE, PT:= tTemp, Q=>xRaiseTemp);	
IF xRaiseTemp THEN
	Glob_Var.TempActual := Glob_Var.TempActual + 0.1;
	T2(IN:=FALSE);
END_IF
END_PROGRAM