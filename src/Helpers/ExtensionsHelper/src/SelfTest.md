# ExtensionsHelper self test

Tools: `ExtensionList`, `ExtensionInfo`, `ExtensionAddPlan`, `ExtensionAddConfirm`, `ExtensionWorkspaceSetup`.

## Full self test

Fixture: a scratch folder with a `.sln`, and a folder for `.vscode`.

- `ExtensionList` (ide = VsCode): PASS when `ok` is true and `items` is a list.
- `ExtensionInfo` (ide = VsCode, id = an installed id): PASS when `ok` is true.
- `ExtensionInfo` (ide = VisualStudio, id = a known Marketplace id): PASS when `ok` is true. Needs network. Record NOT RUN when offline.
- `ExtensionAddPlan` (ide = VsCode, id = an id that is not installed): PASS when `ok` is true and a `token` is returned. Nothing installs.
- `ExtensionAddConfirm` (token from the plan): PASS when the reply holds the exit code and output. This installs the extension, so run it only with the operator's approval.
- `ExtensionAddConfirm` (same token again): PASS when `ok` is false.
- `ExtensionWorkspaceSetup` (folderPath = fixture, solutionPath = fixture): PASS when `.vscode/extensions.json` lists the C# extensions and existing keys are kept.

Record each as PASS, FAIL or NOT RUN with the exact arguments and reply.
