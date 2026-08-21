(*

[2]_ will be "2" (manually numbered footnote),

[3]_ will be "3" (another manually numbered footnote),

[#]_ will be "4" (anonymous auto-numbered footnote), and

[#label]_ will be "1" (labeled auto-numbered).

--------

.. [2] This footnote is labeled manually, so its number is fixed.

.. [3] This footnote is also labeled manually, so its number is also fixed.

.. [#label] This autonumber-labeled footnote will be labeled "1".
   It is the first auto-numbered footnote and no other footnote
   with label "1" exists.  The order of the footnotes is used to
   determine numbering, not the order of the footnote references!

.. [#] This footnote will be labeled "4".  It is the second
   auto-numbered footnote, but footnote labels "1", "2", "3" are already used. 
*)

FUNCTION_BLOCK FB_Libdoc_ManuallyAndAutomaticallyNumberedFootnotes
VAR_INPUT
    nVarA : INT;
    nVarB : INT;
END_VAR
VAR_OUTPUT
    nSum : INT;
END_VAR
VAR
END_VAR
