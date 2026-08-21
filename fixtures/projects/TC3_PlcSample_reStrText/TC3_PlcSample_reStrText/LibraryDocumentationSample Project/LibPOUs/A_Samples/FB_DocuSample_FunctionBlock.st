(* 
:Description: This function block represents <...> and can be used for <...> ...
:Instructions for use: How to use this FB ...

Version history:

+-------------+------------+----------------+----------+------------------------------------------+
|Date         | Version    | created under  | Author   | Remark                                   |
+=============+============+================+==========+==========================================+
|2017-07-04   | 1.0.0.0    | V3.1.4022.0    | S.H.     | Performance optimization                 |
+-------------+------------+----------------+----------+------------------------------------------+
|2019-01-11   | 1.1.0.0    | V3.1.4022.27   | K.F.     | Bug fix: Output calculation corrected    |
+-------------+------------+----------------+----------+------------------------------------------+
*)

FUNCTION_BLOCK FB_DocuSample_FunctionBlock
VAR_INPUT
    nVarInA : INT;       //First input variable
    nVarInB : INT := 5;  //Second input variable
END_VAR
VAR_OUTPUT
    nVarOut : INT;       //Output variable
END_VAR
