(*
Line blocks are useful where the structure of lines is significant.

| Each new line begins with 
| a vertical bar ("|"). 
|     Line breaks and initial indents 
|     are preserved. 
| Continuation lines are wrapped 
  portions of long lines; they begin 
  with spaces in place of vertical bars.

    | Each new line begins with 
    | a vertical bar ("|"). 
    |     Line breaks and initial indents 
    |     are preserved. 
    | Continuation lines are wrapped 
      portions of long lines; they begin 
      with spaces in place of vertical bars.
*)

FUNCTION_BLOCK FB_Libdoc_LineBlock
VAR_INPUT
    nVarA : INT;
    nVarB : INT;
END_VAR
VAR_OUTPUT
    nSum : INT;
END_VAR
VAR
END_VAR
