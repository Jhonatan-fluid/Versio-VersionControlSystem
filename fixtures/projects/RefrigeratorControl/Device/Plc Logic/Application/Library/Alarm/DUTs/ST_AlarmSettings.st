TYPE ST_AlarmSettings :
STRUCT
	ShortDesc			: STRING(20);
	Severity			: E_AlarmSeverity;
	Category			: E_AlarmCategory;
	Latch				: BOOL := FALSE;
	OnDelayTime			: TIME := T#0S;
	OffDelayTime		: TIME := T#0S;
	Enable				: BOOL := TRUE;
END_STRUCT
END_TYPE
