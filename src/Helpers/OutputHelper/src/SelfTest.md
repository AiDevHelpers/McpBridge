# OutputHelper self test

Tools: `OutputChannelSet`, `OutputWrite`.

## Full self test

Fixture: a scratch solution path (the file need not exist) and a running VS Code or Visual Studio instance for the live step.

- `OutputChannelSet` (solutionPath = fixture, channel = `SelfTest`, ide = `VsCode`): PASS when `ok` is true, and `.claude/aidevhelpers.json` next to the fixture holds `outputChannels.SelfTest` = `VsCode`.
- `OutputChannelSet` (ide = `Notepad`): PASS when `ok` is false and `error` names the allowed IDEs.
- `OutputWrite` (solutionPath = fixture, channel = `SelfTest`, text = `self test`): PASS when `ok` is true and `written` equals the text length. Live check: the channel `SelfTest` appears in the IDE with the text. This needs a running IDE; record NOT RUN when none is open.
- `OutputWrite` (channel = `Unset`, no stored IDE, no ide argument): PASS when `ok` is false and `error` says no IDE is set.

Record each as PASS, FAIL or NOT RUN with the exact arguments and reply.
