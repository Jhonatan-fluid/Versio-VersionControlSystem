(*
=====  =====  =======
  A      B    A and B
=====  =====  =======
False  False  False
True   False  False
False  True   False
True   True   True
=====  =====  =======

|

=====  =====  ======
   Inputs     Output
------------  ------
  A      B    A or B
=====  =====  ======
False  False  False
True   False  True
False  True   True
True   True   True
=====  =====  ======

|

=====  =====  ======
   Inputs     Output
------------  ------
  A      B    A or B
=====  =====  ======
False  False  False
       False  True
False  True   True
True   True   True
=====  =====  ======

|

=====  =====
Col 1  Col 2
=====  =====
1      Second column of row 1.
2      Second column of row 2.
       Second line of paragraph.
3      Second column of row 2.
                             
       Second line of paragraph
4      - Second column of row 3.

       - Second item in bullet
         list (row 4, column 2).
\      Row 5; column 1 will be empty.
=====  =====
*)

FUNCTION_BLOCK FB_Libdoc_SimpleTables
VAR_INPUT
    nVarA : INT;
    nVarB : INT;
END_VAR
VAR_OUTPUT
    nSum : INT;
END_VAR
VAR
END_VAR
