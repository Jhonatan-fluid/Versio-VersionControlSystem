(*
**Substitution in paragraphs**

Images are a common use for substitution references: |Beckhoff|_.

.. |Beckhoff| image:: ../../LibPics/BeckhoffLogo.png
 :height: 16
 :width: 64
.. _Beckhoff: http://www.beckhoff.de/

The |safety| symbol indicates a hazardous situation which,
if not avoided, could result in minor or moderate injury.

.. |safety| image:: ../../LibPics/safetysymbol.png


**Substitution in lists or tables**

* |Run mode| Run mode
* |Stop mode| Stop mode
* |Config mode| Config mode

============= ======
Symbol        Status
============= ======
|Run mode|    Run mode
|Stop mode|   Stop mode
|Config mode| Config mode
============= ======

.. |Run mode| image:: ../../LibPics/tc3runmode.png
.. |Stop mode| image:: ../../LibPics/tc3stopmode.png
.. |Config mode| image:: ../../LibPics/tc3configmode.png
*)

FUNCTION_BLOCK FB_Libdoc_Substitution_Images
VAR_INPUT
    nVarA : INT;
    nVarB : INT;
END_VAR
VAR_OUTPUT
    nSum : INT;
END_VAR
VAR
END_VAR
