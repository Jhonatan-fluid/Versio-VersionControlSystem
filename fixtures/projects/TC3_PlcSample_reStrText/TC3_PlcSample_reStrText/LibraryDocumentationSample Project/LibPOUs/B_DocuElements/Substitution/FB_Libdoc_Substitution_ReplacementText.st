(*
**Substitution in paragraphs**

|RST|_ is an easy-to-read, what-you-see-is-what-you-get plaintext markup syntax.
Among others it is useful for library documentation. If the |RST| option is activated,
comments written in |RST| will be shown in the Documentation tab of the library manager in an attractive format.
|RST| is a little annoying to type over and over and spelling out the
bicapitalized word |RST| every time isn't really necessary for |RST| source readability.

.. |RST| replace:: reStructuredText
.. _RST: https://infosys.beckhoff.de/


This is a simple |substitution reference|. It will be replaced by
the processing system.

This is a |substitution and hyperlink reference|_. In
addition to being replaced, the replacement text or element will
refer to the "substitution and hyperlink reference" target.

.. |substitution reference| replace:: example of substitution 
.. |substitution and hyperlink reference| replace:: combination of substitution and hyperlink reference
.. _substitution and hyperlink reference: https://beckhoff.de/
*)

FUNCTION_BLOCK FB_Libdoc_Substitution_ReplacementText
VAR_INPUT
    nVarA : INT;
    nVarB : INT;
END_VAR
VAR_OUTPUT
    nSum : INT;
END_VAR
VAR
END_VAR
