(*
.. csv-table:: Property list
   :header: "Items", "Property 1", "Property 2", "Property 3"
   :widths: 10, 15, 15, 15

   "Item 1", 1.67, angular, red
   "Item 2", "not specified", round, blue

|

.. csv-table:: Property list
   :header-rows: 1
   :widths: 10, 15, 15, 15

   "Items", "Property 1", "Property 2", "Property 3"
   "Item 1", 1.67, angular, red
   "Item 2", "not specified", round, blue
*)

FUNCTION_BLOCK FB_Libdoc_CSVTable
VAR_INPUT
    nVarA : INT;
    nVarB : INT;
END_VAR
VAR_OUTPUT
    nSum : INT;
END_VAR
VAR
END_VAR
