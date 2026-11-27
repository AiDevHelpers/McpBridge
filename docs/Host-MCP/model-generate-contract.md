# model-generate contract

Generate and chat calls against the SimpleModelServer router, for the `model_generate` and `model_chat` tools (U4). HTTP only, through the same address discovery as [model-server-contract.md](model-server-contract.md). Behavior is checked against TokenSaver `OllamaService.cs` (read only) and the router contract `TokenSaver.SimpleModelServer/docs/Server/router-contract.md`.

## Endpoints used

| call | path | request body | success body |
|---|---|---|---|
| generate | `POST /api/generate` | `{ "model", "prompt", "system"?, "stream": false }` | `{ "model", "response", "done": true }` |
| chat | `POST /v1/chat/completions` | `{ "model", "messages": [{ "role", "content" }], "stream": false }` | `choices[0].message.content` |

- `system` is omitted from the body when not given, so the model's default system template applies.
- `stream` is always `false`. Streaming is not exposed.
- Chat uses `/v1/chat/completions`, the path the router contract documents. `architecture.md` also names `/api/chat`; the router contract is the authority, so the helper does not call `/api/chat`.
- `messages[].role` is `system`, `user` or `assistant`. Other roles are refused before any request is sent (validated in U7 for `clean_and_chat`; `model_chat` in U4 applies the same check).

## Warm-up

The router answers `200` with `{ "status": "warming_up", "retry_after": N }` while a model loads, for both generate and chat (router contract, warm-up gate). The helper:
- waits `max(1, N)` seconds and resends, when `N` is missing it waits 3 seconds;
- gives up after 10 attempts and returns the last warm-up reply as an error (`warming_up after 10 attempts`).

## Output cap

The generated text is capped at 20,000 characters. A longer reply is cut to 20,000 characters and returned with `capped: true`. The cap is applied after the reply is read, so the full body is still read from the router.

## Timeout and cancellation

- Default timeout 120 seconds, configurable 1 to 600 seconds (a value outside the range is refused before any request).
- Cancellation from the MCP call is honored and is not reported as a timeout.
- A timeout is reported as `timedOut: true` with `ok: false`.

## Errors

| case | result |
|---|---|
| non-2xx response | `ok: false`, `status` (HTTP code), `error` with the status line and the first 500 characters of the body |
| 2xx without `response` (generate) or without `choices[0].message.content` (chat) | `ok: false`, `error` naming the missing field, with the body cut to 500 characters |
| invalid JSON in a 2xx body | `ok: false`, `error` "reply is not JSON" |
| empty `model` or `prompt` | refused before a request: `ok: false` |
| invalid role or more than 200 messages | refused before a request: `ok: false` |
| router unreachable | `ok: false`, `error` names the URL. Not `running: false`; generate has no status probe. |

Prompts and replies are never written to logs.

## Mapping from TokenSaver `OllamaService.CallModelAsync` overloads

Every overload is mapped or dropped.

| TokenSaver overload | aidevtools | notes |
|---|---|---|
| `CallModelAsync(prompt, ct)` | `model_generate` with `model` required | TokenSaver's `EffectiveModel` comes from its config. aidevtools has no model config, so `model` is required. |
| `CallModelOnEndpointAsync(prompt, endpointName, ct)` | dropped | Endpoint pinning is client-side in TokenSaver's `OllamaEndpointRegistry` (sent as a request option). The router contract documents no endpoint selection, so there is nothing to pin to through the router. Revisit if the router gains it. |
| `CallModelAsync(model, prompt, ct)` | `model_generate` | Mapped. |
| `CallModelAsync(model, userPrompt, systemPrompt, ct)` | `model_generate` with `system` | Mapped. |
| `CallModelAsync(model, userPrompt, systemPrompt, endpointName, ct)` | `model_generate` with `system`; `endpointName` dropped | Same reason as the endpoint overload above. |
| `IPromptAdapter` applied to `userPrompt` | dropped | TokenSaver-specific prompt formatting. aidevtools sends the prompt unchanged. |
| `PostGenerateAsync` warm-up retry (10 attempts, `retry_after`) | mapped (see Warm-up) | |
| `EmbedAsync` (`/api/embed`) | dropped from this plan | Not part of the generate and chat units. Add later as `model_embed` if needed. |
| `WaitForReadyAsync` | dropped | `model_server_status` (U3 of the earlier plan) covers reachability. |
| `GetOllamaStatusAsync`, `GetGpuStatsAsync` | dropped | Covered by `model_server_status`; GPU stats are out of scope there too. |

## Agent-type inference

The router infers an agent type from headers, query, body and prompt (router contract). The helper sends none of those, so the router applies its default behavior. The `research` agent type (web-or-local routing) is not used.

## Tools (planned, U4)

- `model_generate(model, prompt, system?, timeoutSeconds?)` → `{ ok, url, model, response, capped, done }`
- `model_chat(model, messages, timeoutSeconds?)` → `{ ok, url, model, content, capped }`

Both return `{ ok: false, error }` on failure, as described above.
