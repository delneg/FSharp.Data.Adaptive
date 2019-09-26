namespace FSharp.Data.Adaptive

open FSharp.Data.Traceable

/// An adaptive reader for alist that allows to pull operations and exposes its current state.
type IIndexListReader<'T> = 
    IOpReader<IndexList<'T>, IndexListDelta<'T>>

/// Adaptive list datastructure.
[<Interface>]
type AdaptiveIndexList<'T> =
    /// Is the list constant?
    abstract member IsConstant : bool

    /// The current content of the list as aval.
    abstract member Content : aval<IndexList<'T>>
    
    /// Gets a new reader to the list.
    abstract member GetReader : unit -> IIndexListReader<'T>

/// Adaptive list datastructure.
and alist<'T> = AdaptiveIndexList<'T>

[<AutoOpen>]
module AdaptiveIndexListHelpers = 
    open System
    open System.Collections.Generic

    let inline combineHash (a: int) (b: int) =
        uint32 a ^^^ uint32 b + 0x9e3779b9u + ((uint32 a) <<< 6) + ((uint32 a) >>> 2) |> int

    type IndexMapping<'k when 'k : comparison>() =
        let mutable store = MapExt.empty<'k, Index>

        member x.Invoke(k : 'k) =
            let (left, self, right) = MapExt.neighbours k store
            match self with
                | Some(_, i) -> 
                    i 
                | None ->
                    let result = 
                        match left, right with
                        | None, None                -> Index.after Index.zero
                        | Some(_,l), None           -> Index.after l
                        | None, Some(_,r)           -> Index.before r
                        | Some (_,l), Some(_,r)     -> Index.between l r

                    store <- MapExt.add k result store
                    result

        member x.Revoke(k : 'k) =
            match MapExt.tryRemove k store with
            | Some(i, rest) ->
                store <- rest
                Some i
            | None -> 
                None

        member x.Clear() =
            store <- MapExt.empty

    type IndexCache<'a, 'b>(f : Index -> 'a -> 'b, release : 'b -> unit) =
        let store = Dictionary<Index, 'a * 'b>()

        member x.InvokeAndGetOld(i : Index, a : 'a) =
            match store.TryGetValue(i) with
                | (true, (oa, old)) ->
                    if Unchecked.equals oa a then
                        None, old
                    else
                        let res = f i a
                        store.[i] <- (a, res)
                        Some old, res
                | _ ->
                    let res = f i a
                    store.[i] <- (a, res)
                    None, res       
                                        
        member x.Revoke(i : Index) =
            match store.TryGetValue i with
                | (true, (oa,ob)) -> 
                    store.Remove i |> ignore
                    release ob
                    Some ob
                | _ -> 
                    None 

        member x.Clear() =
            store.Values |> Seq.iter (snd >> release)
            store.Clear()

        new(f : Index -> 'a -> 'b) = IndexCache(f, ignore)

    type Unique<'b when 'b : comparison>(value : 'b) =
        static let mutable currentId = 0
        static let newId() = System.Threading.Interlocked.Increment(&currentId)

        let id = newId()

        member x.Value = value
        member private x.Id = id

        override x.ToString() = value.ToString()

        override x.GetHashCode() = combineHash(Unchecked.hash value) id
        override x.Equals o =
            match o with
                | :? Unique<'b> as o -> Unchecked.equals value o.Value && id = o.Id
                | _ -> false

        interface IComparable with
            member x.CompareTo o =
                match o with
                    | :? Unique<'b> as o ->
                        let c = compare value o.Value
                        if c = 0 then compare id o.Id
                        else c
                    | _ ->
                        failwith "uncomparable"

/// Internal implementations for alist operations.
module AdaptiveIndexListImplementation =

    /// Core implementation for a dependent list.
    type AdaptiveIndexListImpl<'T>(createReader : unit -> IOpReader<IndexListDelta<'T>>) =
        let history = History(createReader, IndexList.trace)

        /// Gets a new reader to the list.
        member x.GetReader() : IIndexListReader<'T> =
            history.NewReader()

        /// Current content of the list as aval.
        member x.Content =
            history :> aval<_>

        interface AdaptiveIndexList<'T> with
            member x.IsConstant = false
            member x.GetReader() = x.GetReader()
            member x.Content = x.Content

    /// Efficient implementation for an empty adaptive list.
    type EmptyList<'T> private() =   
        static let instance = EmptyList<'T>() :> alist<_>
        let content = AVal.constant IndexList.empty
        let reader = new History.Readers.EmptyReader<IndexList<'T>, IndexListDelta<'T>>(IndexList.trace) :> IIndexListReader<'T>
        static member Instance = instance
        
        member x.Content = content
        member x.GetReader() = reader
        
        interface AdaptiveIndexList<'T> with
            member x.IsConstant = true
            member x.GetReader() = x.GetReader()
            member x.Content = x.Content

    /// Efficient implementation for a constant adaptive list.
    type ConstantList<'T>(content : Lazy<IndexList<'T>>) =
        let value = AVal.delay (fun () -> content.Value)

        member x.Content = value

        member x.GetReader() =
            new History.Readers.ConstantReader<_,_>(
                IndexList.trace,
                lazy (IndexList.differentiate IndexList.empty content.Value),
                content
            ) :> IIndexListReader<_>

        interface AdaptiveIndexList<'T> with
            member x.IsConstant = true
            member x.GetReader() = x.GetReader()
            member x.Content = x.Content

    /// Reader for map operations.
    type MapReader<'a, 'b>(input : alist<'a>, mapping : Index -> 'a -> 'b) =
        inherit AbstractReader<IndexListDelta<'b>>(IndexListDelta.monoid)

        let reader = input.GetReader()

        override x.Compute(token) =
            reader.GetChanges token |> IndexListDelta.map (fun i op ->
                match op with
                | Remove -> Remove
                | Set v -> Set (mapping i v)
            )

    /// Reader for choose operations.
    type ChooseReader<'a, 'b>(input : alist<'a>, mapping : Index -> 'a -> option<'b>) =
        inherit AbstractReader<IndexListDelta<'b>>(IndexListDelta.monoid)

        let r = input.GetReader()
        let mapping = IndexCache mapping

        override x.Compute(token) =
            r.GetChanges token |> IndexListDelta.choose (fun i op ->
                match op with
                | Remove -> 
                    match mapping.Revoke(i) with
                    | Some _ -> Some Remove
                    | _ -> None
                | Set v -> 
                    let o, n = mapping.InvokeAndGetOld(i, v)
                    match n with
                    | Some res -> Some (Set res)
                    | None -> 
                        match o with
                        | Some (Some _o) -> Some Remove
                        | _ -> None
        )

    /// Reader for filter operations.
    type FilterReader<'a>(input : alist<'a>, predicate : Index -> 'a -> bool) =
        inherit AbstractReader<IndexListDelta<'a>>(IndexListDelta.monoid)

        let reader = input.GetReader()
        let mapping = IndexCache predicate

        override x.Compute(token) =
            reader.GetChanges token |> IndexListDelta.choose (fun i op ->
                match op with
                | Remove -> 
                    match mapping.Revoke(i) with
                    | Some true -> Some Remove
                    | _ -> None
                | Set v -> 
                    let o, n = mapping.InvokeAndGetOld(i, v)
                    match n with
                    | true -> 
                        Some (Set v)
                    | false -> 
                        match o with
                            | Some true -> Some Remove
                            | _ -> None
            )

    /// Ulitity used by CollectReader.
    type MultiReader<'a>(mapping : IndexMapping<Index * Index>, list : alist<'a>, release : alist<'a> -> unit) =
        inherit AbstractReader<IndexListDelta<'a>>(IndexListDelta.monoid)
            
        let targets = System.Collections.Generic.HashSet<Index>()

        let mutable reader = None

        let getReader() =
            match reader with
            | Some r -> r
            | None ->
                let r = list.GetReader()
                reader <- Some r
                r

        member x.AddTarget(oi : Index) =
            if targets.Add oi then
                getReader().State.Content
                |> MapExt.mapMonotonic (fun ii v -> mapping.Invoke(oi, ii), Set v)
                |> IndexListDelta.ofMap
            else
                IndexListDelta.empty

        member x.RemoveTarget(dirty : System.Collections.Generic.HashSet<MultiReader<'a>>, oi : Index) =
            if targets.Remove oi then
                match reader with
                | Some r ->
                    let result = 
                        r.State.Content 
                        |> MapExt.toSeq
                        |> Seq.choose (fun (ii, v) -> 
                            match mapping.Revoke(oi,ii) with
                            | Some v -> Some (v, Remove)
                            | None -> None
                        )
                        |> IndexListDelta.ofSeq

                    if targets.Count = 0 then 
                        dirty.Remove x |> ignore
                        x.Release()

                    result

                | None ->
                    IndexListDelta.empty
            else
                IndexListDelta.empty

        member x.Release() =
            match reader with
            | Some r ->
                release(list)
                r.Outputs.Remove x |> ignore
                x.Outputs.Consume() |> ignore
                reader <- None
            | None ->   
                ()

        override x.Compute(token) =
            match reader with
            | Some r -> 
                let ops = r.GetChanges token

                ops |> IndexListDelta.collect (fun ii op ->
                    match op with
                    | Remove -> 
                        targets
                        |> Seq.choose (fun oi -> 
                            match mapping.Revoke(oi, ii) with   
                            | Some i -> Some(i, Remove)
                            | None -> None
                        )
                        |> IndexListDelta.ofSeq

                    | Set v ->
                        targets
                        |> Seq.map (fun oi -> mapping.Invoke(oi, ii), Set v)
                        |> IndexListDelta.ofSeq

                )

            | None ->
                IndexListDelta.empty

    /// Reader for collect operations.
    type CollectReader<'a, 'b>(input : alist<'a>, f : Index -> 'a -> alist<'b>) =
        inherit AbstractDirtyReader<MultiReader<'b>, IndexListDelta<'b>>(IndexListDelta.monoid)
            
        let mapping = IndexMapping<Index * Index>()
        let cache = System.Collections.Generic.Dictionary<Index, 'a * alist<'b>>()
        let readers = System.Collections.Generic.Dictionary<alist<'b>, MultiReader<'b>>()
        let input = input.GetReader()

        let removeReader (l : alist<'b>) =
            readers.Remove l |> ignore

        let getReader (l : alist<'b>) =
            match readers.TryGetValue l with
            | (true, r) -> r
            | _ ->
                let r = new MultiReader<'b>(mapping, l, removeReader)
                readers.Add(l, r) 
                r

        member x.Invoke (dirty : System.Collections.Generic.HashSet<MultiReader<'b>>, i : Index, v : 'a) =
            match cache.TryGetValue(i) with
            | (true, (oldValue, oldList)) ->
                if Unchecked.equals oldValue v then
                    dirty.Add (getReader(oldList)) |> ignore
                    IndexListDelta.empty
                else
                    let newList = f i v
                    cache.[i] <- (v, newList)
                    let newReader = getReader(newList)

                    let rem = getReader(oldList).RemoveTarget(dirty, i)
                    let add = newReader.AddTarget i
                    dirty.Add newReader |> ignore
                    IndexListDelta.combine rem add 

            | _ ->
                let newList = f i v
                cache.[i] <- (v, newList)
                let newReader = getReader(newList)
                let add = newReader.AddTarget i
                dirty.Add newReader |> ignore
                add

        member x.Revoke (dirty : System.Collections.Generic.HashSet<MultiReader<'b>>, i : Index) =
            match cache.TryGetValue i with
            | (true, (v,l)) ->
                let r = getReader l
                cache.Remove i |> ignore
                r.RemoveTarget(dirty, i)
            | _ ->
                IndexListDelta.empty

        override x.Compute(token, dirty) =
            let mutable result = 
                input.GetChanges token |> IndexListDelta.collect (fun i op ->
                    match op with
                    | Remove -> x.Revoke(dirty, i)
                    | Set v -> x.Invoke(dirty, i, v)
                )

            for d in dirty do
                result <- IndexListDelta.combine result (d.GetChanges token)

            result



    /// Gets the current content of the alist as IndexList.
    let inline force (list : alist<'T>) = 
        AVal.force list.Content

    /// Creates a constant list using the creation function.
    let inline constant (content : unit -> IndexList<'T>) = 
        ConstantList(lazy(content())) :> alist<_> 

    /// Creates an adaptive list using the reader.
    let inline create (reader : unit -> #IOpReader<IndexListDelta<'T>>) =
        AdaptiveIndexListImpl(fun () -> reader() :> IOpReader<_>) :> alist<_>


/// Functional operators for the alist<_> type.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module AList =
    open AdaptiveIndexListImplementation

    /// The empty alist.
    [<GeneralizableValue>]
    let empty<'T> : alist<'T> = 
        EmptyList<'T>.Instance

    /// A constant alist holding a single value.
    let single (value : 'T) =
        lazy (IndexList.single value) |> ConstantList :> alist<_>
        
    /// Creates an alist holding the given values.
    let ofSeq (s : seq<'T>) =
        lazy (IndexList.ofSeq s) |> ConstantList :> alist<_>
        
    /// Creates an alist holding the given values.
    let ofList (s : list<'T>) =
        lazy (IndexList.ofList s) |> ConstantList :> alist<_>
        
    /// Creates an alist holding the given values.
    let ofArray (s : 'T[]) =
        lazy (IndexList.ofArray s) |> ConstantList :> alist<_>
        
    /// Creates an alist holding the given values. `O(1)`
    let ofIndexList (elements : IndexList<'T>) =
        ConstantList(lazy elements) :> alist<_>

    let mapi (mapping: Index -> 'T1 -> 'T2) (list : alist<'T1>) =
        if list.IsConstant then
            constant (fun () -> list |> force |> IndexList.mapi mapping)
        else
            create (fun () -> MapReader(list, mapping))

    let map (mapping: 'T1 -> 'T2) (list : alist<'T1>) =
        if list.IsConstant then
            constant (fun () -> list |> force |> IndexList.map mapping)
        else
            // TODO: better implementation (caching possible since no Index needed)
            create (fun () -> MapReader(list, fun _ -> mapping))
  
    let collecti (mapping: Index -> 'T1 -> alist<'T2>) (list : alist<'T1>) =
        create (fun () -> CollectReader(list, mapping))
                     

    /// Creates an aval providing access to the current content of the list.
    let toAVal (list : alist<'T>) =
        list.Content
