# UnityVecEnv: style and cleanup review

Written 2026-10-17 as the sibling of `BoundaryValueBTRL/docs/style_and_cleanup_review.md`.
Scope: the Python package under `Python/unity_vecenv` and the C# under `Unity/`
(Runtime and Editor scripts). Generated protobuf code, prefabs, scenes and other
Unity assets are out of scope. Reference revision for "how it was written":
`035b16f` (last May commit) with `8846d32` (April 25 release) behind it.
Reviewed revision: `456dc15` (HEAD). No code was changed for the review itself.
Step 1 of section 5 (pure deletions) was applied on 2026-10-17; see the note
under section 4.1 for the items that were kept. Step 2 (the comment pass: D1,
D4, D7) was applied the same day; moving the reload note into
`docs/python-usage.md` also documented `reload_scene` and `reinitialize`, which
covers part of D8. Step 3 (D2, D6) was applied the same day: `initialize_environment`
now sends what its callers already normalized, and the worker has one `_submit`.
Step 4 (D8) was applied the same day: reset seeds and the launch arguments are
documented in `docs/python-usage.md`, and the launch-argument table in
`docs/unity-usage.md`, which listed flag names the build never parsed
(`--port`, `--num-agents`), now matches `Bootstrap.ParseCommandLine`.

Verification labels: **verified** means checked with a grep across both this
repository and `BoundaryValueBTRL` (code, scenes and prefabs by GUID);
**judgement** means a reading of the code.

## 1. How the repository grew

Commits by month in 2026: Jan 39, Feb 25, Mar 40, Apr 12, May 2, Jun 1, Jul 9, Aug 1.
Everything after May is feature work in six commits plus four release bumps
(v0.1.6 to v0.1.9): scene loading over the initialize endpoint (`961aa3e`),
initialization parameters and `SameStep` autoreset (`84db233`), reset seeding
(`744d274`), CoreCLR-safe shutdown (`82d62b4`), and `reinitialize()` with scene
reload and per-agent random state (`456dc15`).

Diff `035b16f..HEAD`: 24 files, +2094 / -299 lines.

| File | May | HEAD | What was added |
| --- | ---: | ---: | --- |
| `Networking/CommunicatorHttpServer.cs` | 922 | 1047 | `Dispose` hardening, `ResetStaticState`, step gate |
| `environment/unity_vector_env.py` | 631 | 867 | `reinitialize`, `_normalize_*` helpers, seeds, `env_parameters` |
| `Core/GymVecEnvManager.cs` | 388 | 596 | `ApplySceneSelection`, seed map, init-parameter accessors, user strings |
| `environment/unity_multi_vector_env.py` | 428 | 494 | `reinitialize`, `_rebuild_layout`, `submit_reinitialize` |
| `Core/GymAgentManager.cs` | 196 | 248 | ordered spawning, environment templates |
| `Message/DefaultMessageMapper.cs` | 128 | 178 | parameters, seeds, scene fields |
| `Core/GymAgent.cs` | 311 | 338 | seeded `DoReset` |
| `Core/InitializationParameterUtils.cs` | - | 112 | new |
| `Core/GymUserStringOverlay.cs` | - | 56 | new |
| `unity_vecenv/cli.py`, `tests/` | - | 83 + 37 | new (`onnx-rename` CLI, five tests) |

The visual observation stack (`CameraVisualObservationSource.cs` 1184,
`InferenceHelper.cs` 449, HDRP bridge 330) is unchanged since May.

## 2. How the code was written in April/May

- **U1. One class per module, logic inline.** `UnityVectorEnv` did everything in
  methods; the only module-level helpers lived in `spaces.py`. Neither Python
  environment module had a docstring (0 in both). The whole `environment/`
  directory carried 16 comment lines, mostly section labels such as `# workers`,
  `# slices`, `# --- async API ---`.
- **U2. C# without doc comments.** `GymVecEnvManager.cs` had no `///` block and
  eight `//` lines, of which several were TODOs. The longest method was
  `FixedUpdate` at 44 lines. One responsibility per file: `Bootstrap` parses the
  command line and owns scene callbacks, `GymAgentManager` spawns, the manager
  runs the step loop, the HTTP server speaks protobuf.
- **U3. Small public surface.** Python exported `UnityVectorEnv`,
  `FlattenedVectorEnvThreaded` and `export_unity_onnx`; C# consumers subclass
  `GymAgent` and use the message types.
- **U4. Fail loud, catch little.** `RuntimeError` on zero environments; `Dispose`
  was four lines with a TODO, no swallowed exceptions anywhere.

## 3. Primary differences at HEAD

This repository did not drift the way `BoundaryValueBTRL` did. Comment density
is still low (Python `environment/` 16 comment lines of 1702; C# `Core/` 29 of
1594), the TODO count fell from six to three, and the new comments are mostly
one-paragraph why-notes of the kind the BoundaryValueBTRL review said to keep
(park the step loop before yielding, pin the shared random stream before a
reload, continue un-reloaded rather than hang the caller). The differences
worth naming are small:

- **D1. Docstrings on private helpers (new genre, judgement).**
  `_apply_environment_description` (5 lines), `_rebuild_layout` (6 lines) and
  the two `reinitialize` docstrings (9 and 4 lines) explain in prose what the
  method name already says. May had none. Same rule as in the BoundaryValueBTRL
  review: one line or nothing on private helpers.
- **D2. A `_normalize_*` family at module level.** Six functions at
  `unity_vector_env.py:38-160` (`_normalize_ui_strings`, `_validate_reset_seed`,
  `_normalize_reset_seeds`, `_normalize_autoreset_mode`,
  `_normalize_environment_parameters`, `_environment_parameter_to_proto`).
  Justified by reuse from `__init__` and `reinitialize`, but `reinitialize`
  normalizes `env_parameters` and `autoreset_mode` and then
  `initialize_environment` normalizes both again. One of the two passes can go.
- **D3. Wrapper methods on the manager (verified).**
  `GymVecEnvManager.TryGetInitializationParameter`, `GetInitializationString`,
  `GetInitializationFloat` and `GetInitializationInt` are one-line forwards to
  `InitializationParameterUtils`. BoundaryValueBTRL calls `GetInitializationString`
  four times and `GetInitializationInt` once; the other two have no caller in
  either repository.
- **D4. Swallowed exceptions in the HTTP server (verified).** `Dispose` grew from
  4 to 66 lines with four `catch { // ignored }` blocks; two more sit at lines
  819 and 831 and one "Ignore shutdown errors" at line 55. Seven silent catches in
  one file where May had none. The `// ignored` comment is the only noise genre
  that appeared since May. Either one small `TryQuietly(Action)` helper or
  catching the two types the listener throws (`ObjectDisposedException`,
  `HttpListenerException`) would say why each is safe to ignore.
- **D5. `ResetStaticState` in five classes.** Needed for Enter Play Mode without
  domain reload; the pattern is identical in `Bootstrap`, `GymVecEnvManager`,
  `CommunicatorHttpServer`, `AgentVisualObservationSource` and the HDRP bridge.
  Fine as is; just a pattern to keep uniform.
- **D6. Three `submit_*` methods on `_ThreadWorker`.** `submit_step`,
  `submit_reset` and `submit_reinitialize` have identical five-line bodies apart
  from the command tuple; `_loop` dispatches on the tuple tag. A single
  `_submit(command)` removes two copies.
- **D7. One design note in code (judgement).** The five-line comment above
  `Random.InitState(0)` in `ApplySceneSelection` explains when reloads are and
  are not identical to a cold launch. It is a user-facing contract and belongs
  next to `reload_scene` in `docs/unity-usage.md`, with a one-line pointer left
  in the code.
- **D8. Docs lag the API (verified).** `docs/python-usage.md` documents
  `reinitialize` and `ui_strings` but not `reload_scene`, the per-environment
  reset seeds, or the `-decisionperiod` launch argument. Nothing in the docs is
  stale; it is only missing.

## 4. What is unused

`unity-vecenv` is a published package (tags v0.1.7 to v0.1.9, `onnx` and `dev`
extras, an `unity-vecenv` console script), so "not used by the paper" is not the
same as dead. The two lists are kept apart.

### 4.1 Dead in the package itself (no caller in either repository, verified)

| Item | Size | Evidence |
| --- | ---: | --- |
| `environment/unity_multi_vec_env.py` | 5 lines | Compatibility shim; nothing imports it, no doc mentions it. BoundaryValueBTRL imports `unity_multi_vector_env` directly. |
| `onnx_utilities/onnx_exporter.py` (`export_unity_onnx_simple`) | 30 lines | Only re-exported from `onnx_utilities/__init__.py`; no caller. BoundaryValueBTRL calls `export_unity_onnx` directly. |
| `Unity/Runtime/Scripts/VecEnv/ScreenshotManager.cs` | 98 lines | No C#, Python, scene or prefab reference. The only trace is the TODO at `unity_vector_env.py:392`. The protobuf `Screenshot` message is generated code and can stay. |
| `Extensions.RangeNormalize` | 4 lines | No caller (`ForceNormalizeVector` has six). |
| `NetworkUtils.ToProtoQuaternion`, `ToProtoVector` | 18 lines | No caller (`ToUnityVector` x2, `ToUnityQuaternion` x1 are used). |
| `GymVecEnvManager.TryGetInitializationParameter`, `GetInitializationFloat` | 10 lines | See D3. Kept, see below. |
| `Bootstrap.cs:216` | 1 line | Commented-out `textBox3.Text` line from the sample this was copied from. |
| `Unity/Editor/Scripts/SplitSelectedMeshBySubmesh.cs` | 76 lines | Editor menu utility unrelated to the vec-env contract, no reference. Keep if it is still used from the menu; it is a judgement call, not dead code. |

Kept when step 1 was applied: `GetInitializationFloat` is documented in
`docs/unity-usage.md` as one of the three typed accessors (string, float, int),
and the owner chose to keep `TryGetInitializationParameter` with it, so the
accessor family stays whole although two members have no caller yet.
`SplitSelectedMeshBySubmesh.cs` was left in place pending a decision.

### 4.2 Live in the package, not on the paper's path (keep unless the package is trimmed to the paper)

- **Visual observations (about 2,000 lines, verified).**
  `CameraVisualObservationSource.cs`, `AgentVisualObservationSource.cs`,
  `AsyncGpuReadbackSupport.cs`, the HDRP bridge, the visual paths of
  `InferenceHelper`, `BuildBatchedVisualObservations` and `LogVisualSummaryOnce`
  in the HTTP server (111 lines), and `_decode_visual_observations_to_numpy`
  plus the visual spaces in Python. In BoundaryValueBTRL the only user is the
  `WatercraftObstacleVisual` scene through `obstacle_env_visual.prefab` and
  `watercraftObstacleVisual.prefab`. No tracked manifest, trainer or the env
  factory names that scene; the paper scenes are `WatercraftPushMove`,
  `WatercraftLoopFuelHazard`, `SamPoolAlign`, `SamPoolMoveAlign` and
  `WatercraftCombined`. The matching visual code in BoundaryValueBTRL
  (`learning/agent.py`, 56 mentions) is that repository's question, not this one's.
- **Unity-side inference (`InferenceHelper.cs`, 449 lines).** Created by
  `GymAgent` only when a model asset is assigned; no BoundaryValueBTRL prefab
  assigns one and the paper evaluates policies from Python through
  `paper_eval/rollout.py`. The ONNX export that feeds it is on the paper path.
- **`unity-vecenv onnx-rename` CLI and `onnx_rename.py`.** Not used by
  BoundaryValueBTRL; two of the five tests cover it.
- **`FlyCamera.cs`.** Used in five BoundaryValueBTRL scenes (`SamPoolAlign`,
  `SamPoolMoveAlign`, `WatercraftCombined`, `WatercraftCombined2`,
  `WatercraftLoop`). Live.

Everything else is exercised by the paper: BoundaryValueBTRL passes every
`UnityVectorEnv` constructor argument except `reload_scene` (which it passes to
`reinitialize` instead), uses `FlattenedVectorEnvThreaded`, `ui_strings`,
`autoreset_mode`, `env_parameters` and the typed custom info, and on the C#
side subclasses `GymAgent` and uses `EnvironmentState`, `AgentAction`,
`AgentObservation`, `EnvironmentParameterCollector`, `CustomInfoBuilder` and the
two initialization accessors named in D3.

### 4.3 Tests

`tests/` is 37 lines in five tests: CLI help, CLI argument validation, a protobuf
round trip, the public exports and the version. Nothing covers
`FlattenedVectorEnvThreaded` (slicing, info merging, `_rebuild_layout` after a
reinitialize) or the reset-seed normalization, which is where the consumer's
risk sits. Not a cleanup item, a gap.

### 4.4 Side note on the consumer

`BoundaryValueBTRL/python/boundary_value_btrl/build/lib` is a stale, gitignored
build copy that still holds deleted modules (`wm_ppo_trainer`). Harmless, but it
shows up in greps; delete it locally.

## 5. Suggested cleanup order, if asked

1. **Pure deletions** (section 4.1): the shim module, `onnx_exporter.py` with
   its `__init__` export, `ScreenshotManager.cs` and its `.meta` plus the Python
   TODO line, the three unused extension methods, the commented-out line.
   Decide separately on `SplitSelectedMeshBySubmesh.cs`.
2. **Comment pass** (D1, D4, D7): private-helper docstrings to one line, the
   seven silent catches to one helper or typed catches, the reload design note
   to the docs.
3. **Dedup** (D2, D6): one normalization pass on the reinitialize path, one
   `_submit` on the worker.
4. **Docs** (D8): add `reload_scene`, reset seeds and `-decisionperiod` to
   `docs/python-usage.md`.

None of the steps touch the visual stack, the CLI, the inference helper or the
wire protocol; those stay as package features. Steps 1 to 3 change roughly 250
lines and bump the package to v0.1.10.

## 6. Docs review (2026-10-17)

`README.md` and `Python/unity_vecenv/README.md` were rewritten on 2026-10-17 in
the shape of https://github.com/banesullivan/README, and the development
instructions moved to `docs/development.md`. The two guides were reviewed but
not rewritten. These are the findings to act on, each checked against the code.

### docs/python-usage.md

- Wrong: `executable_path` "None to connect to an already-running instance"
  (only `start_process=False` connects); `port` "incremented if busy" (with
  `start_process=False` it searches upward for a port that is in use); the
  constructor table omits `batch_mode`; custom info "(num_envs,) float" (also
  int32, bool, Vector3 as (num_envs, 3) and Quaternion as (num_envs, 4));
  SAME_STEP "stores final_observation" (both modes do); "pass callables so each
  instance is created in the right thread" (the factories run on the calling
  thread); the onnx-rename section lacks `--no-validate`.
- Missing: `export_unity_onnx` and `UnityInferenceExportPolicy`, the only export
  entry point and the one BoundaryValueBTRL uses; visual observations on the
  Python side (a Dict space with `state` plus one key per camera);
  `env.environment_parameters`; a prerequisites line (a player or the Editor in
  Play Mode, same machine, port 50010); the two common failures (connection
  failed after 20 attempts; Unity quitting after `timeoutMilliseconds`).
- Noise: the third copy of the install text; Communication details (retries,
  byte encodings); the launch-argument table duplicated from the Unity guide;
  the Unity random-stream paragraphs (keep the three code lines); the private
  import path for `FlattenedVectorEnvThreaded` (use the package export).

### docs/unity-usage.md

- Wrong: Done or Truncated "resets before the next observation is sent"
  (NextStep sends the terminal observation first; only SameStep resets first);
  an explicit reset "fires only the observation events" (`PreStepReset` and
  `PostStepReset` fire too); the `GymEnvironmentTemplate` table omits
  `rowlength` and `rowOffset`; "opt not to send the agent count"
  (`UnityVectorEnv` always sends it; `num_envs=0` is the only way);
  `Rigidbody.velocity` in the example (Unity 6 uses `linearVelocity`); "Server
  Build" (Dedicated Server in Unity 6, unverified).
- Missing: visual observations (`visualObservationSources`,
  `CameraVisualObservationSource`); `CollectEnvironmentParameters`; a first-run
  checklist (scene in Build Settings, press Play, expect "Communication Server
  started at http://localhost:50010/"); that the Editor ignores `-scene` and the
  other command-line arguments; how `physics_steps_per_action` relates to
  `FixedUpdate`; the HDRP define.
- Noise: the install copy with a hard-coded `#v0.1.9`; the execution-order
  table; changelog sentences ("now supports typed values", "still supported for
  backward compatibility"); nine lifecycle events before the walkthrough reaches
  running without Python.

### Outside the files

- GitHub Releases is empty while tags v0.1.0 to v0.1.9 exist; the READMEs and
  `pyproject.toml` now link the tags page.
- The repository has no description or topics on GitHub.
