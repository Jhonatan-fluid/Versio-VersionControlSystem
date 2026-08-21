(*
:Organization: Beckhoff Automation GmbH & Co. KG
:Contact: info@beckhoff.de
:Address: | Hülshorstweg 20
          | 33415 Verl
          | Germany
:Authors: - Me
          - Myself
          - I
:Version: 1.0
:Status: released
:Date: 2017-12-07

:Copyright:
 © Beckhoff Automation GmbH & Co. KG, Germany.
 The reproduction, distribution and utilization of this document as well as 
 the communication of its contents to others without express authorization are prohibited.
 Offenders will be held liable for the payment of damages. 
 All rights reserved in the event of the grant of a patent, utility model or design.
:Abstract: topic
:Indentation:
 Since the field marker may be quite long, the second
 and subsequent lines of the field body do not have to line up
 with the first line, but they must be indented relative to the
 field name marker, and they must line up with each other.
:LongLongFieldname: Since the field marker is quite long,
                    the field body is shown in the line after the marker.
:Parameter nIn: Integer
:Parameter nVarB: Integer
:*Parameter nVarC*: Integer
:``Parameter nVarD``: Integer
*)

FUNCTION_BLOCK FB_Libdoc_FieldList
VAR_INPUT
    nVarA : INT;
    nVarB : INT;
END_VAR
VAR_OUTPUT
    nSum : INT;
END_VAR
VAR
END_VAR
