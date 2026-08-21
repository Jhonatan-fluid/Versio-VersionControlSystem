(*

**Code block with color-coded syntax**

.. code:: 
    
    // Attempts to return the value of a boolean property.
   
    FUNCTION GetBooleanProperty : BOOL
    VAR_INPUT
        sKey: STRING;
    END_VAR

    // This structure defines a special profile.

    TYPE ST_Profile :
    STRUCT
        nId     : INT := -1;
        sBuffer : STRING(255) := 'Hello';
    END_STRUCT
    END_TYPE

**Code block with line numbering**

.. code:: 
   :number-lines: 1
    
    // Attempts to return the value of a boolean property.
   
    FUNCTION GetBooleanProperty : BOOL
    VAR_INPUT
        sKey: STRING;
    END_VAR

    // This structure defines a special profile.

    TYPE ST_Profile :
    STRUCT
        nId     : INT := -1;
        sBuffer : STRING(255) := 'Hello';
    END_STRUCT
    END_TYPE

*)

FUNCTION_BLOCK FB_Libdoc_CodeBlock
VAR_INPUT
    nVarA : INT;
    nVarB : INT;
END_VAR
VAR_OUTPUT
    nSum : INT;
END_VAR
VAR
END_VAR
