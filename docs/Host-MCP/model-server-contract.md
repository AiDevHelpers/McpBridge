# model-server contract

Client side of the SimpleModelServer router. Ported from TokenSaver `SimpleModelServerCapacityClient`, `OllamaService.GetOllamaStatusAsync` and
`SimpleModelServerHealthCheck`. HTTP only; no SimpleModelServer assemblies.

## Address discovery (aidevtools design, not a port)
1. Default `http://localhost:11435`.
2. If `%ProgramData%\SimpleModelServer\SimpleModelServer.config.json` exists, read only `global.routerHost` and `global.routerPort`.
3. Accept the host only when it is loopback (`localhost`, `127.0.0.1`, `::1`). Any other host: ignore the file, use the default.
4. Missing file, malformed JSON or missing keys: use the default. Never return the file contents or `TrustedApiKeys`.
5. An explicit URL argument to the helper overrides discovery.

## Endpoints used
| call | path | notes |
|---|---|---|
| tags | `GET /api/tags` | `models[].name`. Failure or non-JSON: router "not running". |
| loaded | `GET /api/ps` | `models[].name`. Failure is swallowed: loaded = []. |
| capacity | `GET /api/capacity` | `cpuThreads`, `gpuLayers` (numbers, optional), `gpu.detected` (bool), `maxConcurrentSummaryRequests` (number, clamped to at least 1). |

The router exposes a single virtual model in `/api/tags`; concrete model names appear only in `/api/ps`.

## Status classification
- Unreachable (tags call fails): `running=false`.
- Reachable, zero models: unhealthy ("reachable but no models are pulled").
- Reachable, at least one model: healthy. No loaded models means `vramState="idle"` (keep_alive expired), not unhealthy.
- GPU stats via `nvidia-smi` and host RAM/CPU are out of scope.

## Capacity
- Non-2xx, bad JSON or exception: default `{ gpuDetected=false, maxConcurrentSummaryRequests=1 }`.
- Differs from TokenSaver: a failure is never cached (TokenSaver caches it forever, hiding a router started later). A success is cached for 60 seconds; `refresh=true` bypasses the cache.

## Transport
- Explicit `HttpClient` timeout of 5 seconds per call. Cancellation is honored.

## Tools
- `model_server_status` → `{ ok, url, running, models[], loaded[], vramState, healthy, message }`.
- `model_server_capacity(refresh?)` → `{ ok, url, cpuThreads, gpuLayers, gpuDetected, maxConcurrentSummaryRequests, fromFallback }`.
- Errors: `{ ok:false, error }`. An unreachable router is `ok:true, running:false`, not an error.
