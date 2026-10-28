# Graph edit rules (L1)

Graph edits patch the existing bytecode in place; nothing is recompiled. That is why some edits that would be trivial in the Unreal editor are refused: the cooked code no longer has the information (or the shape) to express them. Each rule below matches an error message.

## Pin values (`set_pin_value`)

- Only unconnected inputs whose value is a literal in the bytecode (`value:<kind>`). A value computed by another node is changed at that node, or the input is first replaced with a literal (`break_pin`).
- One pin may be backed by several constants (a macro or a node expanded by the compiler); all of them change together.
- Enum pins take the enumerator name (the list is in the flags) or its number. Values outside the enum are accepted by the bytecode but undefined in game.
- `Name` and `String` literals can hold any text; non-ASCII strings switch to the UTF-16 form automatically.
- Object literals can only be `None` (removing an object reference). Pointing at another asset needs an L2 edit (`EX_ObjectConst`).

## Execution wires (`connect_pins` exec → exec, `break_pin` on an exec output)

| Error | Why | Instead |
| --- | --- | --- |
| Only nodes with a single execution output can be rewired | Branch, Sequence, loops, switches jump to several places | Rewire the node before it, or after the branch target; or change the condition with L2 |
| Events, function entries and macros can't be rewired yet | Their exec output is a jump table or a flow stack | Use the event's own exec output (that is supported: it moves the event start) |
| Latent nodes (Delay...) continue at a resume point | The engine resumes after the latent call at a stored offset | Rewire the node that follows the resume, or edit the duration |
| The node is followed by compiler-generated statements | The node's work continues past its own statement (e.g. it stores results) | Rewire from the next real node |
| The target has no execution input / is not a statement | Pure nodes and macro internals have no statement to jump to | Jump to the impure node that reads them |
| Event binding nodes ... can't be jumped to | Bind Event depends on a Create Event statement before it | Jump to the node before the Create Event |
| Another event already starts there | The decompiler tells events apart by their start | Start at a different node |
| Input events set up temporary values at their start | Their first statements fill the key/value temporaries | Not supported yet |

Jumping into a node also runs the pure nodes it reads (Flora jumps to their statements first), so values are fresh.

`break_pin` on a function's exec output jumps to the function's return; in the event graph it ends the current execution thread (later Sequence pins still run).

## Data wires (`connect_pins` data → data, `break_pin` on a data input)

| Error | Why |
| --- | --- |
| This input has no argument in the bytecode | The value is not an expression of its own (e.g. a self target folded into a context) |
| This output is not kept in a variable | The value only exists inside the reading expression; it can't be read again |
| The variable holding this output is reused by other nodes | Temporaries are shared: reading it elsewhere could see another node's value |
| The types don't match | No conversion is inserted; enum and byte count as the same type |
| The target of a call / a switch selection can't be replaced by a literal | The bytecode needs an object or the switch's own expression there |

## Class defaults

- Simple types (bool, numbers, names, strings, enums) take text. Structs, arrays, maps, soft references take the UAssetAPI JSON of the property: get it with `get_class_default`, change values, keep the `$type` entries and structure.
- `remove_class_default` makes the class inherit the parent's value again (it does not set the type's zero value).
- Defaults affect new instances. Actors already placed in a level keep the values saved in the level.

## Validation failures

A failure reads `[stage] ...` plus the exception chain and the top frames. Stages: `patch: declare locals`, `patch: bytecode` (the edits couldn't be applied), `verify: write the patched package` (UAssetAPI), `verify: read it back` (CUE4Parse), `verify: compare bytecode sizes`, `verify: decompile the edited functions` (names the function and, when known, the statement).

- `validate_edits` with nothing pending is the baseline check of the original, not a result for an edit.
- `[patch]` the edits could not be applied to the bytecode (usually two edits touching the same expression, or a stale offset): revert one.
- `[verify] ... size mismatch` the other parser reads the patched code to a different length: the edit can't be saved; revert it and report it.
- `... no longer decompiles` the patched function is not readable as a graph any more: treat it as broken.
- `... changed shape after the edit` a constant edit changed the node structure, which must not happen: revert it.
