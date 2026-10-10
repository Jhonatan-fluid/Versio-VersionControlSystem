(* METADATA
{
	"v3Meta": {
		"objectGuid": "01bf9639-d5d4-441c-a341-fcf52c1878b6",
		"subObjects": {
			"get": {
				"objectGuid": "27d95739-7462-4cdc-b221-4283a2d18604"
			}
		}
	}
}
*)
{attribute 'monitoring' := 'call'}
PROPERTY_Get P_Active : BOOL
P_Active := State = E_AlarmState.Active;END_PROPERTY
