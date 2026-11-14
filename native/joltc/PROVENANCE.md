# joltc provenance (vendored, patched)

The viewer uses the grid's **patched** joltc (per-system TempAllocator), never the stock
`JoltPhysics.Native 1.0.4` joltc. What the patch does and how to rebuild it: `win-x64/README.md`
(copied from the source below, with grid-specific names, local paths and dates generalised).

| Item | Value |
|---|---|
| Source repository | the grid's Tranquillity (OpenSim) fork, read with non-writing git only |
| Branch | `feature/ais-v3` |
| Commit | `1bb1131cfdc447929d42c1e17f9aaeac8d7b9c22` |
| Extracted | `git show feature/ais-v3:<path> > <file>` |

| Vendored file | Source path in that commit | SHA-256 |
|---|---|---|
| `win-x64/joltc.dll` | the grid's Jolt physics module, `runtimes/win-x64/native/joltc.dll` (blob `2d2bfccb6ce07fdd4ce4f3f0a500af600db9e644`) | `16AF76381387DADD7DFA5E10D6E3AD025AB624F22187D7442D1BDB88146743B5` |
| `win-x64/README.md` | `native/joltc/README.md` | adapted (see above); the source blob's SHA-256 began `1C94394051C11BE5` |
| `win-x64/per-system-tempallocator.patch` | `native/joltc/per-system-tempallocator.patch` | `037AE504FC03AC95…` (byte-identical to the blob before the licence notice header was added above its first diff line) |

## Hash check at extraction (all three must match)

| Copy | SHA-256 |
|---|---|
| Expected, from the source README | `16AF76381387DADD7DFA5E10D6E3AD025AB624F22187D7442D1BDB88146743B5` |
| Extracted `win-x64/joltc.dll` | `16AF76381387DADD7DFA5E10D6E3AD025AB624F22187D7442D1BDB88146743B5` |
| Live grid regionserver `joltc.dll` (read-only) | `16AF76381387DADD7DFA5E10D6E3AD025AB624F22187D7442D1BDB88146743B5` |

The joltc blob is the same in the grid's job-pool teardown commit (`a8e918b3cf`) and its deploy integration
(`c0193d1bbf`).

**Stock (must never be accepted):** `67BECFC70CFBDA643AB9B75ABA895042900C3E339B001080BA4107E4929B0910`.

## Guard

`tools/assert-joltc.ps1 -Path <dir>` fails unless every `joltc*.dll` under `<dir>` is the patched build.
`JoltcGuardTests` runs it on the test output and proves it rejects the stock native.

## Re-pointing

When the grid's Jolt work is ported to NGC develop, the pin and this source are re-pointed there (ADR-009).

## Linux builds (linux-x64, linux-arm64): upstream, unpatched

The Linux exports use joltc exactly as the JoltPhysics.Native 1.0.4 package ships it for Linux. The patch above is not
applied, because the viewer does not need it to be correct:

- The patch exists for a process that steps several physics systems on several threads at once (a region server running
  regions in parallel). The stock build shares one scratch allocator between all systems, and two systems updating at the
  same time corrupt it.
- The viewer creates a physics system in one place, `core/Viewer.Core/Physics/PhysicsWorld.cs`, and creates worlds only in
  `PhysicsLifecycleProbe.Run` (the start-up check and the lifecycle smoke). There each world is made, stepped once and
  disposed before the next (a `using` block in one loop on one thread), so two systems never exist at the same time.
- If the viewer ever steps more than one system at a time, the Linux builds must be rebuilt from the same source with the
  patch, as the Windows one was (`win-x64/README.md`), and this section replaced.

| Item | Value |
|---|---|
| Package | `JoltPhysics.Native` 1.0.4 from nuget.org (signed), the version every project pins (`Directory.Packages.props`) |
| Package content hash (SHA-512, as in every `packages.lock.json`) | `cH8A3b/sKrrQd4qthnFzLL4e+DonLxgCjYFB9ziRC7urlrKna6LeNF5jDq+4SeXTqUntGU7q8/jddHAl06HLMQ==` |
| Package source repository (nuspec) | https://github.com/amerkoleci/joltc, commit `ba2f3068c391d33788d33ebe53dc0ad4f7c15be4` |
| Built by | upstream's own CI, as packaged; not rebuilt here |
| Extracted | the files below, byte for byte, read from inside the `.nupkg` and compared with the restored copies |

| Vendored file | Path in the package | SHA-256 |
|---|---|---|
| `linux-x64/libjoltc.so` (ELF 64-bit, x86-64) | `runtimes/linux-x64/native/libjoltc.so` | `5FC051708BDD05031A816796612A2F87E17ED32CC198F195A43B2305B3D990FD` |
| `linux-arm64/libjoltc.so` (ELF 64-bit, AArch64) | `runtimes/linux-arm64/native/libjoltc.so` | `FDE70508C826370B5CF6BB54C6EA9CEEAB0C37C826BCD71579EBB9B4AC3E0D61` |
| `linux-*/LICENSE-joltc.txt`, `linux-*/LICENSE-JoltPhysics.txt` | copies of `win-x64/`'s | |

Both need glibc 2.34 or newer (their highest versioned glibc symbol). The Linux export takes the file for its architecture
(`host/Viewer.Godot/Viewer.Godot.csproj`) and leaves the Windows `joltc.dll` out. `tools/package.ps1` checks that every
Linux export holds exactly one `libjoltc.so`, byte-identical to the vendored one, and no `joltc*.dll`.
