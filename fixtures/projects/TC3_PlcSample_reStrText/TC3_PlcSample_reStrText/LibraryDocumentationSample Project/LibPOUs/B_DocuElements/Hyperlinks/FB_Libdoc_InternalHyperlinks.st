(*

**Simple internal hyperlink target**

Clicking on this internal hyperlink will take us to the target_
below.

.. _target:

**Nesting of internal hyperlink targets**

Clicking on this internal hyperlink will take us to the `third item`_ of the bullet list.

* First list item
* Second list item

  .. _third item:

* Third list item, with hyperlink target.

**Concatentation of internal hyperlink targets**

Clicking on this internal hyperlink will take us to target1_
and clicking on this internal hyperlink will take us to target2_.
Both targets point the the same paragraph.

.. _target1:
.. _target2:

The targets "target1" and "target2" are synonyms; they both
point to this paragraph.
*)

FUNCTION_BLOCK FB_Libdoc_InternalHyperlinks
VAR_INPUT
    nVarA : INT;
    nVarB : INT;
END_VAR
VAR_OUTPUT
    nSum : INT;
END_VAR
VAR
END_VAR
