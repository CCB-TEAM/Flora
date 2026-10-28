# Bytecode objects (L2, `kismet_*` tools)

L2 edits replace, insert or remove Kismet bytecode as nested JSON objects that map one-to-one onto UAssetAPI's expression classes. Nothing in the JSON is a byte offset or a table index: Flora resolves references, adds names and imports, and recomputes every offset when it lays the function out again.

## The shape

```json
{"op": "EX_Let",
 "Value": {"path": ["Temp_float_Variable"], "owner": "export:ExecuteUbergraph_BP_Hero"},
 "Variable": {"op": "EX_LocalVariable", "Variable": {"path": ["Temp_float_Variable"], "owner": "export:ExecuteUbergraph_BP_Hero"}},
 "Expression": {"op": "EX_FloatConst", "Value": 900.0}}
```

- `op` is the token name. Every other key is a field of that UAssetAPI class, with the exact field name. `kismet_schema` lists the fields of any op; unknown or missing fields are errors.
- Output from `kismet_get_expression` / `kismet_list_statements` adds `"@": offset` to each object. It is ignored on input, so you can send back what you read.
- **Copy before you invent.** The safest way to write a statement is to fetch an existing one of the same kind from the same blueprint (`kismet_get_expression`), change what you need, and send it back. Cooked code has conventions (which variable form, context, return temporaries) that are easier to copy than to reconstruct.

## References

| Field type | JSON |
| --- | --- |
| Object (`FPackageIndex`) | `"import:/Script/Engine.KismetSystemLibrary:PrintString"`, `"export:BP_Hero_C"`, or `null`. A missing import is added; its class is guessed (package / Class or BlueprintGeneratedClass / Function). For anything else pass `{"import": "/Script/Engine.SomeStruct", "className": "ScriptStruct", "classPackage": "/Script/CoreUObject"}`. |
| Property (`KismetPropertyPointer`) | `{"path": ["Health"], "owner": "export:BP_Hero_C"}` (UE 4.25+; path innermost first) or `{"object": "import:..."}` (older engines). Use the form the existing code of this blueprint uses. **The property must be declared by its owner** (a function's locals and parameters, a class's variables); a name alone is refused. Locals of the function you edit are `owner: "export:<bytecode function>"`; declare new ones with `kismet_add_local`. |
| Name (`FName`) | plain string; added to the name map when new |
| Code offset (jumps, `EX_PushExecutionFlow.PushingAddress`, `EX_SkipOffsetConst`) | `"@312"` = the statement that starts at 312 in the **original** code, or `"#name"` = a statement of the same insert that has `"label": "name"` |
| Derived (`EX_Context.Offset`, `EX_Skip.CodeOffset`, switch ends and case offsets) | left out: computed from sizes |
| `EX_StructConst.StructSize` | may be left out when another constant of the same struct exists in the package |

`kismet_find_symbols` searches imports, exports and properties (locals of each function, class variables) by name.

## Edits

- `kismet_replace(function, offset, token, object)` replaces the expression starting at `offset` (statement or nested expression; `token` guards against stale offsets). Offsets that pointed at the old statement now point at the new one.
- `kismet_insert_after(function, offset, token, statements)` inserts statements after a statement. Jumps to the following statement are unchanged, so the inserted code runs only when execution falls through from `offset`.
- `kismet_remove_statement(function, offset, token)` turns a statement into `EX_Nothing`, keeping every offset that points at it valid.
- Each edit targets the original code; two edits may not rewrite overlapping expressions. Validate after each L2 edit: errors point at the JSON path (`statements[1].Expression.Parameters[0]: ...`).

## How cooked blueprint code is organised

- **Functions** run their statements in order and end with `EX_Return` then `EX_EndOfScript`. Return values and out parameters are locals assigned before the return.
- **The event graph** is one function, `ExecuteUbergraph_<BP>`, taking an int entry point. Each event is a small stub function that calls it (`EX_LocalFinalFunction` with `EX_IntConst` = the offset where that event's chain starts). Inside, chains end with `EX_PopExecutionFlow` (end of this thread) or jump to shared code. Values that must survive across events or latent calls live on the persistent frame (`EX_LetValueOnPersistentFrame`).
- **Sequence** nodes push the next pin's address (`EX_PushExecutionFlow`) and pop it at the end of each pin's chain. **Branch** is `EX_JumpIfNot(condition, target)` (jump when false); `EX_PopExecutionFlowIfNot` ends the thread when false.
- **Calls**: `EX_CallMath` (static library functions, `StackNode` = function import), `EX_FinalFunction` / `EX_LocalFinalFunction` (non-virtual, the local form for functions of this class), `EX_VirtualFunction` / `EX_LocalVirtualFunction` (by name, `VirtualFunctionName`). `Parameters` follow the callee's signature in order; out parameters are passed as variable expressions. A call on another object is wrapped in `EX_Context { ObjectExpression, ContextExpression: the call, RValuePointer: the property receiving the result or null }` (`EX_Context_FailSilent` skips silently when the object is null).
- **Out parameters and return values need real storage.** A parameter the callee writes (`OutParm`, e.g. an `Array<int>` output) is passed as a variable expression (`EX_LocalVariable` / `EX_LocalOutVariable` with a declared property). Never pass `EX_Nothing`: the callee then writes into uninitialised memory, which crashes for arrays and other non-trivial types. If the result is unused, still declare a local for it. Check which parameters are out with `jmap_function`.
- **Results** of calls with a return value are assigned to a temporary with `EX_Let` (`Expression` = the call); later nodes read the temporary.
- **Assignments**: `EX_Let` (generic, with `Value` pointing at the property), and the typed forms `EX_LetBool`, `EX_LetObj`, `EX_LetWeakObjPtr`, `EX_LetDelegate`, `EX_LetMulticastDelegate`.
- **Variables**: `EX_LocalVariable`, `EX_InstanceVariable` (member of self), `EX_DefaultVariable` (class default), `EX_LocalOutVariable` (out parameter), `EX_Self`.
- **Literals**: `EX_True`/`EX_False`, `EX_IntConst`, `EX_Int64Const`, `EX_FloatConst`, `EX_DoubleConst` (UE5 floats are usually doubles), `EX_ByteConst`, `EX_NameConst`, `EX_StringConst`/`EX_UnicodeStringConst`, `EX_TextConst`, `EX_ObjectConst`, `EX_NoObject`, `EX_StructConst`, `EX_VectorConst`, `EX_RotationConst`, `EX_TransformConst`.

## Recipes

**Call a different function with the same signature**: get the call (`kismet_get_expression` at the call's offset), change `StackNode` (or `VirtualFunctionName`), `kismet_replace` it.

**Change a condition**: replace the `BooleanExpression` of the `EX_JumpIfNot` at the branch's statement, e.g. with `{"op": "EX_True"}` to never take the false path, or with a call returning bool.

**Skip a statement only sometimes**: insert before it (after the previous statement) an `EX_JumpIfNot` whose `CodeOffset` is the statement after the one to skip (`"@<offset>"`).

**Add a call**: insert a statement after an existing one. Copy an existing call to the same library function if there is one; otherwise check the callee's parameters with `get_graph` on a node that calls it elsewhere, or keep to functions whose signature you are certain of. A wrong parameter count or type validates but crashes or misbehaves in game.

**Remove a statement**: `kismet_remove_statement`. Removing an assignment leaves its variable with the previous value; removing a `EX_PopExecutionFlow` or `EX_Return` makes execution fall through into whatever follows. Prefer an L1 exec edit when one fits.

## New locals (`kismet_add_local`)

- `kismet_add_local(function, name, type | like, description)` adds a property to the function's frame (for the event graph, its persistent frame). Types: `int`, `int64`, `float`, `double`, `bool`, `byte`, `name`, `string`, `text`, `object:<class path>`, `class:<meta class path>`, `softobject:<class path>`, `enum:<enum path>`, `array:<type>`; `like` copies the type, flags and size from an existing property (the only way for structs).
- Flags and size are copied from a property of the same type that the package already has (what the compiler wrote); when there is none Flora uses defaults and says so in the notes: be more careful then and say so in your report.
- Name it like the compiler does for the call it serves, e.g. `CallFunc_DrawCardsFromDeckBySide_cardIDs`, so it can't clash with a real variable.
- Declared locals exist for every edit of the same save: reference them as `{"path": [name], "owner": "export:<function>"}`. `revert_edit` with `local` removes one; edits that use it then fail validation.
- Experimental: the engine lays out the frame itself when it loads the class, so a declaration validates structurally and must still be tested in game. Packages older than UE 4.25 (properties as separate exports) are not supported.

## Errors you can get while writing statements

- `$.Field must not be null: expected ...` — an expression field was `null`. Give an expression object (`{"op": "EX_Nothing"}` for "no value"). A **property reference** may be `null` (it becomes an empty pointer, e.g. the `RValuePointer` of a call without a return value).
- Text with non-ASCII characters (`"你好"`) in `EX_StringConst` is converted to `EX_UnicodeStringConst` automatically and noted in the result; write `EX_UnicodeStringConst` yourself to avoid the note.
- Every error carries the JSON path (`$.Parameters[1].Variable`) and what is expected there; `kismet_schema` (or the shape tables above) shows the fields of an op.
- After recording a statement, the tool checks native calls against the jmap and appends `WARNING native call check` lines: wrong parameter count, out/ref parameters that are not variables, literals of the wrong kind. Fix them before validating; `jmap_call_template` writes the right shape.

## Checking behavior, not only format

`validate_edits` says the package is well-formed; it also reports a **behavior check** of every edited function against the original (new unreachable statements, loops that can never end, execution flow stack that grows or underflows). `diff_edits(function)` lists which statements were added, removed or changed (jumps compare by target statement, so an insertion doesn't make every jump look changed). `audit_flow(function)` audits any function (add `edited: true` for the built version). Loops that already existed are information, not findings.

## Event stubs

`OnPlayedFromHand` as a function is only the **stub** of the event: it calls `ExecuteUbergraph_<Blueprint>` with an entry point. The nodes and the `stmt@` offsets of `get_graph` belong to the ubergraph. `kismet_*` tools given an event name use the ubergraph (like `get_graph`); the stub itself is `stub:<event>`. Tools that find nothing at an offset say when the function is a stub.

## Limits

- New members of the class (variables other nodes can see) can't be created; locals are the only declarations.
- Latent calls (Delay, timelines, async nodes) carry resume offsets and persistent-frame state: don't insert or duplicate them.
- Validation checks structure (offsets, sizes, decompiles), not meaning. Read the `show_function` graph after validating and compare it with what you intended.
