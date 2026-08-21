(*

**Indirect references to an internal reference target**

This hyperlink points to target one_ and indirect to target three.

.. _one: two_
.. _two: three_
.. _three:

The hyperlink targets above point to this paragraph.

**Indirect references to an external reference target**

The `Beckhoff Information System`_ is a reference source for Beckhoff_ products

.. _Beckhoff: `Beckhoff Information System`_

.. _Beckhoff Information System: https://infosys.beckhoff.de/
*)

FUNCTION_BLOCK FB_Libdoc_IndirectHyperlinks
VAR_INPUT
    nVarA : INT;
    nVarB : INT;
END_VAR
VAR_OUTPUT
    nSum : INT;
END_VAR
VAR
END_VAR
