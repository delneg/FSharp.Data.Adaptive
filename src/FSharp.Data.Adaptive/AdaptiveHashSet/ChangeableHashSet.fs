﻿namespace FSharp.Data.Adaptive

open FSharp.Data.Traceable
open System.Threading

/// Changeable adaptive set that allows mutation by user-code and implements aset.
[<Sealed>]
type ChangeableHashSet<'T>(initial : HashSet<'T>) =
    let history = 
        let h = History(CountingHashSet.traceNoRefCountTup)
        if not initial.IsEmpty then 
            let delta = HashSet.computeDelta HashSet.empty initial
            h.Perform delta |> ignore
        h

    //let mutable content = initial
    let mutable adaptiveContentCache = Unchecked.defaultof<aval<HashSet<'T>>> 

    let getAdaptiveContent() =
        let c = adaptiveContentCache
        if Unchecked.isNull c then
            // TODO
            let v = history |> AVal.map snd
            let o = Interlocked.CompareExchange(&adaptiveContentCache, v, Unchecked.defaultof<_>)
            if Unchecked.isNull o then v
            else o
        else
            c

    /// The number of entries currently in the set.
    member x.Count =
        let (_, state) = history.State
        state.Count

    /// Is the set currently empty?
    member x.IsEmpty =
        let (_, state) = history.State
        state.IsEmpty

    /// Checks whether the given value is contained.
    member x.Contains (value : 'T) =
        let (_, state) = history.State
        HashSet.contains value state

    /// Gets or sets the current state as HashSet.
    member x.Value
        with get() = 
            let (_, state) = history.State
            state
        and set newSet = 
            x.UpdateTo newSet |> ignore
                
    /// Sets the current state as HashSet.
    member x.UpdateTo(newSet : HashSet<'T>) =
        let (_, nonCountingSet) = history.State
        if not (cheapEqual nonCountingSet newSet) then
            let delta = HashSet.computeDelta nonCountingSet newSet
            history.Perform(delta)
        else
            false
    /// Performs the given Operations on the Set.
    member x.Perform(operations : HashSetDelta<'T>) =   
        if not (HashSetDelta.isEmpty operations) then
            history.Perform operations |> ignore

    /// Adds a value and returns whether the element was new.
    member x.Add(value : 'T) =
        history.Perform (HashSetDelta.single (Add value))

    /// Removes a value and returns whether the element was deleted.
    member x.Remove(value : 'T) =
        history.Perform (HashSetDelta.single (Rem value))

    /// Clears the set.
    member x.Clear() =
        let (_, nonCountingSet) = history.State
        if not nonCountingSet.IsEmpty then
            let ops = HashSet.computeDelta nonCountingSet HashSet.empty
            history.Perform ops |> ignore

    /// Adds all the given values to the set.
    member x.UnionWith (other : seq<'T>) =
        let ops = other |> Seq.map (fun v -> v, 1) |> HashMap.ofSeq |> HashSetDelta
        x.Perform ops
        
    /// Removes all the given elements from the set.
    member x.ExceptWith (other : seq<'T>) =
        let ops = other |> Seq.map (fun v -> v, -1) |> HashMap.ofSeq |> HashSetDelta
        x.Perform ops
        
    member x.SymmetricExceptWith (other : seq<'T>) : unit =
        let other = CountingHashSet.ofSeq other
        let delta =
            let (state,_) = history.State
            (state.ToHashMap(), other.ToHashMap()) ||> HashMap.choose2V (fun _k l r ->
                match l with
                | ValueSome l ->
                    match r with
                    | ValueSome _ -> ValueSome -l
                    | ValueNone -> ValueNone
                | ValueNone ->
                    match r with
                    | ValueSome _ -> ValueSome 1
                    | ValueNone -> ValueNone
            )
            |> HashSetDelta
        x.Perform delta

    /// Removes all elements from the set that are not also contained in other.
    member x.IntersectWith (other : seq<'T>) =
        let otherSet = HashSet.ofSeq other

        let apply (_value : 'T) (inOther : bool) (myRefCnt : int) =
            if not inOther && myRefCnt > 0 then
                struct(false, ValueSome -myRefCnt)
            else
                struct(false, ValueNone)

        let _, opsMap = 
            let (state,_) = history.State
            HashSet.ApplyDelta(otherSet, state.ToHashMap(), apply)
        let ops = HashSetDelta.ofHashMap opsMap
        x.Perform ops

    /// Creates an adaptive reader for the set.
    member x.GetReader() : IHashSetReader<'T> =
        history.NewReader()

    member x.GetEnumerator() = x.Value.GetEnumerator()

    interface IAdaptiveHashSet<'T> with
        member x.IsConstant = false
        member x.GetReader() = x.GetReader()
        member x.Content = getAdaptiveContent()
        member x.History = Some history
        
    interface System.Collections.IEnumerable with
        member x.GetEnumerator() = (x.Value :> System.Collections.IEnumerable).GetEnumerator()
        
    interface System.Collections.Generic.IEnumerable<'T> with
        member x.GetEnumerator() = (x.Value :> seq<_>).GetEnumerator()
        
    interface System.Collections.Generic.ICollection<'T> with
        member x.IsReadOnly = false
        member x.Count = x.Count
        member x.Contains value = x.Contains value
        member x.Clear() = transactIfNecessary (fun () -> x.Clear())
        member x.Add value = transactIfNecessary (fun () -> x.Add value |> ignore)
        member x.Remove value = transactIfNecessary (fun () -> x.Remove value)
        member x.CopyTo(array: 'T[], idx : int) = 
            let (_, content) = history.State
            content.CopyTo(array, idx)

    #if !FABLE_COMPILER
    interface System.Collections.Generic.ISet<'T> with
        member x.Add value = transactIfNecessary (fun () -> x.Add value)
        member x.ExceptWith other = transactIfNecessary (fun () -> x.ExceptWith other)
        member x.SymmetricExceptWith other = transactIfNecessary (fun () -> x.SymmetricExceptWith other)
        member x.UnionWith other = transactIfNecessary (fun () -> x.UnionWith other)
        member x.IntersectWith other = transactIfNecessary (fun () -> x.IntersectWith other)
        member x.IsProperSubsetOf other = x.Value.IsProperSubsetOf other
        member x.IsProperSupersetOf other = x.Value.IsProperSupersetOf other
        member x.IsSubsetOf other = x.Value.IsSubsetOf other
        member x.IsSupersetOf other = x.Value.IsSupersetOf other
        member x.Overlaps other = x.Value.Overlaps other
        member x.SetEquals other = x.Value.SetEquals other
    #endif

    override x.ToString() =
        let suffix =
            if x.Count > 5 then "; ..."
            else ""

        let content =
            history.State |> snd |> Seq.truncate 5 |> Seq.map (sprintf "%A") |> String.concat "; "

        "cset [" + content + suffix + "]"

    member private x.AsString = x.ToString()

    /// Creates a new empty cset.
    new() = cset<'T>(HashSet.empty)

    /// Creates a new cset containing all the given elements.
    new(elements : seq<'T>) = cset<'T>(HashSet.ofSeq elements)

and cset<'T> = ChangeableHashSet<'T>
