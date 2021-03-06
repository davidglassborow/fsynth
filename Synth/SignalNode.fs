﻿namespace Synth
open System

type Note =
    | C | CS | D | DS | E | F | FS | G | GS | A | AS | B
    
    override this.ToString () =
        match this with
        | C -> "C" | CS -> "C#"
        | D -> "D" | DS -> "D#"
        | E -> "E"
        | F -> "F" | FS -> "F#"
        | G -> "G" | GS -> "G#"
        | A -> "A" | AS -> "A#"
        | B -> "B"
    
    static member allNotes = [C; CS; D; DS; E; F; FS; G; GS; A; AS; B]

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Note =
    let isSharp = function | CS | DS | FS | GS | AS -> true | _ -> false
    let isNatural = isSharp >> not
    
    // There are 3 concepts going on here: "note" means note+octave (e.g. A 4 or C 3), "key index" is one
    // number representing a note on a piano keyboard, and "frequency" is just that (hertz)
    
    let noteToKeyIndexMapping =
        [C; CS; D; DS; E; F; FS; G; GS; A; AS; B]
        |> List.mapi (fun i n -> n, (i + 4))
        |> Map.ofList
    let keyIndexToNoteMapping =
        [A; AS; B; C; CS; D; DS; E; F; FS; G; GS]
        |> List.mapi (fun i n -> i, n)
        |> Map.ofList
    let noteToKeyIndex (note, octave) = (12 * (octave - 1)) + noteToKeyIndexMapping.[note]
    let keyIndexToNote keyIndex =
        let octave = (keyIndex + 8) / 12
        let note = keyIndexToNoteMapping.[(keyIndex - 1) % 12]
        note, octave
    let keyIndexToFrequency keyIndex =
        2. ** (float (keyIndex - 49) / 12.) * 440.
    let frequencyToKeyIndex frequency =
        let log2 n = log n / log 2.
        (12. * log2 (frequency / 440.)) + 49.
    let noteFrequencies =
        [|yield GS, 0; yield A, 0; yield AS, 0; yield B, 0
          for octave in 1..8 do
             for note in Note.allNotes do
                 yield (note, octave)|]
        |> Array.map (fun (note, octave) -> keyIndexToFrequency (noteToKeyIndex (note, octave)))
    let noteToFrequency (note, octave) = noteFrequencies.[noteToKeyIndex(note, octave)]

type GeneratorState =
    { /// A function that creates the core waveform from radians (repeats every 2π)
      genFunc: (float -> float)
      /// Location within the repeating wave function, in radians
      phase: float }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module GeneratorState =
    let update deltaTime frequency state = { state with phase = (state.phase + (frequency * deltaTime)) % 1. }
    let sample state = state.genFunc state.phase

type SignalNodeID = int
type SignalParameter =
    /// Parameter is set to a single unchanging value
    | Constant of float
    /// Parameter is controlled by the output of another node
    | Input of SignalNodeID
    /// Parameter is controlled by the frequency of the note that is played
    | MidiInput
type SignalNode =
    | GeneratorNode of
        generator: GeneratorState
        * frequency: SignalParameter * amplitude: SignalParameter * bias: SignalParameter
    | MixerNode of masterAmplitude:SignalParameter * signalsAndAmplitudes:(SignalParameter * SignalParameter) list
    | ADSREnvelopeNode of attack: float * decay: float * sustain: float * release: float * releaseFrom: float

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module SignalNode =
    let rec sampleParameter midiInputFreq time timeSinceRelease (nodes: Map<_,_>) parameter =
        match parameter with
        | Constant(x) -> x
        | Input(nodeId) -> sample midiInputFreq time timeSinceRelease nodes (nodes.[nodeId])
        | MidiInput -> midiInputFreq
    
    and sampleADSR time timeSinceRelease (attack, decay, sustain, release, releaseFrom) =
        // a1 and a2 are the two amplitudes to interpolate between, and x is a value from 0 to 1
        // indicating how far between a1 and a2 to interpolate
        let x, a1, a2 =
            match timeSinceRelease with
            | None ->
                let tDecay = attack + decay
                if time < attack then time / attack, 0., 1.
                elif time < attack + decay then (time - attack) / decay, 1., sustain
                else 0., sustain, sustain
            | Some(timeSinceRelease) -> timeSinceRelease / release, releaseFrom, 0.
        // interpolate
        a1 + (x * (a2 - a1))
    
    and sample midiInputFreq (time: float) (timeSinceRelease: float option) nodes node =
        match node with
        | GeneratorNode(state, freq, ampl, bias) ->
            let ampl = sampleParameter midiInputFreq time timeSinceRelease nodes ampl
            let bias = sampleParameter midiInputFreq time timeSinceRelease nodes bias
            GeneratorState.sample state * ampl + bias
        | MixerNode(masterAmpl, signals) ->
            let output =
                signals
                |> List.map (fun (signal, gain) ->
                    sampleParameter midiInputFreq time timeSinceRelease nodes signal
                    * sampleParameter midiInputFreq time timeSinceRelease nodes gain)
                |> List.reduce (+)
            output * (sampleParameter midiInputFreq time timeSinceRelease nodes masterAmpl)
        // t = duration, a = amplitude
        | ADSREnvelopeNode(attack, decay, sustain, release, releaseFrom) -> sampleADSR time timeSinceRelease (attack, decay, sustain, release, releaseFrom)
    
    let update midiInputFreq deltaTime time timeSinceRelease nodes node =
        match node with
        | GeneratorNode(state, freq, ampl, bias) ->
            GeneratorNode(GeneratorState.update deltaTime (sampleParameter midiInputFreq time timeSinceRelease nodes freq) state, freq, ampl, bias)
        | MixerNode(_) -> node
        | ADSREnvelopeNode(attack, decay, sustain, release, releaseFrom) ->
            // releaseFrom needs to be set and stay at the last value the envelope was at before entering release
            // in other words, releaseFrom cannot change when the release starts
            match timeSinceRelease with
            | Some(timeSinceRelease) -> node
            | None ->
                let newReleaseFrom = sampleADSR time None (attack, decay, sustain, release, releaseFrom)
                ADSREnvelopeNode(attack, decay, sustain, release, newReleaseFrom)

type NoteInstance =
    { id: int
      frequency: float
      nodes: Map<SignalNodeID, SignalNode>
      time: float
      timeSinceRelease: float option }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module NoteInstance =
    let sample outputNodeId noteInstance =
        match Map.tryFind outputNodeId noteInstance.nodes with
        | Some(node) ->
            SignalNode.sample
                noteInstance.frequency
                noteInstance.time noteInstance.timeSinceRelease
                noteInstance.nodes node
        | None -> failwith (sprintf "(in sample) Node %i not found in %A" outputNodeId noteInstance.nodes)
    
    let sampleMany outputNodeId noteInstances = List.fold (fun acc noteInstance -> sample outputNodeId noteInstance + acc) 0. noteInstances
    
    let update outputNodeId deltaTime noteInstance =
        { noteInstance with
            nodes =
                match Map.tryFind outputNodeId noteInstance.nodes with
                | Some(node) ->
                    noteInstance.nodes |> Map.map (fun id node ->
                        SignalNode.update
                            noteInstance.frequency deltaTime
                            noteInstance.time noteInstance.timeSinceRelease
                            noteInstance.nodes node)
                | None -> failwith (sprintf "(in update) Node %i not found in %A" outputNodeId noteInstance.nodes)
            time = noteInstance.time + deltaTime
            timeSinceRelease = Option.map ((+) deltaTime) noteInstance.timeSinceRelease }
    
    let updateMany outputNodeId deltaTime noteInstances =
        noteInstances
        |> List.map (fun noteInstance -> update outputNodeId deltaTime noteInstance)
        // cull notes that are completely off (past the release duration)
        |> List.filter (fun noteInstance ->
            match noteInstance.timeSinceRelease with
            // a note is off if it's tSinceRelease value is >= all the longest ADSR envelope release value (or zero, if no ADSRs present)
            | Some(timeSinceRelease) ->
                let longestRelease =
                    noteInstance.nodes
                    |> Map.toList |> List.map (fun (nodeId, oscillatorNode) ->
                        match oscillatorNode with
                        | ADSREnvelopeNode(_, _, _, release, _) -> release
                        | _ -> 0.)
                    |> List.max
                timeSinceRelease < longestRelease
            | None -> true)

/// Unit waveforms
module Waveform =
    let sin x = sin (x * 2. * Math.PI)
    let sawtooth x = (x % 1.) * 2. - 1.
    let square x = float (sign (sawtooth x))
    let triangle x = abs (sawtooth x * 2.) - 1.