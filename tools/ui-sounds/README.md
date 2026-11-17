# Interface sound generator

Seventeen of the interface sounds are recordings: public-domain (CC0 1.0) sounds and sounds made with ChatGPT, each resampled to
16-bit mono 44.1 kHz and brought to the loudness of the official viewer's sound for the same event (`sl-targets.json`) with its peak
no higher than -1 dBFS. `host/Viewer.Shell/Sound/UiSoundRecordings.cs` lists them, and `THIRD-PARTY-NOTICES.md` credits each one.
This tool does not write them and the Sound Tuner does not offer them.

This tool makes the others from code: the chat ping, both health reduction sounds, the restart warning, the snapshot, the typing
sound, and our footstep. It writes them as 16-bit mono 44.1 kHz WAV files into `host/Viewer.Shell/Assets/Sounds`. Those WAVs are
this tool's output; they are committed so a build needs no extra step, and a test (`UiSoundFileTests`) fails if they differ from
what the generator makes now.

The recipes and the synthesizer live in the shell, `host/Viewer.Shell/Sound/UiSoundSynth.cs`; this tool compiles those same
files, and the viewer's Sound Tuner uses them too, so the tuner, the generator and the shipped files make the same bytes.

## Fitting the sounds to measured targets

The interface sounds are fitted to measurements of the official viewer's interface sounds: numbers only (length, pitch and
strongest partials, attack, decay to -20 and -40 dB, spectral centroid, tonal share, integrated loudness in LUFS, pitch
movement, number of bursts), kept in `sl-targets.json`. No audio from anywhere is read. Seven settings have no measurement;
`UiSoundFit.Unmeasured` derives each one's target from a neighbour's, and its recipe's note says "no SL reference".

1. Write both candidates of every generated sound's target into a folder (nothing in the repository changes):

   ```
   dotnet run --project tools/ui-sounds/UiSoundGen.csproj -c Release -- --fit tools/ui-sounds/sl-targets.json --candidates <folder>
   ```

   Candidate A is plain sines on the measured partials (tonal) or noise in a fixed band (noisy); candidate B is an FM bell
   (tonal) or noise in a gliding band with a low thump (noisy). The folder also gets `targets.json`, the targets as fitted.
2. Measure both candidates and choose the one closer to its target; write the choices as `Setting=A` or `Setting=B` lines
   into `sl-choices.txt`.
3. Fold the chosen candidates into the committed table and write the sounds:

   ```
   dotnet run --project tools/ui-sounds/UiSoundGen.csproj -c Release -- --fit tools/ui-sounds/sl-targets.json --choose tools/ui-sounds/sl-choices.txt
   ```

A fitted recipe (`L(...)` in the table) is brought to its target loudness with its peak no higher than -1 dBFS; the others
(`R(...)`) to a -6 dBFS peak.

## Changing a sound with the Sound Tuner

1. In the viewer, open Preferences > Sound and press **Tune…**.
2. Pick a sound. Each number of its recipe is a slider; moving one makes the sound again and plays it.
3. **Save** keeps the tuned recipes in the viewer's user folder, in `ui-sounds-tuned.json`; the viewer plays them from then on.
   **Reset** puts the sound shown back to its committed recipe.
4. To make the tuning the shipped sound, fold the file into the committed table and write the files, from the repository root:

   ```
   dotnet run --project tools/ui-sounds/UiSoundGen.csproj -c Release -- --fold <path to ui-sounds-tuned.json>
   ```

   This rewrites the block between `// recipes:begin` and `// recipes:end` in `UiSoundSynth.cs` and writes the sounds from the
   folded table. Commit both.

## Changing a sound by hand

1. Change the numbers of its entry between the `recipes:begin` and `recipes:end` lines of `UiSoundSynth.cs` (lengths in
   milliseconds, pitches in hertz, levels relative to the other voices of the same sound; an `R(...)` sound is brought to a -6 dBFS
   peak afterwards, an `L(...)` sound to its loudness target).
2. Run the generator from the repository root:

   ```
   dotnet run --project tools/ui-sounds/UiSoundGen.csproj -c Release
   ```

   With one folder argument it writes into that folder instead, for trying a change without touching the repository.
3. Run the viewer and use Preferences > Sound: every sound has a Play button.

The same recipes always give the same bytes (the noise is seeded), so a change shows up in `git diff` only for the sounds
whose numbers changed.

## Voices

- `Sine(start, attack, decay, level, Hz[, end Hz])`: a sine, optionally gliding to the end pitch.
- `Bell(start, attack, decay, level, Hz, ratio, index)`: an FM bell; ratio is the modulator's pitch against the note's,
  index how bright the start is (the brightness fades with the note).
- `Noise(start, attack, decay, level, low Hz, high Hz)`: noise in a fixed band.
- `Swish(start, attack, decay, level, from Hz, to Hz)`: noise in a band that glides (the window swishes, the teleport).
- A voice that no helper fits (a tuned bell given a pitch glide, for example) is written as `new Layer(...)` with every number named.

`decay` is the time from the peak down to -60 dB.
