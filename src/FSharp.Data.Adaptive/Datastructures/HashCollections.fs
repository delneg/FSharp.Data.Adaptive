﻿namespace rec FSharp.Data.Adaptive

open System.Collections
open System.Collections.Generic
open System.Runtime.CompilerServices
#if NETCOREAPP3_0 && USE_INTRINSICS
open System.Runtime.Intrinsics.X86
#endif

[<AutoOpen>]
module internal HashMapUtilities =

    let inline nextPowerOfTwo (v : int) =
        let mutable v = v - 1
        v <- v ||| (v >>> 1)
        v <- v ||| (v >>> 2)
        v <- v ||| (v >>> 4)
        v <- v ||| (v >>> 8)
        v <- v ||| (v >>> 16)
        v + 1

    let resizeArray (r : ref<'a[]>) (l : int) = 
        let len = r.Value.Length
        if l < len then 
            r := Array.take l r.Value
        elif l > len then 
            let res = Array.zeroCreate l
            res.[0..len-1] <- r.Value
            r := res
        
    let inline ensureLength (r : ref<'a[]>) (minLength : int) =
        let o = r.Value.Length
        if minLength > o then
            let n = nextPowerOfTwo minLength
            let res = Array.zeroCreate n
            res.[0..o-1] <- r.Value
            r := res
        //elif n < o then
        //    r := Array.take n r.Value
            


    type private EnumeratorSeq<'T>(create : unit -> System.Collections.Generic.IEnumerator<'T>) =
        interface System.Collections.IEnumerable with
            member x.GetEnumerator() = create() :> _

        interface System.Collections.Generic.IEnumerable<'T> with
            member x.GetEnumerator() = create()

    module Seq =
        let ofEnumerator (create : unit -> #System.Collections.Generic.IEnumerator<'T>) =
            EnumeratorSeq(fun () -> create() :> _) :> seq<_>


    type Mask = uint32

    let inline combineHash (a: int) (b: int) =
        uint32 a ^^^ uint32 b + 0x9e3779b9u + ((uint32 a) <<< 6) + ((uint32 a) >>> 2) |> int

    let inline private highestBitMask x =
        let mutable x = x
        x <- x ||| (x >>> 1)
        x <- x ||| (x >>> 2)
        x <- x ||| (x >>> 4)
        x <- x ||| (x >>> 8)
        x <- x ||| (x >>> 16)
        x ^^^ (x >>> 1)

    let inline getPrefix (k: uint32) (m: Mask) = 
        #if NETCOREAPP3_0 && USE_INTRINSICS
        if Bmi1.IsSupported then
            k
        else
            k &&& ~~~((m <<< 1) - 1u)
        #else
        k &&& ~~~((m <<< 1) - 1u)
        #endif

    #if NETCOREAPP3_0 && USE_INTRINSICS
    let inline zeroBit (k: uint32) (m: Mask) =
        if Bmi1.IsSupported then
            Bmi1.BitFieldExtract(k, uint16 m)
        else
            if (k &&& m) <> 0u then 1u else 0u
    #else
    let inline zeroBit (k: uint32) (m: uint32) =
        if (k &&& m) <> 0u then 1u else 0u
    #endif
        
    #if NETCOREAPP3_0 && USE_INTRINSICS 
    let inline matchPrefixAndGetBit (hash: uint32) (prefix: uint32) (m: Mask) =
        if Bmi1.IsSupported then
            let lz = Lzcnt.LeadingZeroCount (hash ^^^ prefix)
            let b = Bmi1.BitFieldExtract(hash, uint16 m)
            if lz >= (m >>> 16) then b
            else 2u
        else
            if getPrefix hash m = prefix then zeroBit hash m
            else 2u
    #else
    let inline matchPrefixAndGetBit (hash: uint32) (prefix: uint32) (m: uint32) =
        if getPrefix hash m = prefix then zeroBit hash m
        else 2u
    #endif

    let inline compareMasks (l : Mask) (r : Mask) =
        #if NETCOREAPP3_0 && USE_INTRINSICS 
        if Bmi1.IsSupported then
            int (r &&& 0xFFu) - int (l &&& 0xFFu)
        else
            compare r l
        #else
        compare r l
        #endif


    let inline getMask (p0 : uint32) (p1 : uint32) =
        #if NETCOREAPP3_0 && USE_INTRINSICS 
        if Bmi1.IsSupported then
            let lz = Lzcnt.LeadingZeroCount(p0 ^^^ p1)
            (lz <<< 16) ||| 0x0100u ||| (31u - lz)
        else
            //lowestBitMask (p0 ^^^ p1) // little endian
            highestBitMask (p0 ^^^ p1) // big endian
        #else
        //lowestBitMask (p0 ^^^ p1) // little endian
        highestBitMask (p0 ^^^ p1) // big endian
        #endif

    let inline (==) (a: ^a) (b: ^a) =
        System.Object.ReferenceEquals(a, b)

[<AutoOpen>]
module internal HashMapImplementation = 
    // ========================================================================================================================
    // HashSetNode implementation
    // ========================================================================================================================

    [<AllowNullLiteral>]
    type HashSetLinked<'T> =
        val mutable public Next: HashSetLinked<'T>
        val mutable public Value: 'T

        new(v) = { Value = v; Next = null }
        new(v, n) = { Value = v; Next = n }

    module HashSetLinked =
    
        let rec addInPlaceUnsafe (cmp: IEqualityComparer<'T>) (value : 'T) (n: HashSetLinked<'T>) =
            if isNull n then
                HashSetLinked(value)
            elif cmp.Equals(n.Value, value) then
                n.Value <- value
                n
            else
                n.Next <- addInPlaceUnsafe cmp value n.Next
                n

        let rec add (cmp: IEqualityComparer<'T>) (value: 'T) (n: HashSetLinked<'T>) =
            if isNull n then
                HashSetLinked(value)
            elif cmp.Equals(n.Value, value) then
                n
            else
                let next = add cmp value n.Next
                if next == n.Next then n
                else HashSetLinked(n.Value, add cmp value n.Next)
               
        let rec alter (cmp: IEqualityComparer<'T>) (value: 'T) (update: bool -> bool) (n: HashSetLinked<'T>) =
            if isNull n then
                if update false then HashSetLinked(value)
                else null
            elif cmp.Equals(n.Value, value) then
                if update true then n
                else n.Next
            else
                let next = alter cmp value update n.Next
                if next == n.Next then n
                else HashSetLinked(n.Value, next)
               
        let rec contains (cmp: IEqualityComparer<'T>) (value: 'T) (n: HashSetLinked<'T>) =
            if isNull n then false
            elif cmp.Equals(n.Value, value) then true
            else contains cmp value n.Next

        let destruct (n: HashSetLinked<'T>) =
            if isNull n then ValueNone
            else ValueSome(struct (n.Value, n.Next))
            
        let rec remove (cmp: IEqualityComparer<'T>) (value: 'T) (n: HashSetLinked<'T>) =
            if isNull n then
                null
            elif cmp.Equals(n.Value, value) then 
                n.Next
            else
                let rest = remove cmp value n.Next
                if rest == n.Next then n
                else HashSetLinked(n.Value, rest)

        let rec tryRemove (cmp: IEqualityComparer<'T>) (value: 'T) (n: HashSetLinked<'T>) =
            if isNull n then
                ValueNone
            elif cmp.Equals(n.Value, value) then 
                ValueSome n.Next
            else
                match tryRemove cmp value n.Next with
                | ValueSome rest ->
                    ValueSome (HashSetLinked(n.Value, rest))
                | ValueNone ->
                    ValueNone

        let rec filter (predicate: 'T -> bool) (n: HashSetLinked<'T>) =
            if isNull n then
                null
            elif predicate n.Value then
                let next = filter predicate n.Next
                if n.Next == next then n
                else HashSetLinked(n.Value, filter predicate n.Next)
            else
                filter predicate n.Next
    
        let rec mapToMap (mapping : 'T -> 'R) (n: HashSetLinked<'T>) =
            if isNull n then
                null
            else
                let v = mapping n.Value
                HashMapLinked(n.Value, v, mapToMap mapping n.Next)
                
        let rec chooseToMap (mapping : 'T -> option<'R>) (n: HashSetLinked<'T>) =
            if isNull n then
                null
            else
                match mapping n.Value with
                | Some v -> HashMapLinked(n.Value, v, chooseToMap mapping n.Next)
                | None -> chooseToMap mapping n.Next
                
        let rec chooseToMapV (mapping : 'T -> voption<'R>) (n: HashSetLinked<'T>) =
            if isNull n then
                null
            else
                match mapping n.Value with
                | ValueSome v -> HashMapLinked(n.Value, v, chooseToMapV mapping n.Next)
                | ValueNone -> chooseToMapV mapping n.Next
                
        let rec chooseToMapV2 (mapping : 'T -> struct(voption<'T1> * voption<'T2>)) (n: HashSetLinked<'T>) =
            if isNull n then
                struct(null, null)
            else
                let struct (l, r) = mapping n.Value
                let struct (ln, rn) = chooseToMapV2 mapping n.Next
                
                let l = match l with | ValueSome l -> HashMapLinked(n.Value, l, ln) | ValueNone -> ln
                let r = match r with | ValueSome r -> HashMapLinked(n.Value, r, rn) | ValueNone -> rn
                struct (l, r)

        let rec exists (predicate: 'T -> bool) (n: HashSetLinked<'T>) =
            if isNull n then 
                false
            elif predicate n.Value then
                true
            else
                exists predicate n.Next
                
        let rec forall (predicate: 'T -> bool) (n: HashSetLinked<'T>) =
            if isNull n then 
                true
            elif not (predicate n.Value) then
                false
            else
                forall predicate n.Next

        let rec copyTo (index: int) (dst : 'T array) (n: HashSetLinked<'T>) =
            if not (isNull n) then
                dst.[index] <- n.Value
                copyTo (index + 1) dst n.Next
            else
                index
    
    [<AbstractClass>]
    type HashSetNode<'T>() =
        abstract member ComputeHash : unit -> int
        abstract member Remove: IEqualityComparer<'T> * uint32 * 'T -> HashSetNode<'T>
        abstract member TryRemove: IEqualityComparer<'T> * uint32 * 'T -> ValueOption<HashSetNode<'T>>

        abstract member Count : int
        abstract member IsEmpty: bool

        abstract member AddInPlaceUnsafe: IEqualityComparer<'T> * uint32 * 'T -> HashSetNode<'T>
        abstract member Add: IEqualityComparer<'T> * uint32 * 'T -> HashSetNode<'T>
        abstract member Alter: IEqualityComparer<'T> * uint32 * 'T * (bool -> bool) -> HashSetNode<'T>
        abstract member Contains: IEqualityComparer<'T> * uint32 * 'T -> bool

        abstract member MapToMap: mapping: ('T -> 'R) -> HashMapNode<'T, 'R>
        abstract member ChooseToMap: mapping: ('T -> option<'R>) -> HashMapNode<'T, 'R>
        abstract member ChooseToMapV: mapping: ('T -> voption<'R>) -> HashMapNode<'T, 'R>
        abstract member ChooseToMapV2: mapping: ('T -> struct(ValueOption<'T1> * ValueOption<'T2>)) -> struct (HashMapNode<'T, 'T1> * HashMapNode<'T, 'T2>)
        abstract member Filter: predicate: ('T -> bool) -> HashSetNode<'T>
        abstract member Iter: action: ('T -> unit) -> unit
        abstract member Fold: acc: OptimizedClosures.FSharpFunc<'S, 'T, 'S> * seed : 'S -> 'S
        abstract member Exists: predicate: ('T -> bool) -> bool
        abstract member Forall: predicate: ('T -> bool) -> bool

        abstract member Accept: HashSetVisitor<'T, 'R> -> 'R

        abstract member CopyTo: dst: 'T array * index : int -> int
        abstract member ToList : list<'T> -> list<'T>

    [<AbstractClass>]
    type HashSetLeaf<'T>() =
        inherit HashSetNode<'T>()
        abstract member LHash : uint32
        abstract member LValue : 'T
        abstract member LNext : HashSetLinked<'T>
        
        static member inline New(h: uint32, v: 'T, n: HashSetLinked<'T>) : HashSetNode<'T> = 
            if isNull n then new HashSetNoCollisionLeaf<_>(Hash = h, Value = v) :> HashSetNode<'T>
            else new HashSetCollisionLeaf<_>(Hash = h, Value = v, Next = n) :> HashSetNode<'T>
  
    [<Sealed>]
    type HashSetEmpty<'T> private() =
        inherit HashSetNode<'T>()
        static let instance = HashSetEmpty<'T>() :> HashSetNode<_>
        static member Instance = instance

        override x.ComputeHash() =
            0

        override x.Count = 0

        override x.Accept(v: HashSetVisitor<_,_>) =
            v.VisitEmpty x

        override x.IsEmpty = true

        override x.Contains(_cmp: IEqualityComparer<'T>, _hash: uint32, _value: 'T) =
            false

        override x.Remove(_cmp: IEqualityComparer<'T>, _hash: uint32, _value: 'T) =
            x:> _
            
        override x.TryRemove(_cmp: IEqualityComparer<'T>, _hash: uint32, _value: 'T) =
            ValueNone

        override x.AddInPlaceUnsafe(_cmp: IEqualityComparer<'T>, hash: uint32, value: 'T) =
            HashSetNoCollisionLeaf.New(hash, value)

        override x.Add(_cmp: IEqualityComparer<'T>, hash: uint32, value: 'T) =
            HashSetNoCollisionLeaf.New(hash, value)

        override x.Alter(cmp: IEqualityComparer<'T>, hash: uint32, value: 'T, update: bool -> bool) =
            if update false then
                HashSetNoCollisionLeaf.New(hash, value)
            else
                x :> _

        override x.MapToMap(_mapping: 'T -> 'R) =
            HashMapEmpty.Instance
            
        override x.ChooseToMap(_mapping: 'T -> option<'R>) =
            HashMapEmpty.Instance
            
        override x.ChooseToMapV(_mapping: 'T -> ValueOption<'R>) =
            HashMapEmpty.Instance
                 
        override x.ChooseToMapV2(_mapping : 'T -> struct (voption<'T1> * voption<'T2>)) =
            struct(HashMapEmpty.Instance, HashMapEmpty.Instance)
                          
        override x.Filter(_predicate: 'T -> bool) =
            HashSetEmpty.Instance

        override x.Iter(_action: 'T -> unit) =
            ()
            
        override x.Fold(_acc: OptimizedClosures.FSharpFunc<'S, 'T, 'S>, seed : 'S) =
            seed

        override x.Exists(_predicate: 'T -> bool) =
            false

        override x.Forall(_predicate: 'T -> bool) =
            true

        override x.CopyTo(_dst : 'T array, index : int) =
            index

        override x.ToList acc =
            acc
     
    type HashSetNoCollisionLeaf<'T>() =
        inherit HashSetLeaf<'T>()
        [<DefaultValue>]
        val mutable public Value: 'T
        [<DefaultValue>]
        val mutable public Hash: uint32

        override x.Count = 1
        override x.LHash = x.Hash
        override x.LValue = x.Value
        override x.LNext = null
        
        override x.ComputeHash() =
            int x.Hash

        override x.IsEmpty = false
        
        override x.Accept(v: HashSetVisitor<_,_>) =
            v.VisitNoCollision x

        override x.Contains(cmp: IEqualityComparer<'T>, hash: uint32, value: 'T) =   
            if hash = x.Hash && cmp.Equals(value, x.Value) then 
                true
            else
                false

        override x.Remove(cmp: IEqualityComparer<'T>, hash: uint32, value: 'T) =
            if hash = x.Hash && cmp.Equals(value, x.Value) then
                HashSetEmpty.Instance
            else
                x:> _

        override x.TryRemove(cmp: IEqualityComparer<'T>, hash: uint32, value: 'T) =
            if hash = x.Hash && cmp.Equals(value, x.Value) then
                ValueSome (HashSetEmpty.Instance)
            else
                ValueNone

        override x.AddInPlaceUnsafe(cmp: IEqualityComparer<'T>, hash: uint32, value: 'T) =
            if x.Hash = hash then
                if cmp.Equals(value, x.Value) then
                    x.Value <- value
                    x:> _
                else
                    HashSetCollisionLeaf.New(x.Hash, x.Value, HashSetLinked(value, null))
            else
                let n = HashSetNoCollisionLeaf.New(hash, value)
                HashSetInner.Join(hash, n, x.Hash, x)

        override x.Add(cmp: IEqualityComparer<'T>, hash: uint32,value: 'T) =
            if x.Hash = hash then
                if cmp.Equals(value, x.Value) then
                    x :> _
                else
                    HashSetCollisionLeaf.New(x.Hash, x.Value, HashSetLinked.add cmp value null)
            else
                let n = HashSetNoCollisionLeaf.New(hash, value)
                HashSetInner.Join(hash, n, x.Hash, x)
        
        override x.Alter(cmp: IEqualityComparer<'T>, hash: uint32, value: 'T, update: bool -> bool) =
            if x.Hash = hash then
                if cmp.Equals(value, x.Value) then
                    if update true then
                        x :> _
                    else
                        HashSetEmpty.Instance
                else
                    if update false then
                        HashSetCollisionLeaf.New(x.Hash, x.Value, HashSetLinked(value, null))
                    else
                        x :> _
            else
                if update false then
                    let n = HashSetNoCollisionLeaf.New(hash, value)
                    HashSetInner.Join(hash, n, x.Hash, x)
                else
                    x:> _
           
        override x.MapToMap(mapping: 'T -> 'R) =
            let t = mapping x.Value
            HashMapNoCollisionLeaf.New(x.Hash, x.Value, t)
               
        override x.ChooseToMap(mapping: 'T -> option<'R>) =
            match mapping x.Value with
            | Some v ->
                HashMapNoCollisionLeaf<'T, 'R>.New(x.Hash, x.Value, v)
            | None ->
                HashMapEmpty<'T, 'R>.Instance
                
        override x.ChooseToMapV(mapping: 'T -> voption<'R>) =
            match mapping x.Value with
            | ValueSome v ->
                HashMapNoCollisionLeaf.New(x.Hash, x.Value, v)
            | ValueNone ->
                HashMapEmpty.Instance
 
        override x.ChooseToMapV2(mapping : 'T -> struct (ValueOption<'T1> * ValueOption<'T2>)) =
            let struct (l,r) = mapping x.Value 
            let l = match l with | ValueSome v -> HashMapNoCollisionLeaf.New(x.Hash, x.Value, v) :> HashMapNode<_,_> | _ -> HashMapEmpty.Instance
            let r = match r with | ValueSome v -> HashMapNoCollisionLeaf.New(x.Hash, x.Value, v) :> HashMapNode<_,_> | _ -> HashMapEmpty.Instance
            struct (l, r)

        override x.Filter(predicate: 'T -> bool) =
            if predicate x.Value then x :> _
            else HashSetEmpty.Instance
 
        override x.Iter(action: 'T -> unit) =
            action x.Value
            
        override x.Fold(acc: OptimizedClosures.FSharpFunc<'S, 'T, 'S>, seed : 'S) =
            acc.Invoke(seed, x.Value)

        override x.Exists(predicate: 'T -> bool) =
            predicate x.Value
                
        override x.Forall(predicate: 'T -> bool) =
            predicate x.Value

        override x.CopyTo(dst : 'T array, index : int) =
            dst.[index] <- x.Value
            index + 1
            
        override x.ToList acc =
           x.Value :: acc

        static member New(h : uint32, v : 'T) : HashSetNode<'T> =
            new HashSetNoCollisionLeaf<_>(Hash = h, Value = v) :> HashSetNode<'T>

    type HashSetCollisionLeaf<'T>() =
        inherit HashSetLeaf<'T>()

        [<DefaultValue>]
        val mutable public Next: HashSetLinked<'T>
        [<DefaultValue>]
        val mutable public Value: 'T
        [<DefaultValue>]
        val mutable public Hash: uint32
  
        override x.Count =
            let mutable cnt = 1
            let mutable c = x.Next
            while not (isNull c) do
                c <- c.Next
                cnt <- cnt + 1
            cnt

        override x.LHash = x.Hash
        override x.LValue = x.Value
        override x.LNext = x.Next
        
        override x.ComputeHash() =
            let mutable cnt = 1
            let mutable c = x.Next
            while not (isNull c) do
                c <- c.Next
                cnt <- cnt + 1
            combineHash cnt (int x.Hash)

        override x.Accept(v: HashSetVisitor<_,_>) =
            v.VisitLeaf x

        override x.IsEmpty = false
        
        override x.Contains(cmp: IEqualityComparer<'T>, hash: uint32, value: 'T) =   
            if hash = x.Hash then
                if cmp.Equals(value, x.Value) then 
                    true
                else
                    HashSetLinked.contains cmp value x.Next
            else
                false

        override x.Remove(cmp: IEqualityComparer<'T>, hash: uint32, value: 'T) =
            if hash = x.Hash then
                if cmp.Equals(value, x.Value) then
                    match HashSetLinked.destruct x.Next with
                    | ValueSome (struct (v, rest)) ->
                        HashSetLeaf.New(hash, v, rest)
                    | ValueNone ->
                        HashSetEmpty.Instance
                else
                    let next = HashSetLinked.remove cmp value x.Next
                    if next == x.Next then x :> _
                    else HashSetLeaf.New(x.Hash, x.Value, next)
            else
                x:> _

        override x.TryRemove(cmp: IEqualityComparer<'T>, hash: uint32, value: 'T) =
            if hash = x.Hash then
                if cmp.Equals(value, x.Value) then
                    match HashSetLinked.destruct x.Next with
                    | ValueSome (struct (v, rest)) ->
                        ValueSome (HashSetLeaf.New(hash, v, rest))
                    | ValueNone ->
                        ValueSome  HashSetEmpty.Instance
                else
                    match HashSetLinked.tryRemove cmp value x.Next with
                    | ValueSome rest ->
                        ValueSome(HashSetLeaf.New(x.Hash, x.Value, rest))
                    | ValueNone ->
                        ValueNone
            else
                ValueNone

        override x.AddInPlaceUnsafe(cmp: IEqualityComparer<'T>, hash: uint32, value: 'T) =
            if x.Hash = hash then
                if cmp.Equals(value, x.Value) then
                    x.Value <- value
                    x:> _
                else
                    x.Next <- HashSetLinked.addInPlaceUnsafe cmp value x.Next
                    x:> _
            else
                let n = HashSetNoCollisionLeaf.New(hash, value)
                HashSetInner.Join(hash, n, x.Hash, x)
                
        override x.Add(cmp: IEqualityComparer<'T>, hash: uint32, value: 'T) =
            if x.Hash = hash then
                if cmp.Equals(value, x.Value) then
                    x :> _
                else
                    HashSetCollisionLeaf.New(x.Hash, x.Value, HashSetLinked.add cmp value x.Next)
            else
                let n = HashSetNoCollisionLeaf.New(hash, value)
                HashSetInner.Join(hash, n, x.Hash, x)

        override x.Alter(cmp: IEqualityComparer<'T>, hash: uint32, value: 'T, update: bool -> bool) =
            if x.Hash = hash then
                if cmp.Equals(value, x.Value) then
                    if update true then
                        // update
                        x :> _
                    else
                        // remove
                        match HashSetLinked.destruct x.Next with
                        | ValueSome (struct (v, rest)) ->
                            HashSetLeaf.New(x.Hash, v, rest)
                        | ValueNone ->
                            HashSetEmpty.Instance
                else
                    // in linked?
                    let n = HashSetLinked.alter cmp value update x.Next
                    if n == x.Next then x:> _
                    else HashSetLeaf.New(x.Hash, x.Value, n)
            else
                // other hash => not contained
                if update false then
                    // add
                    let n = HashSetNoCollisionLeaf.New(hash, value)
                    HashSetInner.Join(hash, n, x.Hash, x)
                else 
                    x:> _

        override x.MapToMap(mapping: 'T -> 'R) =
            let t = mapping x.Value
            HashMapCollisionLeaf.New(x.Hash, x.Value, t, HashSetLinked.mapToMap mapping x.Next)
            
        override x.ChooseToMap(mapping: 'T -> option<'R>) =
            match mapping x.Value with
            | Some v ->
                HashMapLeaf.New(x.Hash, x.Value, v, HashSetLinked.chooseToMap mapping x.Next)
            | None -> 
                let rest = HashSetLinked.chooseToMap mapping x.Next
                match HashMapLinked.destruct rest with
                | ValueSome (struct (key, value, rest)) ->
                    HashMapLeaf.New(x.Hash, key, value, rest)
                | ValueNone ->
                    HashMapEmpty.Instance

        override x.ChooseToMapV(mapping: 'T -> voption<'R>) =
            match mapping x.Value with
            | ValueSome v ->
                HashMapLeaf.New(x.Hash, x.Value, v, HashSetLinked.chooseToMapV mapping x.Next)
            | ValueNone -> 
                let rest = HashSetLinked.chooseToMapV mapping x.Next
                match HashMapLinked.destruct rest with
                | ValueSome (struct (key, value, rest)) ->
                    HashMapLeaf.New(x.Hash, key, value, rest)
                | ValueNone ->
                    HashMapEmpty.Instance

        override x.ChooseToMapV2(mapping: 'T -> struct (ValueOption<'T1> * ValueOption<'T2>)) =
            let struct (l,r) = mapping x.Value
            let struct (ln, rn) = HashSetLinked.chooseToMapV2 mapping x.Next
            let left = 
                match l with
                | ValueSome v -> HashMapLeaf.New(x.Hash, x.Value, v, ln) :> HashMapNode<_,_>
                | ValueNone -> 
                    match HashMapLinked.destruct ln with
                    | ValueSome (struct (key, value, rest)) ->
                        HashMapLeaf.New(x.Hash, key, value, rest)
                    | ValueNone ->
                        HashMapEmpty.Instance
            let right = 
                match r with
                | ValueSome v -> HashMapLeaf.New(x.Hash, x.Value, v, rn) :> HashMapNode<_,_>
                | ValueNone -> 
                    match HashMapLinked.destruct rn with
                    | ValueSome (struct (key, value, rest)) ->
                        HashMapLeaf.New(x.Hash, key, value, rest)
                    | ValueNone ->
                        HashMapEmpty.Instance
            struct (left, right)

        override x.Filter(predicate: 'T -> bool) =
            if predicate x.Value then
                HashSetLeaf.New(x.Hash, x.Value, HashSetLinked.filter predicate x.Next)
            else
                let rest = HashSetLinked.filter predicate x.Next
                match HashSetLinked.destruct rest with
                | ValueSome (struct (value, rest)) ->
                    HashSetLeaf.New(x.Hash, value, rest)
                | ValueNone ->
                    HashSetEmpty.Instance

        override x.Iter(action: 'T -> unit) =
            action x.Value
            let mutable n = x.Next
            while not (isNull n) do
                action n.Value
                n <- n.Next
                
        override x.Fold(acc: OptimizedClosures.FSharpFunc<'S, 'T, 'S>, seed : 'S) =
            let mutable res = acc.Invoke(seed, x.Value)
            let mutable n = x.Next
            while not (isNull n) do
                res <- acc.Invoke(res, n.Value)
                n <- n.Next
            res

        override x.Exists(predicate: 'T -> bool) =
            if predicate x.Value then true
            else HashSetLinked.exists predicate x.Next
                
        override x.Forall(predicate: 'T -> bool) =
            if predicate x.Value then HashSetLinked.forall predicate x.Next
            else false

        override x.CopyTo(dst : 'T array, index : int) =
            dst.[index] <- x.Value
            HashSetLinked.copyTo (index + 1) dst x.Next
            
        override x.ToList acc =
            let rec run (acc : list<'T>) (n : HashSetLinked<'T>) =
                if isNull n then acc
                else n.Value :: run acc n.Next
            x.Value :: run acc x.Next

        static member New(h: uint32, v: 'T, n: HashSetLinked<'T>) : HashSetNode<'T> = 
            assert (not (isNull n))
            new HashSetCollisionLeaf<_>(Hash = h, Value = v, Next = n) :> HashSetNode<'T>
 
    type HashSetInner<'T>() =
        inherit HashSetNode<'T>()
        [<DefaultValue>]
        val mutable public Prefix: uint32
        [<DefaultValue>]
        val mutable public Mask: Mask
        [<DefaultValue>]
        val mutable public Left: HashSetNode<'T>
        [<DefaultValue>]
        val mutable public Right: HashSetNode<'T>
        [<DefaultValue>]
        val mutable public _Count: int

        override x.Count = x._Count

        static member Join (p0 : uint32, t0 : HashSetNode<'T>, p1 : uint32, t1 : HashSetNode<'T>) : HashSetNode<'T>=
            if t0.IsEmpty then t1
            elif t1.IsEmpty then t0
            else
                let m = getMask p0 p1
                if zeroBit p0 m = 0u then HashSetInner.New(getPrefix p0 m, m, t0, t1)
                else HashSetInner.New(getPrefix p0 m, m, t1, t0)

        static member Create(p: uint32, m: Mask, l: HashSetNode<'T>, r: HashSetNode<'T>) =
            if r.IsEmpty then l
            elif l.IsEmpty then r
            else HashSetInner.New(p, m, l, r)

        override x.ComputeHash() =
            combineHash (int x.Mask) (combineHash (x.Left.ComputeHash()) (x.Right.ComputeHash()))

        override x.IsEmpty = false
        
        override x.Accept(v: HashSetVisitor<_,_>) =
            v.VisitNode x

        override x.Contains(cmp: IEqualityComparer<'T>, hash: uint32, value: 'T) =
            let m = zeroBit hash x.Mask
            if m = 0u then x.Left.Contains(cmp, hash, value)
            else x.Right.Contains(cmp, hash, value)

        override x.Remove(cmp: IEqualityComparer<'T>, hash: uint32, value: 'T) =
            let m = matchPrefixAndGetBit hash x.Prefix x.Mask
            if m = 0u then 
                let l = x.Left.Remove(cmp, hash, value)
                if l == x.Left then x :> _
                else HashSetInner.Create(x.Prefix, x.Mask, l, x.Right)
            elif m = 1u then
                let r = x.Right.Remove(cmp, hash, value)
                if r == x.Right then x :> _
                else HashSetInner.Create(x.Prefix, x.Mask, x.Left, r)
            else
                x:> _

        override x.TryRemove(cmp: IEqualityComparer<'T>, hash: uint32, value: 'T) =
            let m = matchPrefixAndGetBit hash x.Prefix x.Mask
            if m = 0u then 
                match x.Left.TryRemove(cmp, hash, value) with
                | ValueSome ll ->
                    ValueSome (HashSetInner.Create(x.Prefix, x.Mask, ll, x.Right))
                | ValueNone ->
                    ValueNone
            elif m = 1u then
                match x.Right.TryRemove(cmp, hash, value) with
                | ValueSome rr ->
                    ValueSome (HashSetInner.Create(x.Prefix, x.Mask, x.Left, rr))
                | ValueNone ->
                    ValueNone
            else
                ValueNone

        override x.AddInPlaceUnsafe(cmp: IEqualityComparer<'T>, hash: uint32, value: 'T) =
            let m = matchPrefixAndGetBit hash x.Prefix x.Mask
            if m = 0u then 
                x.Left <- x.Left.AddInPlaceUnsafe(cmp, hash, value)
                x._Count <- x.Left.Count + x.Right.Count
                x:> HashSetNode<_>
            elif m = 1u then 
                x.Right <- x.Right.AddInPlaceUnsafe(cmp, hash, value)
                x._Count <- x.Left.Count + x.Right.Count
                x:> HashSetNode<_>
            else
                let n = HashSetNoCollisionLeaf.New(hash, value)
                HashSetInner.Join(x.Prefix, x, hash, n)

        override x.Add(cmp: IEqualityComparer<'T>, hash: uint32, value: 'T) =
            let m = matchPrefixAndGetBit hash x.Prefix x.Mask
            if m = 0u then 
                HashSetInner.New(x.Prefix, x.Mask, x.Left.Add(cmp, hash, value), x.Right)
            elif m = 1u then 
                HashSetInner.New(x.Prefix, x.Mask, x.Left, x.Right.Add(cmp, hash, value))
            else
                let n = HashSetNoCollisionLeaf.New(hash, value)
                HashSetInner.Join(x.Prefix, x, hash, n)

        override x.Alter(cmp: IEqualityComparer<'T>, hash: uint32, value: 'T, update: bool -> bool) =
            let m = matchPrefixAndGetBit hash x.Prefix x.Mask
            if m = 0u then 
                let ll = x.Left.Alter(cmp, hash, value, update)
                if ll == x.Left then x:> _
                else HashSetInner.Create(x.Prefix, x.Mask, ll, x.Right)
            elif m = 1u then
                let rr = x.Right.Alter(cmp, hash, value, update)
                if rr == x.Right then x:> _
                else HashSetInner.Create(x.Prefix, x.Mask, x.Left, rr)
            else
                if update false then
                    let n = HashSetNoCollisionLeaf.New(hash, value)
                    HashSetInner.Join(x.Prefix, x, hash, n)
                else
                    x:> _
                    
        override x.MapToMap(mapping: 'T -> 'R) =
            HashMapInner.New(x.Prefix, x.Mask, x.Left.MapToMap(mapping), x.Right.MapToMap(mapping))
  
        override x.ChooseToMap(mapping: 'T -> option<'R>) =
            HashMapInner.Create(x.Prefix, x.Mask, x.Left.ChooseToMap(mapping), x.Right.ChooseToMap(mapping))
            
        override x.ChooseToMapV(mapping: 'T -> voption<'R>) =
            HashMapInner.Create(x.Prefix, x.Mask, x.Left.ChooseToMapV(mapping), x.Right.ChooseToMapV(mapping))
      
        override x.ChooseToMapV2(mapping: 'T -> struct(ValueOption<'T1> * ValueOption<'T2>)) =
            let struct (la, lb) = x.Left.ChooseToMapV2(mapping)
            let struct (ra, rb) = x.Right.ChooseToMapV2(mapping)

            struct (
                HashMapInner.Create(x.Prefix, x.Mask, la, ra),
                HashMapInner.Create(x.Prefix, x.Mask, lb, rb)
            )
      
        override x.Filter(predicate: 'T -> bool) =
            HashSetInner.Create(x.Prefix, x.Mask, x.Left.Filter(predicate), x.Right.Filter(predicate))
            
        override x.Iter(action: 'T -> unit) =
            x.Left.Iter(action)
            x.Right.Iter(action)

        override x.Fold(acc: OptimizedClosures.FSharpFunc<'S, 'T, 'S>, seed : 'S) =
            let s = x.Left.Fold(acc, seed)
            x.Right.Fold(acc, s)
            

        override x.Exists(predicate: 'T -> bool) =
            x.Left.Exists predicate || x.Right.Exists predicate
                
        override x.Forall(predicate: 'T -> bool) =
            x.Left.Forall predicate && x.Right.Forall predicate

        override x.CopyTo(dst : 'T array, index : int) =
            let i = x.Left.CopyTo(dst, index)
            x.Right.CopyTo(dst, i)
            
        override x.ToList acc =
            let a = x.Right.ToList acc
            x.Left.ToList a

        static member New(p: uint32, m: Mask, l: HashSetNode<'T>, r: HashSetNode<'T>) : HashSetNode<'T> = 
            assert(not l.IsEmpty)
            assert(not r.IsEmpty)
            new HashSetInner<_>(Prefix = p, Mask = m, Left = l, Right = r, _Count = l.Count + r.Count) :> _

    // ========================================================================================================================
    // HashMapNode implementation
    // ========================================================================================================================

    [<AllowNullLiteral>]
    type HashMapLinked<'K, 'V> =
        val mutable public Next: HashMapLinked<'K, 'V>
        val mutable public Key: 'K
        val mutable public Value: 'V

        new(k : 'K, v : 'V) = { Key = k; Value = v; Next = null }
        new(k : 'K, v : 'V, n : HashMapLinked<'K, 'V>) = { Key = k; Value = v; Next = n }

    module HashMapLinked =
    
        let rec keys (n: HashMapLinked<'K, 'V>) =
            if isNull n then
                null
            else
                HashSetLinked<'K>(n.Key, keys n.Next)
                

        let rec addInPlaceUnsafe (cmp: IEqualityComparer<'K>) (key: 'K) (value: 'V) (n: HashMapLinked<'K, 'V>) =
            if isNull n then
                HashMapLinked(key, value)
            elif cmp.Equals(n.Key, key) then
                n.Key <- key
                n.Value <- value
                n
            else
                n.Next <- addInPlaceUnsafe cmp key value n.Next
                n

        let rec add (cmp: IEqualityComparer<'K>) (key: 'K) (value: 'V) (n: HashMapLinked<'K, 'V>) =
            if isNull n then
                HashMapLinked(key, value)
            elif cmp.Equals(n.Key, key) then
                HashMapLinked(key, value, n.Next)
            else
                HashMapLinked(n.Key, n.Value, add cmp key value n.Next)
               
        let rec alter (cmp: IEqualityComparer<'K>) (key: 'K) (update: option<'V> -> option<'V>) (n: HashMapLinked<'K, 'V>) =
            if isNull n then
                match update None with
                | Some value -> 
                    HashMapLinked(key, value)
                | None ->
                    null
            elif cmp.Equals(n.Key, key) then
                match update (Some n.Value) with
                | Some value -> 
                    HashMapLinked(key, value, n.Next)
                | None -> 
                    n.Next
            else
                let next = alter cmp key update n.Next
                if next == n.Next then n
                else HashMapLinked(n.Key, n.Value, next)
               
        let rec alterV (cmp: IEqualityComparer<'K>) (key: 'K) (update: voption<'V> -> voption<'V>) (n: HashMapLinked<'K, 'V>) =
            if isNull n then
                match update ValueNone with
                | ValueSome value -> 
                    HashMapLinked(key, value)
                | ValueNone ->
                    null
            elif cmp.Equals(n.Key, key) then
                match update (ValueSome n.Value) with
                | ValueSome value -> 
                    HashMapLinked(key, value, n.Next)
                | ValueNone -> 
                    n.Next
            else
                let next = alterV cmp key update n.Next
                if next == n.Next then n
                else HashMapLinked(n.Key, n.Value, next)
               
        let rec change (cmp: IEqualityComparer<'K>) (key: 'K) (changed : byref<bool>) (update: voption<'V> -> voption<'V>) (n: HashMapLinked<'K, 'V>) =
            if isNull n then
                match update ValueNone with
                | ValueSome value -> 
                    changed <- true
                    HashMapLinked(key, value)
                | ValueNone ->
                    null
            elif cmp.Equals(n.Key, key) then
                match update (ValueSome n.Value) with
                | ValueSome value -> 
                    HashMapLinked(key, value, n.Next)
                | ValueNone -> 
                    changed <- true
                    n.Next
            else
                let next = change cmp key &changed update n.Next
                if next == n.Next then n
                else HashMapLinked(n.Key, n.Value, next)
               
        let rec tryFind (cmp: IEqualityComparer<'K>) (key: 'K) (n: HashMapLinked<'K, 'V>) =
            if isNull n then None
            elif cmp.Equals(n.Key, key) then Some n.Value
            else tryFind cmp key n.Next
            
        let rec tryFindV (cmp: IEqualityComparer<'K>) (key: 'K) (n: HashMapLinked<'K, 'V>) =
            if isNull n then ValueNone
            elif cmp.Equals(n.Key, key) then ValueSome n.Value
            else tryFindV cmp key n.Next
            
        let rec containsKey (cmp: IEqualityComparer<'K>) (key: 'K) (n: HashMapLinked<'K, 'V>) =
            if isNull n then false
            elif cmp.Equals(n.Key, key) then true
            else containsKey cmp key n.Next

        let destruct<'K, 'V> (n: HashMapLinked<'K, 'V>) : voption<struct('K * 'V * HashMapLinked<'K, 'V>)> =
            if isNull n then ValueNone
            else ValueSome(struct (n.Key, n.Value, n.Next))
            
        let rec remove (cmp: IEqualityComparer<'K>) (key: 'K) (n: HashMapLinked<'K, 'V>) =
            if isNull n then
                null
            elif cmp.Equals(n.Key, key) then 
                n.Next
            else
                let rest = remove cmp key n.Next
                if rest == n.Next then n
                else HashMapLinked(n.Key, n.Value, rest)

        let rec tryRemove (cmp: IEqualityComparer<'K>) (key: 'K) (n: HashMapLinked<'K, 'V>) =
            if isNull n then
                ValueNone
            elif cmp.Equals(n.Key, key) then 
                ValueSome (struct(n.Value, n.Next))
            else
                match tryRemove cmp key n.Next with
                | ValueSome (struct (value, rest)) ->
                    ValueSome(struct(value, HashMapLinked(n.Key, n.Value, rest)))
                | ValueNone ->
                    ValueNone

        let rec map (mapping: OptimizedClosures.FSharpFunc<'K, 'V, 'T>) (n: HashMapLinked<'K, 'V>) = 
            if isNull n then
                null
            else 
                let r = mapping.Invoke(n.Key, n.Value)
                HashMapLinked(n.Key, r, map mapping n.Next)

        let rec choose (mapping: OptimizedClosures.FSharpFunc<'K, 'V, option<'T>>) (n: HashMapLinked<'K, 'V>) = 
            if isNull n then
                null
            else 
                match mapping.Invoke(n.Key, n.Value) with
                | Some r -> 
                    HashMapLinked(n.Key, r, choose mapping n.Next)
                | None -> 
                    choose mapping n.Next
    
        let rec chooseV (mapping: OptimizedClosures.FSharpFunc<'K, 'V, ValueOption<'T>>) (n: HashMapLinked<'K, 'V>) = 
            if isNull n then
                null
            else 
                match mapping.Invoke(n.Key, n.Value) with
                | ValueSome r -> 
                    HashMapLinked(n.Key, r, chooseV mapping n.Next)
                | ValueNone -> 
                    chooseV mapping n.Next
    
        let rec chooseV2 (mapping: OptimizedClosures.FSharpFunc<'K, 'V, struct(ValueOption<'T1> * ValueOption<'T2>)>) (n: HashMapLinked<'K, 'V>) = 
            if isNull n then
                struct(null, null)
            else 
                let struct (l, r) = mapping.Invoke(n.Key, n.Value)
                let struct (lr, rr) = chooseV2 mapping n.Next

                let left = 
                    match l with
                    | ValueSome l -> HashMapLinked(n.Key, l, lr)
                    | ValueNone -> lr
                let right =
                    match r with
                    | ValueSome r -> HashMapLinked(n.Key, r, rr)
                    | ValueNone -> rr
                struct(left, right)

        let rec chooseSV2 (mapping: OptimizedClosures.FSharpFunc<'K, 'V, struct(bool * ValueOption<'T2>)>) (n: HashMapLinked<'K, 'V>) = 
            if isNull n then
                struct(null, null)
            else 
                let struct (l, r) = mapping.Invoke(n.Key, n.Value)
                let struct (lr, rr) = chooseSV2 mapping n.Next

                let left = 
                    if l then HashSetLinked(n.Key, lr)
                    else lr

                let right =
                    match r with
                    | ValueSome r -> HashMapLinked(n.Key, r, rr)
                    | ValueNone -> rr
                struct(left, right)
    
    
        let rec filter (predicate: OptimizedClosures.FSharpFunc<'K, 'V, bool>) (n: HashMapLinked<'K, 'V>) =
            if isNull n then
                null
            elif predicate.Invoke(n.Key, n.Value) then
                HashMapLinked(n.Key, n.Value, filter predicate n.Next)
            else
                filter predicate n.Next
    
        let rec exists (predicate: OptimizedClosures.FSharpFunc<'K, 'V, bool>) (n: HashMapLinked<'K, 'V>) =
            if isNull n then 
                false
            elif predicate.Invoke(n.Key, n.Value) then
                true
            else
                exists predicate n.Next
                
        let rec forall (predicate: OptimizedClosures.FSharpFunc<'K, 'V, bool>) (n: HashMapLinked<'K, 'V>) =
            if isNull n then 
                true
            elif not (predicate.Invoke(n.Key, n.Value)) then
                false
            else
                forall predicate n.Next

        let rec copyTo (index: int) (dst : ('K * 'V) array) (n: HashMapLinked<'K, 'V>) =
            if not (isNull n) then
                dst.[index] <- n.Key, n.Value
                copyTo (index + 1) dst n.Next
            else
                index

        let rec copyToV (index: int) (dst : (struct ('K * 'V)) array) (n: HashMapLinked<'K, 'V>) =
            if not (isNull n) then
                dst.[index] <- struct (n.Key, n.Value)
                copyToV (index + 1) dst n.Next
            else
                index
                
        let rec copyToKeys (index: int) (dst : 'K[]) (n: HashMapLinked<'K, 'V>) =
            if not (isNull n) then
                dst.[index] <- n.Key
                copyToKeys (index + 1) dst n.Next
            else
                index

        let rec copyToValues (index: int) (dst : 'V[]) (n: HashMapLinked<'K, 'V>) =
            if not (isNull n) then
                dst.[index] <- n.Value
                copyToValues (index + 1) dst n.Next
            else
                index

    [<AbstractClass>]
    type HashMapNode<'K, 'V>() =
        abstract member Remove: IEqualityComparer<'K> * uint32 * 'K -> HashMapNode<'K, 'V>
        abstract member TryRemove: IEqualityComparer<'K> * uint32 * 'K -> ValueOption<struct ('V * HashMapNode<'K, 'V>)>

        abstract member Count : int
        abstract member IsEmpty: bool
        abstract member ComputeHash : unit -> int

        abstract member Change : IEqualityComparer<'K> * uint32 * 'K * changed : byref<bool> * (voption<'V> -> voption<'V>) -> HashMapNode<'K, 'V>

        abstract member AddInPlaceUnsafe: IEqualityComparer<'K> * uint32 * 'K * 'V -> HashMapNode<'K, 'V>
        abstract member Add: IEqualityComparer<'K> * uint32 * 'K * 'V -> HashMapNode<'K, 'V>
        abstract member Alter: IEqualityComparer<'K> * uint32 * 'K * (option<'V> -> option<'V>) -> HashMapNode<'K, 'V>
        abstract member TryFind: IEqualityComparer<'K> * uint32 * 'K -> option<'V>
        abstract member AlterV: IEqualityComparer<'K> * uint32 * 'K * (voption<'V> -> voption<'V>) -> HashMapNode<'K, 'V>
        abstract member TryFindV: IEqualityComparer<'K> * uint32 * 'K -> voption<'V>
        abstract member ContainsKey: IEqualityComparer<'K> * uint32 * 'K -> bool

        abstract member Map: mapping: OptimizedClosures.FSharpFunc<'K, 'V, 'T> -> HashMapNode<'K, 'T>
        abstract member Choose: mapping: OptimizedClosures.FSharpFunc<'K, 'V, option<'T>> -> HashMapNode<'K, 'T>
        abstract member ChooseV: mapping: OptimizedClosures.FSharpFunc<'K, 'V, ValueOption<'T>> -> HashMapNode<'K, 'T>
        abstract member ChooseV2: mapping: OptimizedClosures.FSharpFunc<'K, 'V, struct(ValueOption<'T1> * ValueOption<'T2>)> -> struct (HashMapNode<'K, 'T1> * HashMapNode<'K, 'T2>)
        abstract member ChooseSV2: mapping: OptimizedClosures.FSharpFunc<'K, 'V, struct(bool * ValueOption<'T2>)> -> struct (HashSetNode<'K> * HashMapNode<'K, 'T2>)

        abstract member Filter: mapping: OptimizedClosures.FSharpFunc<'K, 'V, bool> -> HashMapNode<'K, 'V>
        abstract member Iter: action: OptimizedClosures.FSharpFunc<'K, 'V, unit> -> unit
        abstract member Fold: acc: OptimizedClosures.FSharpFunc<'S, 'K, 'V, 'S> * seed : 'S -> 'S
        abstract member Exists: predicate: OptimizedClosures.FSharpFunc<'K, 'V, bool> -> bool
        abstract member Forall: predicate: OptimizedClosures.FSharpFunc<'K, 'V, bool> -> bool

        abstract member GetKeys : unit -> HashSetNode<'K>

        abstract member Accept: HashMapVisitor<'K, 'V, 'R> -> 'R

        abstract member CopyTo: dst: ('K * 'V) array * index : int -> int
        abstract member CopyToV: dst: (struct('K * 'V)) array * index : int -> int
        abstract member CopyToKeys: dst: 'K[] * index : int -> int
        abstract member CopyToValues: dst: 'V[] * index : int -> int

        abstract member ToKeyList : list<'K> -> list<'K>
        abstract member ToValueList : list<'V> -> list<'V>
        abstract member ToList : list<'K * 'V> -> list<'K * 'V>
        abstract member ToListV : list<struct('K * 'V)> -> list<struct('K * 'V)>

    [<AbstractClass>]
    type HashMapLeaf<'K, 'V>() =
        inherit HashMapNode<'K, 'V>()
        abstract member LHash : uint32
        abstract member LKey : 'K
        abstract member LValue : 'V
        abstract member LNext : HashMapLinked<'K, 'V>
        
        static member New(h: uint32, k: 'K, v: 'V, n: HashMapLinked<'K, 'V>) : HashMapNode<'K, 'V> = 
            if isNull n then new HashMapNoCollisionLeaf<_,_>(Hash = h, Key = k, Value = v) :> HashMapNode<'K, 'V>
            else new HashMapCollisionLeaf<_,_>(Hash = h, Key = k, Value = v, Next = n) :> HashMapNode<'K, 'V>
     
    [<Sealed>]
    type HashMapEmpty<'K, 'V> private() =
        inherit HashMapNode<'K, 'V>()
        static let instance = HashMapEmpty<'K, 'V>() :> HashMapNode<_,_>
        static member Instance : HashMapNode<'K, 'V> = instance

        override x.Count = 0

        override x.ComputeHash() =
            0

        override x.GetKeys() =
            HashSetEmpty<'K>.Instance

        override x.Accept(v: HashMapVisitor<_,_,_>) =
            v.VisitEmpty x

        override x.IsEmpty = true

        override x.TryFind(_cmp: IEqualityComparer<'K>, _hash: uint32, _key: 'K) =
            None
            
        override x.TryFindV(_cmp: IEqualityComparer<'K>, _hash: uint32, _key: 'K) =
            ValueNone

        override x.ContainsKey(_cmp: IEqualityComparer<'K>, _hash: uint32, _key: 'K) =
            false

        override x.Remove(_cmp: IEqualityComparer<'K>, _hash: uint32, _key: 'K) =
            x:> _
            
        override x.TryRemove(_cmp: IEqualityComparer<'K>, _hash: uint32, _key: 'K) =
            ValueNone

        override x.AddInPlaceUnsafe(_cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, value: 'V) =
            HashMapNoCollisionLeaf.New(hash, key, value)

        override x.Add(_cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, value: 'V) =
            HashMapNoCollisionLeaf.New(hash, key, value)

        override x.Alter(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, update: option<'V> -> option<'V>) =
            match update None with
            | None -> x:> _
            | Some value ->
                HashMapNoCollisionLeaf.New(hash, key, value)
                
        override x.Change(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, changed : byref<bool>, update: voption<'V> -> voption<'V>) =
            match update ValueNone with
            | ValueNone -> x :> _
            | ValueSome value ->
                changed <- true
                HashMapNoCollisionLeaf.New(hash, key, value)

        override x.AlterV(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, update: voption<'V> -> voption<'V>) =
            match update ValueNone with
            | ValueNone -> x:> _
            | ValueSome value ->
                HashMapNoCollisionLeaf.New(hash, key, value)

        override x.Map(_mapping: OptimizedClosures.FSharpFunc<'K, 'V, 'T>) =
            HashMapEmpty.Instance
            
        override x.Choose(_mapping: OptimizedClosures.FSharpFunc<'K, 'V, option<'T>>) =
            HashMapEmpty.Instance
            
        override x.ChooseV(_mapping: OptimizedClosures.FSharpFunc<'K, 'V, ValueOption<'T>>) =
            HashMapEmpty.Instance
                 
        override x.ChooseV2(_mapping) =
            struct(HashMapEmpty.Instance, HashMapEmpty.Instance)
                 
        override x.ChooseSV2(_mapping) =
            struct(HashSetEmpty.Instance, HashMapEmpty.Instance)
                                
        override x.Filter(_predicate: OptimizedClosures.FSharpFunc<'K, 'V, bool>) =
            HashMapEmpty.Instance

        override x.Iter(_action: OptimizedClosures.FSharpFunc<'K, 'V, unit>) =
            ()
            
        override x.Fold(_acc: OptimizedClosures.FSharpFunc<'S, 'K, 'V, 'S>, seed : 'S) =
            seed

        override x.Exists(_predicate: OptimizedClosures.FSharpFunc<'K, 'V, bool>) =
            false

        override x.Forall(_predicate: OptimizedClosures.FSharpFunc<'K, 'V, bool>) =
            true

        override x.CopyTo(_dst : ('K * 'V) array, index : int) =
            index

        override x.CopyToV(_dst : (struct ('K * 'V)) array, index : int) =
            index
            
        override x.CopyToKeys(_dst : 'K[], index : int) =
            index

        override x.CopyToValues(_dst : 'V[], index : int) =
            index

        override x.ToList(acc : list<'K * 'V>) =
            acc
            
        override x.ToListV(acc : list<struct('K * 'V)>) =
            acc

        override x.ToKeyList(acc : list<'K>) =
            acc
            
        override x.ToValueList(acc : list<'V>) =
            acc

    [<Sealed>]
    type HashMapCollisionLeaf<'K, 'V>() =
        inherit HashMapLeaf<'K, 'V>()

        [<DefaultValue>]
        val mutable public Next: HashMapLinked<'K, 'V>
        [<DefaultValue>]
        val mutable public Key: 'K
        [<DefaultValue>]
        val mutable public Value: 'V
        [<DefaultValue>]
        val mutable public Hash: uint32
  
        override x.Count =
            let mutable cnt = 1
            let mutable c = x.Next
            while not (isNull c) do
                c <- c.Next
                cnt <- cnt + 1
            cnt

        override x.LHash = x.Hash
        override x.LKey = x.Key
        override x.LValue = x.Value
        override x.LNext = x.Next

        override x.ComputeHash() =
            let mutable vh = (DefaultEquality.hash x.Value)
            let mutable c = x.Next
            while not (isNull c) do
                vh <- vh ^^^ (DefaultEquality.hash c.Value)
                c <- c.Next
            combineHash (int x.Hash) vh

        override x.GetKeys() =
            HashSetCollisionLeaf<'K>.New(x.Hash, x.Key, HashMapLinked.keys x.Next)

        override x.Accept(v: HashMapVisitor<_,_,_>) =
            v.VisitLeaf x

        override x.IsEmpty = false
        
        override x.TryFind(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K) =   
            if hash = x.Hash then
                if cmp.Equals(key, x.Key) then 
                    Some x.Value
                else
                    HashMapLinked.tryFind cmp key x.Next
            else
                None

        override x.TryFindV(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K) =   
            if hash = x.Hash then
                if cmp.Equals(key, x.Key) then 
                    ValueSome x.Value
                else
                    HashMapLinked.tryFindV cmp key x.Next
            else
                ValueNone

        override x.ContainsKey(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K) =   
            if hash = x.Hash then
                if cmp.Equals(key, x.Key) then 
                    true
                else
                    HashMapLinked.containsKey cmp key x.Next
            else
                false

        override x.Remove(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K) =
            if hash = x.Hash then
                if cmp.Equals(key, x.Key) then
                    match HashMapLinked.destruct x.Next with
                    | ValueSome (struct (k, v, rest)) ->
                        HashMapLeaf.New(hash, k, v, rest)
                    | ValueNone ->
                        HashMapEmpty.Instance
                else
                    let next = HashMapLinked.remove cmp key x.Next
                    if next == x.Next then x :> _
                    else HashMapLeaf.New(x.Hash, x.Key, x.Value, next)
            else
                x:> _

        override x.TryRemove(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K)         =
            if hash = x.Hash then
                if cmp.Equals(key, x.Key) then
                    match HashMapLinked.destruct x.Next with
                    | ValueSome (struct (k, v, rest)) ->
                        ValueSome(struct(x.Value, HashMapLeaf.New(hash, k, v, rest)))
                    | ValueNone ->
                        ValueSome(struct(x.Value, HashMapEmpty.Instance))
                else
                    match HashMapLinked.tryRemove cmp key x.Next with
                    | ValueSome(struct(value, rest)) ->
                        ValueSome(
                            struct(
                                value,
                                HashMapLeaf.New(x.Hash, x.Key, x.Value, rest)
                            )
                        )
                    | ValueNone ->
                        ValueNone
            else
                ValueNone

        override x.AddInPlaceUnsafe(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, value: 'V) =
            if x.Hash = hash then
                if cmp.Equals(key, x.Key) then
                    x.Key <- key
                    x.Value <- value
                    x:> _
                else
                    x.Next <- HashMapLinked.addInPlaceUnsafe cmp key value x.Next
                    x:> _
            else
                let n = HashMapNoCollisionLeaf.New(hash, key, value)
                HashMapInner.Join(hash, n, x.Hash, x)
                
        override x.Add(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, value: 'V) =
            if x.Hash = hash then
                if cmp.Equals(key, x.Key) then
                    HashMapCollisionLeaf.New(x.Hash, key, value, x.Next)
                else
                    HashMapCollisionLeaf.New(x.Hash, x.Key, x.Value, HashMapLinked.add cmp key value x.Next)
            else
                let n = HashMapNoCollisionLeaf.New(hash, key, value)
                HashMapInner.Join(hash, n, x.Hash, x)

        override x.Alter(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, update: option<'V> -> option<'V>) =
            if x.Hash = hash then
                if cmp.Equals(key, x.Key) then
                    match update (Some x.Value) with
                    | None ->
                        // remove
                        match HashMapLinked.destruct x.Next with
                        | ValueSome (struct (k, v, rest)) ->
                            HashMapLeaf.New(x.Hash, k, v, rest)
                        | ValueNone ->
                            HashMapEmpty.Instance
                    | Some value ->
                        // update
                        HashMapCollisionLeaf.New(x.Hash, x.Key, value, x.Next) 
                else
                    // in linked?
                    let n = HashMapLinked.alter cmp key update x.Next
                    if n == x.Next then x:> _
                    else HashMapLeaf.New(x.Hash, x.Key, x.Value, n)
            else
                // other hash => not contained
                match update None with
                | None -> x:> _
                | Some value ->
                    // add
                    let n = HashMapNoCollisionLeaf.New(hash, key, value)
                    HashMapInner.Join(hash, n, x.Hash, x)

        override x.AlterV(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, update: voption<'V> -> voption<'V>) =
            if x.Hash = hash then
                if cmp.Equals(key, x.Key) then
                    match update (ValueSome x.Value) with
                    | ValueNone ->
                        // remove
                        match HashMapLinked.destruct x.Next with
                        | ValueSome (struct (k, v, rest)) ->
                            HashMapLeaf.New(x.Hash, k, v, rest)
                        | ValueNone ->
                            HashMapEmpty.Instance
                    | ValueSome value ->
                        // update
                        HashMapCollisionLeaf.New(x.Hash, x.Key, value, x.Next) 
                else
                    // in linked?
                    let n = HashMapLinked.alterV cmp key update x.Next
                    if n == x.Next then x:> _
                    else HashMapLeaf.New(x.Hash, x.Key, x.Value, n)
            else
                // other hash => not contained
                match update ValueNone with
                | ValueNone -> x:> _
                | ValueSome value ->
                    // add
                    let n = HashMapNoCollisionLeaf.New(hash, key, value)
                    HashMapInner.Join(hash, n, x.Hash, x)
                    
        override x.Change(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, changed : byref<bool>, update: voption<'V> -> voption<'V>) =
            if x.Hash = hash then
                if cmp.Equals(key, x.Key) then
                    match update (ValueSome x.Value) with
                    | ValueNone ->
                        // remove
                        changed <- true
                        match HashMapLinked.destruct x.Next with
                        | ValueSome (struct (k, v, rest)) ->
                            HashMapLeaf.New(x.Hash, k, v, rest)
                        | ValueNone ->
                            HashMapEmpty.Instance
                    | ValueSome value ->
                        // update
                        HashMapCollisionLeaf.New(x.Hash, x.Key, value, x.Next) 
                else
                    // in linked?
                    let n = HashMapLinked.change cmp key &changed update x.Next
                    if n == x.Next then x:> _
                    else HashMapLeaf.New(x.Hash, x.Key, x.Value, n)
            else
                // other hash => not contained
                match update ValueNone with
                | ValueNone -> x:> _
                | ValueSome value ->
                    // add
                    changed <- true
                    let n = HashMapNoCollisionLeaf.New(hash, key, value)
                    HashMapInner.Join(hash, n, x.Hash, x)

        override x.Map(mapping: OptimizedClosures.FSharpFunc<'K, 'V, 'T>) =
            let t = mapping.Invoke(x.Key, x.Value)
            HashMapCollisionLeaf.New(x.Hash, x.Key, t, HashMapLinked.map mapping x.Next)
            
        override x.Choose(mapping: OptimizedClosures.FSharpFunc<'K, 'V, option<'T>>) =
            match mapping.Invoke(x.Key, x.Value) with
            | Some v ->
                HashMapLeaf.New(x.Hash, x.Key, v, HashMapLinked.choose mapping x.Next)
            | None -> 
                let rest = HashMapLinked.choose mapping x.Next
                match HashMapLinked.destruct rest with
                | ValueSome (struct (key, value, rest)) ->
                    HashMapLeaf.New(x.Hash, key, value, rest)
                | ValueNone ->
                    HashMapEmpty.Instance

        override x.ChooseV(mapping: OptimizedClosures.FSharpFunc<'K, 'V, ValueOption<'T>>) =
            match mapping.Invoke(x.Key, x.Value) with
            | ValueSome v ->
                HashMapLeaf.New(x.Hash, x.Key, v, HashMapLinked.chooseV mapping x.Next)
            | ValueNone -> 
                let rest = HashMapLinked.chooseV mapping x.Next
                match HashMapLinked.destruct rest with
                | ValueSome (struct (key, value, rest)) ->
                    HashMapLeaf.New(x.Hash, key, value, rest)
                | ValueNone ->
                    HashMapEmpty.Instance

        override x.ChooseV2(mapping: OptimizedClosures.FSharpFunc<'K, 'V, struct (ValueOption<'T1> * ValueOption<'T2>)>) =
            let struct (l,r) = mapping.Invoke(x.Key, x.Value)
            let struct (ln, rn) = HashMapLinked.chooseV2 mapping x.Next
            let left = 
                match l with
                | ValueSome v -> HashMapLeaf.New(x.Hash, x.Key, v, ln) :> HashMapNode<_,_>
                | ValueNone -> 
                    match HashMapLinked.destruct ln with
                    | ValueSome (struct (key, value, rest)) ->
                        HashMapLeaf.New(x.Hash, key, value, rest)
                    | ValueNone ->
                        HashMapEmpty.Instance
            let right = 
                match r with
                | ValueSome v -> HashMapLeaf.New(x.Hash, x.Key, v, rn) :> HashMapNode<_,_>
                | ValueNone -> 
                    match HashMapLinked.destruct rn with
                    | ValueSome (struct (key, value, rest)) ->
                        HashMapLeaf.New(x.Hash, key, value, rest)
                    | ValueNone ->
                        HashMapEmpty.Instance
            struct (left, right)

        override x.ChooseSV2(mapping: OptimizedClosures.FSharpFunc<'K, 'V, struct (bool * ValueOption<'T2>)>) =
            let struct (l,r) = mapping.Invoke(x.Key, x.Value)
            let struct (ln, rn) = HashMapLinked.chooseSV2 mapping x.Next
            let left = 
                if l then
                    HashSetLeaf.New(x.Hash, x.Key, ln)
                else
                    match HashSetLinked.destruct ln with
                    | ValueSome (struct (value, rest)) ->
                        HashSetLeaf.New(x.Hash, value, rest)
                    | ValueNone ->
                        HashSetEmpty.Instance
            let right = 
                match r with
                | ValueSome v -> HashMapLeaf.New(x.Hash, x.Key, v, rn) :> HashMapNode<_,_>
                | ValueNone -> 
                    match HashMapLinked.destruct rn with
                    | ValueSome (struct (key, value, rest)) ->
                        HashMapLeaf.New(x.Hash, key, value, rest)
                    | ValueNone ->
                        HashMapEmpty.Instance
            struct (left, right)

        override x.Filter(predicate: OptimizedClosures.FSharpFunc<'K, 'V, bool>) =
            if predicate.Invoke(x.Key, x.Value) then
                HashMapLeaf.New(x.Hash, x.Key, x.Value, HashMapLinked.filter predicate x.Next)
            else
                let rest = HashMapLinked.filter predicate x.Next
                match HashMapLinked.destruct rest with
                | ValueSome (struct (key, value, rest)) ->
                    HashMapLeaf.New(x.Hash, key, value, rest)
                | ValueNone ->
                    HashMapEmpty.Instance

        override x.Iter(action: OptimizedClosures.FSharpFunc<'K, 'V, unit>) =
            action.Invoke(x.Key, x.Value)
            let mutable n = x.Next
            while not (isNull n) do
                action.Invoke(n.Key, n.Value)
                n <- n.Next
                
        override x.Fold(acc: OptimizedClosures.FSharpFunc<'S, 'K, 'V, 'S>, seed : 'S) =
            let mutable res = acc.Invoke(seed, x.Key, x.Value)
            let mutable n = x.Next
            while not (isNull n) do
                res <- acc.Invoke(res, n.Key, n.Value)
                n <- n.Next
            res

        override x.Exists(predicate: OptimizedClosures.FSharpFunc<'K, 'V, bool>) =
            if predicate.Invoke(x.Key, x.Value) then true
            else HashMapLinked.exists predicate x.Next
                
        override x.Forall(predicate: OptimizedClosures.FSharpFunc<'K, 'V, bool>) =
            if predicate.Invoke(x.Key, x.Value) then HashMapLinked.forall predicate x.Next
            else false

        override x.CopyTo(dst : ('K * 'V) array, index : int) =
            dst.[index] <- (x.Key, x.Value)
            HashMapLinked.copyTo (index + 1) dst x.Next
            
        override x.CopyToV(dst : (struct ('K * 'V)) array, index : int) =
            dst.[index] <- struct (x.Key, x.Value)
            HashMapLinked.copyToV (index + 1) dst x.Next
            
        override x.CopyToKeys(dst : 'K[], index : int) =
            dst.[index] <- x.Key
            HashMapLinked.copyToKeys (index + 1) dst x.Next
            
        override x.CopyToValues(dst : 'V[], index : int) =
            dst.[index] <- x.Value
            HashMapLinked.copyToValues (index + 1) dst x.Next
            
        override x.ToList(acc : list<'K * 'V>) =
            let rec run (acc : list<'K * 'V>) (n : HashMapLinked<'K, 'V>) =
                if isNull n then acc
                else (n.Key,n.Value) :: run acc n.Next
            (x.Key, x.Value) :: run acc x.Next
            
        override x.ToListV(acc : list<struct('K * 'V)>) =
            let rec run (acc : list<struct('K * 'V)>) (n : HashMapLinked<'K, 'V>) =
                if isNull n then acc
                else struct(n.Key,n.Value) :: run acc n.Next
            struct(x.Key, x.Value) :: run acc x.Next

        override x.ToKeyList(acc : list<'K>) =
            let rec run (acc : list<_>) (n : HashMapLinked<'K, 'V>) =
                if isNull n then acc
                else n.Key :: run acc n.Next
            x.Key :: run acc x.Next
            
        override x.ToValueList(acc : list<'V>) =
            let rec run (acc : list<_>) (n : HashMapLinked<'K, 'V>) =
                if isNull n then acc
                else n.Value :: run acc n.Next
            x.Value :: run acc x.Next

        static member New(h: uint32, k: 'K, v: 'V, n: HashMapLinked<'K, 'V>) : HashMapNode<'K, 'V> = 
            assert (not (isNull n))
            new HashMapCollisionLeaf<_,_>(Hash = h, Key = k, Value = v, Next = n) :> HashMapNode<'K, 'V>
     
    [<Sealed>]
    type HashMapNoCollisionLeaf<'K, 'V>() =
        inherit HashMapLeaf<'K, 'V>()
        [<DefaultValue>]
        val mutable public Key: 'K
        [<DefaultValue>]
        val mutable public Value: 'V
        [<DefaultValue>]
        val mutable public Hash: uint32

        override x.Count = 1
        override x.LHash = x.Hash
        override x.LKey = x.Key
        override x.LValue = x.Value
        override x.LNext = null
        
        override x.ComputeHash() =
            combineHash (int x.Hash) (DefaultEquality.hash x.Value)

        override x.GetKeys() =
            HashSetNoCollisionLeaf.New(x.Hash, x.Key)

        override x.IsEmpty = false
        
        override x.Accept(v: HashMapVisitor<_,_,_>) =
            v.VisitNoCollision x

        override x.TryFind(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K) =   
            if hash = x.Hash && cmp.Equals(key, x.Key) then 
                Some x.Value
            else
                None

        override x.TryFindV(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K) =   
            if hash = x.Hash && cmp.Equals(key, x.Key) then 
                ValueSome x.Value
            else
                ValueNone

        override x.ContainsKey(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K) =   
            if hash = x.Hash && cmp.Equals(key, x.Key) then 
                true
            else
                false

        override x.Remove(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K) =
            if hash = x.Hash && cmp.Equals(key, x.Key) then
                HashMapEmpty.Instance
            else
                x:> _

        override x.TryRemove(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K) =
            if hash = x.Hash && cmp.Equals(key, x.Key) then
                ValueSome (struct(x.Value, HashMapEmpty.Instance))
            else
                ValueNone

        override x.AddInPlaceUnsafe(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, value: 'V) =
            if x.Hash = hash then
                if cmp.Equals(key, x.Key) then
                    x.Key <- key
                    x.Value <- value
                    x:> _
                else
                    HashMapCollisionLeaf.New(x.Hash, x.Key, x.Value, HashMapLinked(key, value, null))
            else
                let n = HashMapNoCollisionLeaf.New(hash, key, value)
                HashMapInner.Join(hash, n, x.Hash, x)

        override x.Add(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, value: 'V) =
            if x.Hash = hash then
                if cmp.Equals(key, x.Key) then
                    HashMapNoCollisionLeaf.New(x.Hash, key, value)
                else
                    HashMapCollisionLeaf.New(x.Hash, x.Key, x.Value, HashMapLinked.add cmp key value null)
            else
                let n = HashMapNoCollisionLeaf.New(hash, key, value)
                HashMapInner.Join(hash, n, x.Hash, x)
        
        override x.Alter(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, update: option<'V> -> option<'V>) =
            if x.Hash = hash then
                if cmp.Equals(key, x.Key) then
                    match update (Some x.Value) with
                    | Some value -> 
                        HashMapNoCollisionLeaf.New(x.Hash, x.Key, value)
                    | None -> 
                        HashMapEmpty.Instance
                else
                    match update None with
                    | None -> x:> _
                    | Some value ->
                        HashMapCollisionLeaf.New(x.Hash, x.Key, x.Value, HashMapLinked(key, value, null))
            else
                match update None with
                | None -> x:> _
                | Some value ->
                    let n = HashMapNoCollisionLeaf.New(hash, key, value)
                    HashMapInner.Join(hash, n, x.Hash, x)
           
        override x.AlterV(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, update: voption<'V> -> voption<'V>) =
            if x.Hash = hash then
                if cmp.Equals(key, x.Key) then
                    match update (ValueSome x.Value) with
                    | ValueSome value -> 
                        HashMapNoCollisionLeaf.New(x.Hash, x.Key, value)
                    | ValueNone -> 
                        HashMapEmpty.Instance
                else
                    match update ValueNone with
                    | ValueNone -> x:> _
                    | ValueSome value ->
                        HashMapCollisionLeaf.New(x.Hash, x.Key, x.Value, HashMapLinked(key, value, null))
            else
                match update ValueNone with
                | ValueNone -> x:> _
                | ValueSome value ->
                    let n = HashMapNoCollisionLeaf.New(hash, key, value)
                    HashMapInner.Join(hash, n, x.Hash, x)
           
        override x.Change(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, changed : byref<bool>, update: voption<'V> -> voption<'V>) =
            if x.Hash = hash then
                if cmp.Equals(key, x.Key) then
                    match update (ValueSome x.Value) with
                    | ValueSome value -> 
                        HashMapNoCollisionLeaf.New(x.Hash, x.Key, value)
                    | ValueNone -> 
                        changed <- true
                        HashMapEmpty.Instance
                else
                    match update ValueNone with
                    | ValueNone -> x:> _
                    | ValueSome value ->
                        changed <- true
                        HashMapCollisionLeaf.New(x.Hash, x.Key, x.Value, HashMapLinked(key, value, null))
            else
                match update ValueNone with
                | ValueNone -> x:> _
                | ValueSome value ->
                    changed <- true
                    let n = HashMapNoCollisionLeaf.New(hash, key, value)
                    HashMapInner.Join(hash, n, x.Hash, x)
           
        override x.Map(mapping: OptimizedClosures.FSharpFunc<'K, 'V, 'T>) =
            let t = mapping.Invoke(x.Key, x.Value)
            HashMapNoCollisionLeaf.New(x.Hash, x.Key, t)
               
        override x.Choose(mapping: OptimizedClosures.FSharpFunc<'K, 'V, option<'T>>) =
            match mapping.Invoke(x.Key, x.Value) with
            | Some v ->
                HashMapNoCollisionLeaf.New(x.Hash, x.Key, v)
            | None ->
                HashMapEmpty.Instance
                
        override x.ChooseV(mapping: OptimizedClosures.FSharpFunc<'K, 'V, ValueOption<'T>>) =
            match mapping.Invoke(x.Key, x.Value) with
            | ValueSome v ->
                HashMapNoCollisionLeaf.New(x.Hash, x.Key, v)
            | ValueNone ->
                HashMapEmpty.Instance
 
        override x.ChooseV2(mapping: OptimizedClosures.FSharpFunc<'K, 'V, struct (ValueOption<'T1> * ValueOption<'T2>)>) =
            let struct (l,r) = mapping.Invoke(x.Key, x.Value)         
            let l = match l with | ValueSome v -> HashMapNoCollisionLeaf.New(x.Hash, x.Key, v) :> HashMapNode<_,_> | _ -> HashMapEmpty.Instance
            let r = match r with | ValueSome v -> HashMapNoCollisionLeaf.New(x.Hash, x.Key, v) :> HashMapNode<_,_> | _ -> HashMapEmpty.Instance
            struct (l, r)

        override x.ChooseSV2(mapping: OptimizedClosures.FSharpFunc<'K, 'V, struct (bool * ValueOption<'T2>)>) =
            let struct (l,r) = mapping.Invoke(x.Key, x.Value)         
            let l = if l then HashSetNoCollisionLeaf.New(x.Hash, x.Key) else HashSetEmpty.Instance
            let r = match r with | ValueSome v -> HashMapNoCollisionLeaf.New(x.Hash, x.Key, v) :> HashMapNode<_,_> | _ -> HashMapEmpty.Instance
            struct (l, r)

        override x.Filter(predicate: OptimizedClosures.FSharpFunc<'K, 'V, bool>) =
            if predicate.Invoke(x.Key, x.Value) then
                HashMapNoCollisionLeaf.New(x.Hash, x.Key, x.Value)
            else
                HashMapEmpty.Instance
 
        override x.Iter(action: OptimizedClosures.FSharpFunc<'K, 'V, unit>) =
            action.Invoke(x.Key, x.Value)
            
        override x.Fold(acc: OptimizedClosures.FSharpFunc<'S, 'K, 'V, 'S>, seed : 'S) =
            acc.Invoke(seed, x.Key, x.Value)

        override x.Exists(predicate: OptimizedClosures.FSharpFunc<'K, 'V, bool>) =
            predicate.Invoke(x.Key, x.Value)
                
        override x.Forall(predicate: OptimizedClosures.FSharpFunc<'K, 'V, bool>) =
            predicate.Invoke(x.Key, x.Value)

        override x.CopyTo(dst : ('K * 'V) array, index : int) =
            dst.[index] <- (x.Key, x.Value)
            index + 1
            
        override x.CopyToV(dst : (struct ('K * 'V)) array, index : int) =
            dst.[index] <- struct (x.Key, x.Value)
            index + 1
            
        override x.CopyToKeys(dst : 'K[], index : int) =
            dst.[index] <- x.Key
            index + 1
            
        override x.CopyToValues(dst : 'V[], index : int) =
            dst.[index] <- x.Value
            index + 1

        override x.ToList(acc : list<'K * 'V>) =
            (x.Key, x.Value) :: acc
            
        override x.ToListV(acc : list<struct('K * 'V)>) =
            struct(x.Key, x.Value) :: acc
            
        override x.ToKeyList(acc : list<'K>) =
            x.Key :: acc
            
        override x.ToValueList(acc : list<'V>) =
            x.Value :: acc

        static member New(h : uint32, k : 'K, v : 'V) : HashMapNode<'K, 'V> =
            new HashMapNoCollisionLeaf<_,_>(Hash = h, Key = k, Value = v) :> HashMapNode<'K, 'V>

    [<Sealed>]
    type HashMapInner<'K, 'V>() =
        inherit HashMapNode<'K, 'V>()
        [<DefaultValue>]
        val mutable public Prefix: uint32
        [<DefaultValue>]
        val mutable public Mask: Mask
        [<DefaultValue>]
        val mutable public Left: HashMapNode<'K, 'V>
        [<DefaultValue>]
        val mutable public Right: HashMapNode<'K, 'V>
        [<DefaultValue>]
        val mutable public _Count: int
          
        override x.ComputeHash() =
            combineHash (int x.Mask) (combineHash (x.Left.ComputeHash()) (x.Right.ComputeHash()))

        override x.GetKeys() =
            HashSetInner.New(x.Prefix, x.Mask, x.Left.GetKeys(), x.Right.GetKeys())

        override x.Count = x._Count

        static member Join (p0 : uint32, t0 : HashMapNode<'K, 'V>, p1 : uint32, t1 : HashMapNode<'K, 'V>) : HashMapNode<'K,'V>=
            if t0.IsEmpty then t1
            elif t1.IsEmpty then t0
            else 
                let m = getMask p0 p1
                if zeroBit p0 m = 0u then HashMapInner.New(getPrefix p0 m, m, t0, t1)
                else HashMapInner.New(getPrefix p0 m, m, t1, t0)

        static member Create(p: uint32, m: Mask, l: HashMapNode<'K, 'V>, r: HashMapNode<'K, 'V>) : HashMapNode<'K, 'V> =
            if r.IsEmpty then l
            elif l.IsEmpty then r
            else HashMapInner.New(p, m, l, r)

        override x.IsEmpty = false
        
        override x.Accept(v: HashMapVisitor<_,_,_>) =
            v.VisitNode x

        override x.TryFind(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K) =
            let m = zeroBit hash x.Mask
            if m = 0u then x.Left.TryFind(cmp, hash, key)
            else x.Right.TryFind(cmp, hash, key)

        override x.TryFindV(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K) =
            let m = zeroBit hash x.Mask
            if m = 0u then x.Left.TryFindV(cmp, hash, key)
            else x.Right.TryFindV(cmp, hash, key)
            
        override x.ContainsKey(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K) =
            let m = zeroBit hash x.Mask
            if m = 0u then x.Left.ContainsKey(cmp, hash, key)
            else x.Right.ContainsKey(cmp, hash, key)

        override x.Remove(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K) =
            let m = matchPrefixAndGetBit hash x.Prefix x.Mask
            if m = 0u then 
                let l = x.Left.Remove(cmp, hash, key)
                if l == x.Left then x :> _
                else HashMapInner.Create(x.Prefix, x.Mask, l, x.Right)
            elif m = 1u then
                let r = x.Right.Remove(cmp, hash, key)
                if r == x.Right then x :> _
                else HashMapInner.Create(x.Prefix, x.Mask, x.Left, r)
            else
                x:> _

        override x.TryRemove(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K) =
            let m = matchPrefixAndGetBit hash x.Prefix x.Mask
            if m = 0u then 
                match x.Left.TryRemove(cmp, hash, key) with
                | ValueSome (struct(value, ll)) ->
                    ValueSome (struct(value, HashMapInner.Create(x.Prefix, x.Mask, ll, x.Right)))
                | ValueNone ->
                    ValueNone
            elif m = 1u then
                match x.Right.TryRemove(cmp, hash, key) with
                | ValueSome (struct(value, rr)) ->
                    ValueSome (struct(value, HashMapInner.Create(x.Prefix, x.Mask, x.Left, rr)))
                | ValueNone ->
                    ValueNone
            else
                ValueNone

        override x.AddInPlaceUnsafe(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, value: 'V) =
            let m = matchPrefixAndGetBit hash x.Prefix x.Mask
            if m = 0u then 
                x.Left <- x.Left.AddInPlaceUnsafe(cmp, hash, key, value)
                x._Count <- x.Left.Count + x.Right.Count
                x:> HashMapNode<_,_>
            elif m = 1u then 
                x.Right <- x.Right.AddInPlaceUnsafe(cmp, hash, key, value)
                x._Count <- x.Left.Count + x.Right.Count
                x:> HashMapNode<_,_>
            else
                let n = HashMapNoCollisionLeaf.New(hash, key, value)
                HashMapInner.Join(x.Prefix, x, hash, n)

        override x.Add(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, value: 'V) =
            let m = matchPrefixAndGetBit hash x.Prefix x.Mask
            if m = 0u then 
                HashMapInner.New(x.Prefix, x.Mask, x.Left.Add(cmp, hash, key, value), x.Right)
            elif m = 1u then 
                HashMapInner.New(x.Prefix, x.Mask, x.Left, x.Right.Add(cmp, hash, key, value))
            else
                let n = HashMapNoCollisionLeaf.New(hash, key, value)
                HashMapInner.Join(x.Prefix, x, hash, n)

        override x.Alter(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, update: option<'V> -> option<'V>) =
            let m = matchPrefixAndGetBit hash x.Prefix x.Mask
            if m = 0u then 
                let ll = x.Left.Alter(cmp, hash, key, update)
                if ll == x.Left then x:> _
                else HashMapInner.Create(x.Prefix, x.Mask, ll, x.Right)
            elif m = 1u then
                let rr = x.Right.Alter(cmp, hash, key, update)
                if rr == x.Right then x:> _
                else HashMapInner.Create(x.Prefix, x.Mask, x.Left, rr)
            else
                match update None with
                | None -> x:> _
                | Some value ->
                    let n = HashMapNoCollisionLeaf.New(hash, key, value)
                    HashMapInner.Join(x.Prefix, x, hash, n)
                    
        override x.AlterV(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, update: voption<'V> -> voption<'V>) =
            let m = matchPrefixAndGetBit hash x.Prefix x.Mask
            if m = 0u then 
                let ll = x.Left.AlterV(cmp, hash, key, update)
                if ll == x.Left then x:> _
                else HashMapInner.Create(x.Prefix, x.Mask, ll, x.Right)
            elif m = 1u then
                let rr = x.Right.AlterV(cmp, hash, key, update)
                if rr == x.Right then x:> _
                else HashMapInner.Create(x.Prefix, x.Mask, x.Left, rr)
            else
                match update ValueNone with
                | ValueNone -> x:> _
                | ValueSome value ->
                    let n = HashMapNoCollisionLeaf.New(hash, key, value)
                    HashMapInner.Join(x.Prefix, x, hash, n)
                                
        override x.Change(cmp: IEqualityComparer<'K>, hash: uint32, key: 'K, changed : byref<bool>, update: voption<'V> -> voption<'V>) =
            let m = matchPrefixAndGetBit hash x.Prefix x.Mask
            if m = 0u then 
                let ll = x.Left.Change(cmp, hash, key, &changed, update)
                if ll == x.Left then x:> _
                else HashMapInner.Create(x.Prefix, x.Mask, ll, x.Right)
            elif m = 1u then
                let rr = x.Right.Change(cmp, hash, key, &changed, update)
                if rr == x.Right then x:> _
                else HashMapInner.Create(x.Prefix, x.Mask, x.Left, rr)
            else
                match update ValueNone with
                | ValueNone -> x:> _
                | ValueSome value ->
                    changed <- true
                    let n = HashMapNoCollisionLeaf.New(hash, key, value)
                    HashMapInner.Join(x.Prefix, x, hash, n)
                    
        override x.Map(mapping: OptimizedClosures.FSharpFunc<'K, 'V, 'T>) =
            HashMapInner.New(x.Prefix, x.Mask, x.Left.Map(mapping), x.Right.Map(mapping))
  
        override x.Choose(mapping: OptimizedClosures.FSharpFunc<'K, 'V, option<'T>>) =
            HashMapInner.Create(x.Prefix, x.Mask, x.Left.Choose(mapping), x.Right.Choose(mapping))
            
        override x.ChooseV(mapping: OptimizedClosures.FSharpFunc<'K, 'V, ValueOption<'T>>) =
            HashMapInner.Create(x.Prefix, x.Mask, x.Left.ChooseV(mapping), x.Right.ChooseV(mapping))
      
        override x.ChooseV2(mapping: OptimizedClosures.FSharpFunc<'K, 'V, struct(ValueOption<'T1> * ValueOption<'T2>)>) =
            let struct (la, lb) = x.Left.ChooseV2(mapping)
            let struct (ra, rb) = x.Right.ChooseV2(mapping)

            struct (
                HashMapInner.Create(x.Prefix, x.Mask, la, ra),
                HashMapInner.Create(x.Prefix, x.Mask, lb, rb)
            )

        override x.ChooseSV2(mapping: OptimizedClosures.FSharpFunc<'K, 'V, struct(bool * ValueOption<'T2>)>) =
            let struct (la, lb) = x.Left.ChooseSV2(mapping)
            let struct (ra, rb) = x.Right.ChooseSV2(mapping)

            struct (
                HashSetInner.Create(x.Prefix, x.Mask, la, ra),
                HashMapInner.Create(x.Prefix, x.Mask, lb, rb)
            )
      
        override x.Filter(predicate: OptimizedClosures.FSharpFunc<'K, 'V, bool>) =
            HashMapInner.Create(x.Prefix, x.Mask, x.Left.Filter(predicate), x.Right.Filter(predicate))
            
        override x.Iter(action: OptimizedClosures.FSharpFunc<'K, 'V, unit>) =
            x.Left.Iter(action)
            x.Right.Iter(action)

        override x.Fold(acc: OptimizedClosures.FSharpFunc<'S, 'K, 'V, 'S>, seed : 'S) =
            let s = x.Left.Fold(acc, seed)
            x.Right.Fold(acc, s)
            

        override x.Exists(predicate: OptimizedClosures.FSharpFunc<'K, 'V, bool>) =
            x.Left.Exists predicate || x.Right.Exists predicate
                
        override x.Forall(predicate: OptimizedClosures.FSharpFunc<'K, 'V, bool>) =
            x.Left.Forall predicate && x.Right.Forall predicate

        override x.CopyTo(dst : ('K * 'V) array, index : int) =
            let i = x.Left.CopyTo(dst, index)
            x.Right.CopyTo(dst, i)
            
        override x.CopyToV(dst : (struct ('K * 'V)) array, index : int) =
            let i = x.Left.CopyToV(dst, index)
            x.Right.CopyToV(dst, i)
            
        override x.CopyToKeys(dst : 'K[], index : int) =
            let i = x.Left.CopyToKeys(dst, index)
            x.Right.CopyToKeys(dst, i)
            
        override x.CopyToValues(dst : 'V[], index : int) =
            let i = x.Left.CopyToValues(dst, index)
            x.Right.CopyToValues(dst, i)

        override x.ToList(acc : list<'K * 'V>) =
            let a = x.Right.ToList acc
            x.Left.ToList a
            
        override x.ToListV(acc : list<struct('K * 'V)>) =
            let a = x.Right.ToListV acc
            x.Left.ToListV a
            
        override x.ToKeyList(acc : list<'K>) =
            let a = x.Right.ToKeyList acc
            x.Left.ToKeyList a

        override x.ToValueList(acc : list<'V>) =
            let a = x.Right.ToValueList acc
            x.Left.ToValueList a

        static member New(p: uint32, m: Mask, l: HashMapNode<'K, 'V>, r: HashMapNode<'K, 'V>) : HashMapNode<'K, 'V> = 
            assert(getPrefix p m = p)
            assert(not l.IsEmpty)
            assert(not r.IsEmpty)
            new HashMapInner<_,_>(Prefix = p, Mask = m, Left = l, Right = r, _Count = l.Count + r.Count) :> _

    
    // ========================================================================================================================
    // crazy OO Visitors
    // ========================================================================================================================
    [<AbstractClass>]
    type HashSetVisitor<'T, 'R>() =
        abstract member VisitEmpty : HashSetEmpty<'T> -> 'R
        abstract member VisitNoCollision : HashSetNoCollisionLeaf<'T> -> 'R
        abstract member VisitLeaf : HashSetCollisionLeaf<'T> -> 'R
        abstract member VisitNode : HashSetInner<'T> -> 'R

    [<AbstractClass>]
    type HashMapVisitor<'K, 'V, 'R>() =
        abstract member VisitNode: HashMapInner<'K, 'V> -> 'R
        abstract member VisitLeaf: HashMapCollisionLeaf<'K, 'V> -> 'R
        abstract member VisitNoCollision: HashMapNoCollisionLeaf<'K, 'V> -> 'R
        abstract member VisitEmpty: HashMapEmpty<'K, 'V> -> 'R
        
    [<AbstractClass>]
    type HashMapVisitor2<'K, 'V1, 'V2, 'R>() =
        abstract member VisitNN     : HashMapInner<'K, 'V1> * HashMapInner<'K, 'V2> -> 'R

        abstract member VisitNL     : HashMapInner<'K, 'V1> * HashMapLeaf<'K, 'V2> -> 'R
        abstract member VisitLN     : HashMapLeaf<'K, 'V1> * HashMapInner<'K, 'V2> -> 'R
        abstract member VisitLL     : HashMapLeaf<'K, 'V1> * HashMapLeaf<'K, 'V2> -> 'R

        abstract member VisitAE     : HashMapNode<'K, 'V1> * HashMapEmpty<'K, 'V2> -> 'R
        abstract member VisitEA     : HashMapEmpty<'K, 'V1> * HashMapNode<'K, 'V2> -> 'R
        abstract member VisitEE     : HashMapEmpty<'K, 'V1> * HashMapEmpty<'K, 'V2> -> 'R
  
    [<AbstractClass>]
    type HashSetMapVisitor<'K, 'V, 'R>() =
        abstract member VisitNN     : HashSetInner<'K> * HashMapInner<'K, 'V> -> 'R

        abstract member VisitNL     : HashSetInner<'K> * HashMapLeaf<'K, 'V> -> 'R
        abstract member VisitLN     : HashSetLeaf<'K> * HashMapInner<'K, 'V> -> 'R
        abstract member VisitLL     : HashSetLeaf<'K> * HashMapLeaf<'K, 'V> -> 'R

        abstract member VisitAE     : HashSetNode<'K> * HashMapEmpty<'K, 'V> -> 'R
        abstract member VisitEA     : HashSetEmpty<'K> * HashMapNode<'K, 'V> -> 'R
        abstract member VisitEE     : HashSetEmpty<'K> * HashMapEmpty<'K, 'V> -> 'R
        
    [<AbstractClass>]
    type HashSetVisitor2<'T, 'R>() =
        abstract member VisitNN     : HashSetInner<'T> * HashSetInner<'T> -> 'R

        abstract member VisitNL     : HashSetInner<'T> * HashSetLeaf<'T> -> 'R
        abstract member VisitLN     : HashSetLeaf<'T> * HashSetInner<'T> -> 'R
        abstract member VisitLL     : HashSetLeaf<'T> * HashSetLeaf<'T> -> 'R

        abstract member VisitAE     : HashSetNode<'T> * HashSetEmpty<'T> -> 'R
        abstract member VisitEA     : HashSetEmpty<'T> * HashSetNode<'T> -> 'R
        abstract member VisitEE     : HashSetEmpty<'T> * HashSetEmpty<'T> -> 'R

    type HashMapVisit2Visitor<'K, 'V1, 'V2, 'R>(real : HashMapVisitor2<'K, 'V1, 'V2, 'R>, node : HashMapNode<'K, 'V2>) =
        inherit HashMapVisitor<'K,'V1,'R>()

        override x.VisitLeaf l = 
            node.Accept {
                new HashMapVisitor<'K, 'V2, 'R>() with
                    member x.VisitLeaf r = real.VisitLL(l, r)
                    member x.VisitNode r = real.VisitLN(l, r)
                    member x.VisitNoCollision r = real.VisitLL(l, r)
                    member x.VisitEmpty r = real.VisitAE(l, r)
            }
            
        override x.VisitNode l = 
            node.Accept {
                new HashMapVisitor<'K, 'V2, 'R>() with
                    member x.VisitLeaf r = real.VisitNL(l, r)
                    member x.VisitNode r = real.VisitNN(l, r)
                    member x.VisitNoCollision r = real.VisitNL(l, r)
                    member x.VisitEmpty r = real.VisitAE(l, r)
            }
            
        override x.VisitNoCollision l = 
            node.Accept {
                new HashMapVisitor<'K, 'V2, 'R>() with
                    member x.VisitLeaf r = real.VisitLL(l, r)
                    member x.VisitNode r = real.VisitLN(l, r)
                    member x.VisitNoCollision r = real.VisitLL(l, r)
                    member x.VisitEmpty r = real.VisitAE(l, r)
            }
            
        override x.VisitEmpty l = 
            node.Accept {
                new HashMapVisitor<'K, 'V2, 'R>() with
                    member x.VisitLeaf r = real.VisitEA(l, r)
                    member x.VisitNode r = real.VisitEA(l, r)
                    member x.VisitNoCollision r = real.VisitEA(l, r)
                    member x.VisitEmpty r = real.VisitEE(l, r)
            }
            
    type HashSetVisit2Visitor<'T, 'R>(real : HashSetVisitor2<'T, 'R>, node : HashSetNode<'T>) =
        inherit HashSetVisitor<'T,'R>()

        override x.VisitLeaf l = 
            node.Accept {
                new HashSetVisitor<'T, 'R>() with
                    member x.VisitLeaf r = real.VisitLL(l, r)
                    member x.VisitNode r = real.VisitLN(l, r)
                    member x.VisitNoCollision r = real.VisitLL(l, r)
                    member x.VisitEmpty r = real.VisitAE(l, r)
            }
            
        override x.VisitNode l = 
            node.Accept {
                new HashSetVisitor<'T, 'R>() with
                    member x.VisitLeaf r = real.VisitNL(l, r)
                    member x.VisitNode r = real.VisitNN(l, r)
                    member x.VisitNoCollision r = real.VisitNL(l, r)
                    member x.VisitEmpty r = real.VisitAE(l, r)
            }
            
        override x.VisitNoCollision l = 
            node.Accept {
                new HashSetVisitor<'T, 'R>() with
                    member x.VisitLeaf r = real.VisitLL(l, r)
                    member x.VisitNode r = real.VisitLN(l, r)
                    member x.VisitNoCollision r = real.VisitLL(l, r)
                    member x.VisitEmpty r = real.VisitAE(l, r)
            }
            
        override x.VisitEmpty l = 
            node.Accept {
                new HashSetVisitor<'T, 'R>() with
                    member x.VisitLeaf r = real.VisitEA(l, r)
                    member x.VisitNode r = real.VisitEA(l, r)
                    member x.VisitNoCollision r = real.VisitEA(l, r)
                    member x.VisitEmpty r = real.VisitEE(l, r)
            }
            
    type HashMapSetVisit2Visitor<'K, 'V, 'R>(real : HashSetMapVisitor<'K, 'V, 'R>, node : HashMapNode<'K, 'V>) =
        inherit HashSetVisitor<'K,'R>()

        override x.VisitLeaf l = 
            node.Accept {
                new HashMapVisitor<'K, 'V, 'R>() with
                    member x.VisitLeaf r = real.VisitLL(l, r)
                    member x.VisitNode r = real.VisitLN(l, r)
                    member x.VisitNoCollision r = real.VisitLL(l, r)
                    member x.VisitEmpty r = real.VisitAE(l, r)
            }
            
        override x.VisitNode l = 
            node.Accept {
                new HashMapVisitor<'K, 'V, 'R>() with
                    member x.VisitLeaf r = real.VisitNL(l, r)
                    member x.VisitNode r = real.VisitNN(l, r)
                    member x.VisitNoCollision r = real.VisitNL(l, r)
                    member x.VisitEmpty r = real.VisitAE(l, r)
            }
            
        override x.VisitNoCollision l = 
            node.Accept {
                new HashMapVisitor<'K, 'V, 'R>() with
                    member x.VisitLeaf r = real.VisitLL(l, r)
                    member x.VisitNode r = real.VisitLN(l, r)
                    member x.VisitNoCollision r = real.VisitLL(l, r)
                    member x.VisitEmpty r = real.VisitAE(l, r)
            }
            
        override x.VisitEmpty l = 
            node.Accept {
                new HashMapVisitor<'K, 'V, 'R>() with
                    member x.VisitLeaf r = real.VisitEA(l, r)
                    member x.VisitNode r = real.VisitEA(l, r)
                    member x.VisitNoCollision r = real.VisitEA(l, r)
                    member x.VisitEmpty r = real.VisitEE(l, r)
            }

    module HashMapNode = 

        let rec copyTo (array : ('K * 'V)[]) (index : int) (n : HashMapNode<'K, 'V>) =
            match n with
            | :? HashMapEmpty<'K, 'V> ->
                index
            | :? HashMapNoCollisionLeaf<'K, 'V> as l ->
                array.[index] <- (l.Key, l.Value)
                index + 1
            | :? HashMapCollisionLeaf<'K, 'V> as l ->
                array.[index] <- (l.Key, l.Value)
                HashMapLinked.copyTo (index + 1) array l.Next
            | :? HashMapInner<'K, 'V> as n ->
                let i = copyTo array index n.Left
                copyTo array i n.Right
            | _ ->
                index

        let rec copyToV (array : struct('K * 'V)[]) (index : int) (n : HashMapNode<'K, 'V>) =
            match n with
            | :? HashMapEmpty<'K, 'V> ->
                index
            | :? HashMapNoCollisionLeaf<'K, 'V> as l ->
                array.[index] <- struct(l.Key, l.Value)
                index + 1
            | :? HashMapCollisionLeaf<'K, 'V> as l ->
                array.[index] <- struct(l.Key, l.Value)
                HashMapLinked.copyToV (index + 1) array l.Next
            | :? HashMapInner<'K, 'V> as n ->
                let i = copyToV array index n.Left
                copyToV array i n.Right
            | _ ->
                index

        let rec toList (acc : list<'K * 'V>) (n : HashMapNode<'K, 'V>) =
            match n with
            | :? HashMapEmpty<'K, 'V> ->
                acc
            | :? HashMapNoCollisionLeaf<'K, 'V> as l ->
                (l.Key, l.Value) :: acc
            | :? HashMapCollisionLeaf<'K, 'V> as l ->
                let rec run (acc : list<_>) (n : HashMapLinked<_,_>) =
                    if isNull n then acc
                    else (n.Key, n.Value) :: run acc n.Next
                (l.Key, l.Value) :: run acc l.Next
            | :? HashMapInner<'K, 'V> as n ->
                let r = toList acc n.Right
                toList r n.Left
            | _ ->
                acc
                
        let rec toListV (acc : list<struct('K * 'V)>) (n : HashMapNode<'K, 'V>) =
            match n with
            | :? HashMapEmpty<'K, 'V> ->
                acc
            | :? HashMapNoCollisionLeaf<'K, 'V> as l ->
                struct(l.Key, l.Value) :: acc
            | :? HashMapCollisionLeaf<'K, 'V> as l ->
                let rec run (acc : list<_>) (n : HashMapLinked<_,_>) =
                    if isNull n then acc
                    else struct(n.Key, n.Value) :: run acc n.Next
                struct(l.Key, l.Value) :: run acc l.Next
            | :? HashMapInner<'K, 'V> as n ->
                let r = toListV acc n.Right
                toListV r n.Left
            | _ ->
                acc

        let visit2 (v : HashMapVisitor2<'K, 'V1, 'V2, 'R>) (l : HashMapNode<'K, 'V1>) (r : HashMapNode<'K, 'V2>) =
            l.Accept (HashMapVisit2Visitor(v, r))

        let equals (cmp : IEqualityComparer<'K>) (l : HashMapNode<'K,'V>) (r : HashMapNode<'K,'V>) =
            let arr = ref (Array.zeroCreate 4)

            (l, r) ||> visit2 {
                new HashMapVisitor2<'K, 'V, 'V, bool>() with
                    member x.VisitEA(_, _) = false
                    member x.VisitAE(_, _) = false
                    member x.VisitLN(_, _) = false
                    member x.VisitNL(_, _) = false

                    member x.VisitEE(_, _) = 
                        true

                    member x.VisitLL(l, r) = 
                        if l == r then
                            true
                        elif l.LHash = r.LHash then
                            let mutable rr = r :> HashMapNode<_,_>
                            let hash = l.LHash
                            ensureLength arr l.Count
                            let len = l.CopyToV(!arr, 0)

                            let mutable i = 0
                            let mutable eq = true
                            while eq && i < len do
                                let struct(k, lv) = arr.Value.[i]
                                match rr.TryRemove(cmp, hash, k) with
                                | ValueSome (rv, rest) ->
                                    eq <- DefaultEquality.equals lv rv
                                    rr <- rest
                                | ValueNone ->
                                    eq <- false
                                i <- i + 1

                            if eq then rr.IsEmpty
                            else false
                        else
                            false

                    member x.VisitNN(l, r) = 
                        (l == r) || (
                            (l.Mask = r.Mask) &&
                            (l.Prefix = r.Prefix) &&
                            (visit2 x l.Left r.Left) &&
                            (visit2 x l.Right r.Right)
                        )
                                    
            }
                    
        let computeDelta 
            (cmp : IEqualityComparer<'K>)
            (add : 'K -> 'V -> 'OP)
            (update : 'K -> 'V -> 'V -> ValueOption<'OP>)
            (remove : 'K -> 'V -> 'OP)
            (l : HashMapNode<'K, 'V>) 
            (r : HashMapNode<'K, 'V>)  =
            let add = OptimizedClosures.FSharpFunc<_,_,_>.Adapt(add)
            let remove = OptimizedClosures.FSharpFunc<_,_,_>.Adapt(remove)
            let update = OptimizedClosures.FSharpFunc<_,_,_,_>.Adapt(update)

        
            let arr = ref (Array.zeroCreate 4)

            (l, r) ||> HashMapNode.visit2 {
                new HashMapVisitor2<'K, 'V, 'V, HashMapNode<'K, 'OP>>() with

                    member x.VisitEE(_, _) = HashMapEmpty.Instance
                    member x.VisitEA(_, r) = r.Map(add)
                    member x.VisitAE(l, _) = l.Map(remove)

                    member x.VisitLL(l, r) = 
                        if l == r then
                            HashMapEmpty.Instance
                        else
                            if l.LHash = r.LHash then
                                let mutable r = r :> HashMapNode<_,_>
                                let mutable res = HashMapEmpty.Instance
                                let hash = l.LHash

                                ensureLength arr l.Count
                                let len = l.CopyToV(!arr, 0)
                                for i in 0 .. len - 1 do
                                    let struct (k, lv) = arr.Value.[i]
                                    match r.TryRemove(cmp, hash, k) with
                                    | ValueSome (rv, rest) ->
                                        r <- rest
                                        match update.Invoke(k, lv, rv) with
                                        | ValueSome op ->
                                            res <- res.AddInPlaceUnsafe(cmp, hash, k, op)
                                        | ValueNone ->
                                            ()
                                    | ValueNone ->
                                        res <- res.AddInPlaceUnsafe(cmp, hash, k, remove.Invoke(k, lv))

                                ensureLength arr r.Count
                                let len = r.CopyToV(!arr, 0)
                                for i in 0 .. len - 1 do
                                    let struct (k, rv) = arr.Value.[i]
                                    res <- res.AddInPlaceUnsafe(cmp, hash, k, add.Invoke(k, rv))
                        
                                res
                            else
                                let mutable res = l.Map(remove)
                                ensureLength arr r.Count
                                let len = r.CopyToV(!arr, 0)
                                for i in 0 .. len - 1 do
                                    let struct (k, rv) = arr.Value.[i]
                                    res <- res.AddInPlaceUnsafe(cmp, r.LHash, k, add.Invoke(k, rv))
                                res

                    member x.VisitLN(l, r) =
                        let b = matchPrefixAndGetBit l.LHash r.Prefix r.Mask
                        if b = 0u then
                            HashMapInner.Create(r.Prefix, r.Mask, HashMapNode.visit2 x l r.Left, r.Right.Map(add))
                        elif b = 1u then
                            HashMapInner.Create(r.Prefix, r.Mask, r.Left.Map(add), HashMapNode.visit2 x l r.Right)
                        else
                            HashMapInner.Join(l.LHash, l.Map(remove), r.Prefix, r.Map(add))

                    member x.VisitNL(l, r) =
                        let b = matchPrefixAndGetBit r.LHash l.Prefix l.Mask
                        if b = 0u then
                            HashMapInner.Create(l.Prefix, l.Mask, HashMapNode.visit2 x l.Left r, l.Right.Map(remove))
                        elif b = 1u then
                            HashMapInner.Create(l.Prefix, l.Mask, l.Left.Map(remove), HashMapNode.visit2 x l.Right r)
                        else
                            HashMapInner.Join(l.Prefix, l.Map(remove), r.LHash, r.Map(add))

                    member x.VisitNN(l, r) = 
                        if l == r then
                            HashMapEmpty.Instance
                        else
                            let cc = compareMasks l.Mask r.Mask
                            if cc = 0 then
                                if l.Prefix = r.Prefix then
                                    let l' = (l.Left, r.Left) ||> HashMapNode.visit2 x
                                    let r' = (l.Right, r.Right) ||> HashMapNode.visit2 x
                                    HashMapInner.Create(l.Prefix, l.Mask, l', r')
                                else
                                    let l1 = l.Map(remove)
                                    let r1 = r.Map(add)
                                    HashMapInner.Join(l.Prefix, l1, r.Prefix, r1)
                                    
                            elif cc > 0 then
                                let lr = matchPrefixAndGetBit l.Prefix r.Prefix r.Mask
                                if lr = 0u then
                                    HashMapInner.Create(r.Prefix, r.Mask, HashMapNode.visit2 x l r.Left, r.Right.Map(add))
                                elif lr = 1u then
                                    HashMapInner.Create(r.Prefix, r.Mask, r.Left.Map(add), HashMapNode.visit2 x l r.Right)
                                else
                                    HashMapInner.Join(l.Prefix, l.Map(remove), r.Prefix, r.Map(add))
                            else
                                let rl = matchPrefixAndGetBit r.Prefix l.Prefix l.Mask
                            
                                if rl = 0u then
                                    HashMapInner.Create(l.Prefix, l.Mask, HashMapNode.visit2 x l.Left r, l.Right.Map(remove))
                                elif rl = 1u then
                                    HashMapInner.Create(l.Prefix, l.Mask, l.Left.Map(remove), HashMapNode.visit2 x l.Right r)
                                else
                                    HashMapInner.Join(l.Prefix, l.Map(remove), r.Prefix, r.Map(add))
                                    
            }

        let choose2 
            (cmp : IEqualityComparer<'K>)
            (update : 'K -> ValueOption<'V1> -> ValueOption<'V2> -> ValueOption<'T>)
            (l : HashMapNode<'K, 'V1>) 
            (r : HashMapNode<'K, 'V2>)  =

            let update = OptimizedClosures.FSharpFunc<_,_,_,_>.Adapt(update)
            let add = OptimizedClosures.FSharpFunc<_,_,_>.Adapt(fun k r -> update.Invoke(k, ValueNone, ValueSome r))
            let remove = OptimizedClosures.FSharpFunc<_,_,_>.Adapt(fun k l -> update.Invoke(k, ValueSome l, ValueNone))
            let update = OptimizedClosures.FSharpFunc<_,_,_,_>.Adapt(fun k l r -> update.Invoke(k, ValueSome l, ValueSome r))

            let arr1 = ref (Array.zeroCreate 4)
            let arr2 = ref (Array.zeroCreate 4)

            (l, r) ||> HashMapNode.visit2 {
                new HashMapVisitor2<'K, 'V1, 'V2, HashMapNode<'K, 'T>>() with

                    member x.VisitEE(_, _) = HashMapEmpty.Instance
                    member x.VisitEA(_, r) = r.ChooseV(add)
                    member x.VisitAE(l, _) = l.ChooseV(remove)

                    member x.VisitLL(l, r) = 
                        if l.LHash = r.LHash then
                            let mutable r = r :> HashMapNode<_,_>
                            let mutable res = HashMapEmpty.Instance
                            let hash = l.LHash
                        
                            ensureLength arr1 l.Count
                            let len = l.CopyToV(!arr1, 0)
                            for i in 0 .. len - 1 do
                                let struct (k, lv) = arr1.Value.[i]
                                match r.TryRemove(cmp, hash, k) with
                                | ValueSome (rv, rest) ->
                                    r <- rest
                                    match update.Invoke(k, lv, rv) with
                                    | ValueSome op ->
                                        res <- res.AddInPlaceUnsafe(cmp, hash, k, op)
                                    | ValueNone ->
                                        ()
                                | ValueNone ->
                                    match remove.Invoke(k, lv) with
                                    | ValueSome rv ->
                                        res <- res.AddInPlaceUnsafe(cmp, hash, k, rv)
                                    | ValueNone ->
                                        ()

                            ensureLength arr2 r.Count
                            let len = r.CopyToV(!arr2, 0)
                            for i in 0 .. len - 1 do
                                let struct (k, rv) = arr2.Value.[i]
                                match add.Invoke(k, rv) with
                                | ValueSome av -> 
                                    res <- res.AddInPlaceUnsafe(cmp, hash, k, av)
                                | ValueNone ->
                                    ()
                            res
                        else
                            let mutable res = l.ChooseV(remove)
                            ensureLength arr2 r.Count
                            let len = r.CopyToV(!arr2, 0)
                            for i in 0 .. len - 1 do
                                let struct (k, rv) = arr2.Value.[i]
                                match add.Invoke(k, rv) with
                                | ValueSome av -> 
                                    res <- res.AddInPlaceUnsafe(cmp, r.LHash, k, av)
                                | ValueNone ->
                                    ()
                            res

                    member x.VisitLN(l, r) =
                        let b = matchPrefixAndGetBit l.LHash r.Prefix r.Mask
                        if b = 0u then
                            HashMapInner.Create(r.Prefix, r.Mask, HashMapNode.visit2 x l r.Left, r.Right.ChooseV(add))
                        elif b = 1u then
                            HashMapInner.Create(r.Prefix, r.Mask, r.Left.ChooseV(add), HashMapNode.visit2 x l r.Right)
                        else
                            HashMapInner.Join(l.LHash, l.ChooseV(remove), r.Prefix, r.ChooseV(add))

                    member x.VisitNL(l, r) =
                        let b = matchPrefixAndGetBit r.LHash l.Prefix l.Mask
                        if b = 0u then
                            HashMapInner.Create(l.Prefix, l.Mask, HashMapNode.visit2 x l.Left r, l.Right.ChooseV(remove))
                        elif b = 1u then
                            HashMapInner.Create(l.Prefix, l.Mask, l.Left.ChooseV(remove), HashMapNode.visit2 x l.Right r)
                        else
                            HashMapInner.Join(l.Prefix, l.ChooseV(remove), r.LHash, r.ChooseV(add))

                    member x.VisitNN(l, r) = 
                        let cc = compareMasks l.Mask r.Mask
                        if cc = 0 then
                            if l.Prefix = r.Prefix then
                                let l' = (l.Left, r.Left) ||> HashMapNode.visit2 x
                                let r' = (l.Right, r.Right) ||> HashMapNode.visit2 x
                                HashMapInner.Create(l.Prefix, l.Mask, l', r')
                            else
                                HashMapInner.Join(l.Prefix, l.ChooseV(remove), r.Prefix, r.ChooseV(add))

                        elif cc > 0 then
                            let lr = matchPrefixAndGetBit l.Prefix r.Prefix r.Mask
                            if lr = 0u then
                                HashMapInner.Create(r.Prefix, r.Mask, HashMapNode.visit2 x l r.Left, r.Right.ChooseV(add))
                            elif lr = 1u then
                                HashMapInner.Create(r.Prefix, r.Mask, r.Left.ChooseV(add), HashMapNode.visit2 x l r.Right)
                            else
                                HashMapInner.Join(l.Prefix, l.ChooseV(remove), r.Prefix, r.ChooseV(add))
                        else
                            let rl = matchPrefixAndGetBit r.Prefix l.Prefix l.Mask
                            
                            if rl = 0u then
                                HashMapInner.Create(l.Prefix, l.Mask, HashMapNode.visit2 x l.Left r, l.Right.ChooseV(remove))
                            elif rl = 1u then
                                HashMapInner.Create(l.Prefix, l.Mask, l.Left.ChooseV(remove), HashMapNode.visit2 x l.Right r)
                            else
                                HashMapInner.Join(l.Prefix, l.ChooseV(remove), r.Prefix, r.ChooseV(add))
                                    
            }

        let fold2
            (cmp : IEqualityComparer<'K>)
            (seed : 'S)
            (folder : 'S -> 'K -> voption<'V1> -> voption<'V2> -> 'S)
            (l : HashMapNode<'K, 'V1>) 
            (r : HashMapNode<'K, 'V2>)  =
            
            let folder = OptimizedClosures.FSharpFunc<_,_,_,_,_>.Adapt folder

            let add =
                OptimizedClosures.FSharpFunc<_,_,_,_>.Adapt (fun s k v ->
                    folder.Invoke(s, k, ValueNone, ValueSome v)
                )
                
            let remove =
                OptimizedClosures.FSharpFunc<_,_,_,_>.Adapt (fun s k v ->
                    folder.Invoke(s, k, ValueSome v, ValueNone)
                )
                
            let arr1 = ref (Array.zeroCreate 4)
            let arr2 = ref (Array.zeroCreate 4)
            let value = ref seed
            (l, r) ||> HashMapNode.visit2 {
                new HashMapVisitor2<'K, 'V1, 'V2, 'S>() with

                    member x.VisitEE(_, _) = 
                        !value

                    member x.VisitEA(_, r) = 
                        value := r.Fold(add, !value)
                        !value

                    member x.VisitAE(l, _) = 
                        value := l.Fold(remove, !value)
                        !value

                    member x.VisitLL(l, r) = 
                        if l.LHash = r.LHash then
                            let mutable r = r :> HashMapNode<_,_>
                            let hash = l.LHash
                        
                            ensureLength arr1 l.Count
                            let len = l.CopyToV(!arr1, 0)
                            for i in 0 .. len - 1 do
                                let struct (k, lv) = arr1.Value.[i]
                                match r.TryRemove(cmp, hash, k) with
                                | ValueSome (rv, rest) ->
                                    r <- rest
                                    value := folder.Invoke(!value, k, ValueSome lv, ValueSome rv)
                                | ValueNone ->
                                    value := remove.Invoke(!value, k, lv)

                            ensureLength arr2 r.Count
                            let len = r.CopyToV(!arr2, 0)
                            for i in 0 .. len - 1 do
                                let struct (k, rv) = arr2.Value.[i]
                                value := add.Invoke(!value, k, rv)
                            !value
                        else
                            let s = l.Fold(remove, !value)
                            value := r.Fold(add, s)
                            !value

                    member x.VisitLN(l, r) =
                        let b = matchPrefixAndGetBit l.LHash r.Prefix r.Mask
                        if b = 0u then
                            let s = HashMapNode.visit2 x l r.Left
                            value := r.Right.Fold(add, s)
                            !value
                        elif b = 1u then
                            value := r.Left.Fold(add, !value)
                            HashMapNode.visit2 x l r.Right
                        else
                            let s = l.Fold(remove, seed)
                            value := r.Fold(add, s)
                            !value

                    member x.VisitNL(l, r) =
                        let b = matchPrefixAndGetBit r.LHash l.Prefix l.Mask
                        if b = 0u then
                            let s = HashMapNode.visit2 x l.Left r
                            value := l.Right.Fold(remove, s)
                            !value
                        elif b = 1u then
                            value := l.Left.Fold(remove, !value)
                            HashMapNode.visit2 x l.Right r
                        else
                            let s = l.Fold(remove, seed)
                            value := r.Fold(add, s)
                            !value

                    member x.VisitNN(l, r) = 
                        let cc = compareMasks l.Mask r.Mask
                        if cc = 0 then
                            if l.Prefix = r.Prefix then
                                let l' = (l.Left, r.Left) ||> HashMapNode.visit2 x
                                let r' = (l.Right, r.Right) ||> HashMapNode.visit2 x
                                !value
                            else
                                let s = l.Fold(remove, !value)
                                value := r.Fold(add, s)
                                !value

                        elif cc > 0 then
                            let lr = matchPrefixAndGetBit l.Prefix r.Prefix r.Mask
                            if lr = 0u then
                                value := HashMapNode.visit2 x l r.Left
                                value := r.Right.Fold(add, !value)
                                !value
                            elif lr = 1u then
                                value := r.Left.Fold(add, !value)
                                HashMapNode.visit2 x l r.Right
                            else
                                let s = l.Fold(remove, !value)
                                value := r.Fold(add, s)
                                !value
                        else
                            let rl = matchPrefixAndGetBit r.Prefix l.Prefix l.Mask
                            
                            if rl = 0u then
                                value := HashMapNode.visit2 x l.Left r
                                value := l.Right.Fold(remove, !value)
                                !value
                            elif rl = 1u then
                                value := l.Left.Fold(remove, !value)
                                HashMapNode.visit2 x l.Right r
                            else
                                let s = l.Fold(remove, !value)
                                value := r.Fold(add, s)
                                !value
                                    
            }


        let unionWith
            (cmp : IEqualityComparer<'K>)
            (resolve : 'K -> 'V -> 'V -> ValueOption<'V>)
            (l : HashMapNode<'K, 'V>)
            (r : HashMapNode<'K, 'V>) =
            let resolve = OptimizedClosures.FSharpFunc<_,_,_,_>.Adapt(resolve)

            let arr = ref (Array.zeroCreate 4)

            (l, r) ||> HashMapNode.visit2 {
                new HashMapVisitor2<'K, 'V, 'V, HashMapNode<'K, 'V>>() with

                    member x.VisitEE(_, _) = HashMapEmpty.Instance
                    member x.VisitEA(_, r) = r
                    member x.VisitAE(l, _) = l

                    member x.VisitLL(l, r) = 
                        if l.LHash = r.LHash then
                            let mutable r = r :> HashMapNode<_,_>
                            let mutable res = HashMapEmpty.Instance
                            let hash = l.LHash
                    
                            ensureLength arr l.Count
                            let len = l.CopyToV(!arr, 0)
                            for i in 0 .. len - 1 do
                                let struct (k, lv) = arr.Value.[i]
                                match r.TryRemove(cmp, hash, k) with
                                | ValueSome (rv, rest) ->
                                    r <- rest
                                    match resolve.Invoke(k, lv, rv) with
                                    | ValueSome op -> res <- res.AddInPlaceUnsafe(cmp, hash, k, op)
                                    | _ -> ()

                                | ValueNone ->
                                    res <- res.AddInPlaceUnsafe(cmp, hash, k, lv)

                            ensureLength arr r.Count
                            let len = r.CopyToV(!arr, 0)
                            for i in 0 .. len - 1 do
                                let struct (k, rv) = arr.Value.[i]
                                res <- res.AddInPlaceUnsafe(cmp, hash, k, rv)
                    
                            res
                        else
                            HashMapInner.Join(l.LHash, l, r.LHash, r)
                         

                    member x.VisitLN(l, r) =
                        let b = matchPrefixAndGetBit l.LHash r.Prefix r.Mask
                        if b = 0u then
                            HashMapInner.Create(r.Prefix, r.Mask, HashMapNode.visit2 x l r.Left, r.Right)
                        elif b = 1u then
                            HashMapInner.Create(r.Prefix, r.Mask, r.Left, HashMapNode.visit2 x l r.Right)
                        else
                            HashMapInner.Join(l.LHash, l, r.Prefix, r)

                    member x.VisitNL(l, r) =
                        let b = matchPrefixAndGetBit r.LHash l.Prefix l.Mask
                        if b = 0u then
                            HashMapInner.Create(l.Prefix, l.Mask, HashMapNode.visit2 x l.Left r, l.Right)
                        elif b = 1u then
                            HashMapInner.Create(l.Prefix, l.Mask, l.Left, HashMapNode.visit2 x l.Right r)
                        else
                            HashMapInner.Join(l.Prefix, l, r.LHash, r)

                    member x.VisitNN(l, r) = 
                        let cc = compareMasks l.Mask r.Mask
                        if cc = 0 then
                            if l.Prefix = r.Prefix then
                                let l' = (l.Left, r.Left) ||> visit2 x
                                let r' = (l.Right, r.Right) ||> visit2 x
                                HashMapInner.Create(l.Prefix, l.Mask, l', r')
                            else
                                HashMapInner.Join(l.Prefix, l, r.Prefix, r)
                        elif cc > 0 then
                            let lr = matchPrefixAndGetBit l.Prefix r.Prefix r.Mask
                            if lr = 0u then
                                HashMapInner.Create(r.Prefix, r.Mask, HashMapNode.visit2 x l r.Left, r.Right)
                            elif lr = 1u then
                                HashMapInner.Create(r.Prefix, r.Mask, r.Left, HashMapNode.visit2 x l r.Right)
                            else
                                HashMapInner.Join(l.Prefix, l, r.Prefix, r)
                        else
                            let rl = matchPrefixAndGetBit r.Prefix l.Prefix l.Mask
                        
                            if rl = 0u then
                                HashMapInner.Create(l.Prefix, l.Mask, HashMapNode.visit2 x l.Left r, l.Right)
                            elif rl = 1u then
                                HashMapInner.Create(l.Prefix, l.Mask, l.Left, HashMapNode.visit2 x l.Right r)
                            else
                                HashMapInner.Join(l.Prefix, l, r.Prefix, r)
                                
            }

        let union
            (cmp : IEqualityComparer<'K>) 
            (l : HashMapNode<'K, 'V>) 
            (r : HashMapNode<'K, 'V>) =
            let arr = ref (Array.zeroCreate 4)
            (l, r) ||> visit2 {
                new HashMapVisitor2<'K, 'V, 'V, HashMapNode<'K, 'V>>() with

                    member x.VisitEE(_, _) = HashMapEmpty.Instance
                    member x.VisitEA(_, r) = r
                    member x.VisitAE(l, _) = l

                    member x.VisitLL(l, r) = 
                        if l == r then
                            r :> HashMapNode<_,_>
                        else
                            if l.LHash = r.LHash then
                                let mutable r = r :> HashMapNode<_,_>
                                let mutable res = HashMapEmpty.Instance
                                let hash = l.LHash
                                
                                ensureLength arr l.Count
                                let len = l.CopyToV(!arr, 0)
                                for i in 0 .. len - 1 do
                                    let struct (k, lv) = arr.Value.[i]
                                    match r.TryRemove(cmp, hash, k) with
                                    | ValueSome (rv, rest) ->
                                        r <- rest
                                        res <- res.AddInPlaceUnsafe(cmp, hash, k, rv)
                                    | ValueNone ->
                                        res <- res.AddInPlaceUnsafe(cmp, hash, k, lv)

                                ensureLength arr r.Count
                                let len = r.CopyToV(!arr, 0)
                                for i in 0 .. len - 1 do
                                    let struct (k, rv) = arr.Value.[i]
                                    res <- res.AddInPlaceUnsafe(cmp, hash, k, rv)
                
                                res
                            else
                                HashMapInner.Join(l.LHash, l, r.LHash, r)
                     

                    member x.VisitLN(l, r) =
                        let b = matchPrefixAndGetBit l.LHash r.Prefix r.Mask
                        if b = 0u then
                            HashMapInner.Create(r.Prefix, r.Mask, visit2 x l r.Left, r.Right)
                        elif b = 1u then
                            HashMapInner.Create(r.Prefix, r.Mask, r.Left, visit2 x l r.Right)
                        else
                            HashMapInner.Join(l.LHash, l, r.Prefix, r)

                    member x.VisitNL(l, r) =
                        let b = matchPrefixAndGetBit r.LHash l.Prefix l.Mask
                        if b = 0u then
                            HashMapInner.Create(l.Prefix, l.Mask, visit2 x l.Left r, l.Right)
                        elif b = 1u then
                            HashMapInner.Create(l.Prefix, l.Mask, l.Left, visit2 x l.Right r)
                        else
                            HashMapInner.Join(l.Prefix, l, r.LHash, r)

                    member x.VisitNN(l, r) = 
                        if l == r then 
                            r :> HashMapNode<_,_>
                        else
                            let cc = compareMasks l.Mask r.Mask
                            if cc = 0 then
                                if l.Prefix = r.Prefix then
                                    let l' = (l.Left, r.Left) ||> visit2 x
                                    let r' = (l.Right, r.Right) ||> visit2 x
                                    HashMapInner.Create(l.Prefix, l.Mask, l', r')
                                else
                                    HashMapInner.Join(l.Prefix, l, r.Prefix, r)
                            elif cc > 0 then
                                let lr = matchPrefixAndGetBit l.Prefix r.Prefix r.Mask
                                if lr = 0u then
                                    HashMapInner.Create(r.Prefix, r.Mask, visit2 x l r.Left, r.Right)
                                elif lr = 1u then
                                    HashMapInner.Create(r.Prefix, r.Mask, r.Left, visit2 x l r.Right)
                                else
                                    HashMapInner.Join(l.Prefix, l, r.Prefix, r)
                            else
                                let rl = matchPrefixAndGetBit r.Prefix l.Prefix l.Mask
                    
                                if rl = 0u then
                                    HashMapInner.Create(l.Prefix, l.Mask, visit2 x l.Left r, l.Right)
                                elif rl = 1u then
                                    HashMapInner.Create(l.Prefix, l.Mask, l.Left, visit2 x l.Right r)
                                else
                                    HashMapInner.Join(l.Prefix, l, r.Prefix, r)
                            
            }

        let applyDelta
            (cmp : IEqualityComparer<'K>) 
            (apply : 'K -> voption<'V> -> 'D -> struct(voption<'V> * voption<'DOut>))
            (state : HashMapNode<'K, 'V>)
            (delta : HashMapNode<'K, 'D>) =

            let arr1 = ref (Array.zeroCreate 4)
            let arr2 = ref (Array.zeroCreate 4)
            let apply = OptimizedClosures.FSharpFunc<_,_,_,_>.Adapt(apply)
            let onlyDelta = OptimizedClosures.FSharpFunc<_,_,_>.Adapt(fun k d -> apply.Invoke(k, ValueNone, d))
    
            (state, delta) ||> HashMapNode.visit2 {
                new HashMapVisitor2<'K, 'V, 'D, struct (HashMapNode<'K, 'V> * HashMapNode<'K, 'DOut>)>() with

                    member x.VisitEE(_, _) = 
                        struct (HashMapEmpty.Instance, HashMapEmpty.Instance)

                    member x.VisitEA(_, r) =    
                        r.ChooseV2 onlyDelta

                    member x.VisitAE(l, _) = 
                        struct(l, HashMapEmpty.Instance)

                    member x.VisitLL(state, delta) = 
                        if state.LHash = delta.LHash then
                            let mutable delta = delta :> HashMapNode<_,_>
                            let mutable resState = HashMapEmpty.Instance
                            let mutable resDelta = HashMapEmpty.Instance
                            let hash = state.LHash
                    
                            ensureLength arr1 state.Count
                            let len = state.CopyToV(!arr1, 0)
                            for i in 0 .. len - 1 do
                                let struct (k, value) = arr1.Value.[i]
                                match delta.TryRemove(cmp, hash, k) with
                                | ValueSome (dd, rest) ->
                                    delta <- rest
                                    let struct (s, d) = apply.Invoke(k, ValueSome value, dd)

                                    match s with
                                    | ValueSome v -> resState <- resState.AddInPlaceUnsafe(cmp, hash, k, v)
                                    | ValueNone -> ()

                                    match d with
                                    | ValueSome v -> resDelta <- resDelta.AddInPlaceUnsafe(cmp, hash, k, v)
                                    | ValueNone -> ()

                                | ValueNone ->
                                    resState <- resState.AddInPlaceUnsafe(cmp, hash, k, value)

                            ensureLength arr2 delta.Count
                            let len = delta.CopyToV(!arr2, 0)
                            for i in 0 .. len - 1 do
                                let struct (k, rv) = arr2.Value.[i]
                                let struct (s, d) = onlyDelta.Invoke(k, rv)
                                match s with
                                | ValueSome v -> resState <- resState.AddInPlaceUnsafe(cmp, hash, k, v)
                                | ValueNone -> ()
                                match d with
                                | ValueSome v -> resDelta <- resDelta.AddInPlaceUnsafe(cmp, hash, k, v)
                                | ValueNone -> ()
                    
                            struct(resState, resDelta)
                        else
                            let struct (ds, dd) = delta.ChooseV2(onlyDelta)
                            struct (
                                HashMapInner.Join(state.LHash, state, delta.LHash, ds),
                                dd
                            )

                    member x.VisitLN(state, delta) =
                        let b = matchPrefixAndGetBit state.LHash delta.Prefix delta.Mask
                        if b = 0u then
                            let struct (ls, ld) = HashMapNode.visit2 x state delta.Left
                            let struct (rs, rd) = delta.Right.ChooseV2(onlyDelta)
                            struct(
                                HashMapInner.Create(delta.Prefix, delta.Mask, ls, rs),
                                HashMapInner.Create(delta.Prefix, delta.Mask, ld, rd)
                            )
                        elif b = 1u then
                            let struct (ls, ld) = delta.Left.ChooseV2(onlyDelta)
                            let struct (rs, rd) = HashMapNode.visit2 x state delta.Right
                            struct(
                                HashMapInner.Create(delta.Prefix, delta.Mask, ls, rs),
                                HashMapInner.Create(delta.Prefix, delta.Mask, ld, rd)
                            )
                        else
                            let struct (ds, dd) = delta.ChooseV2(onlyDelta)
                            struct(
                                HashMapInner.Join(state.LHash, state, delta.Prefix, ds),
                                dd
                            )

                    member x.VisitNL(l, r) =
                        let b = matchPrefixAndGetBit r.LHash l.Prefix l.Mask
                        if b = 0u then
                            let struct (ls, ld) = HashMapNode.visit2 x l.Left r
                            struct (
                                HashMapInner.Create(l.Prefix, l.Mask, ls, l.Right),
                                ld
                            )
                        elif b = 1u then
                            let struct (rs, rd) = HashMapNode.visit2 x l.Right r
                            struct (
                                HashMapInner.Create(l.Prefix, l.Mask, l.Left, rs),
                                rd
                            )
                        else
                            let struct (rs, rd) = r.ChooseV2(onlyDelta)
                            struct (
                                HashMapInner.Join(l.Prefix, l, r.LHash, rs),
                                rd
                            )

                    member x.VisitNN(l, r) = 
                        let cc = compareMasks l.Mask r.Mask
                        if cc = 0 then
                            if l.Prefix = r.Prefix then
                                let struct (ls, ld) = (l.Left, r.Left) ||> HashMapNode.visit2 x
                                let struct (rs, rd) = (l.Right, r.Right) ||> HashMapNode.visit2 x
                                struct (
                                    HashMapInner.Create(l.Prefix, l.Mask, ls, rs),
                                    HashMapInner.Create(l.Prefix, l.Mask, ld, rd)
                                )
                            else
                                let struct (rs, rd) = r.ChooseV2 onlyDelta
                                struct (
                                    HashMapInner.Join(l.Prefix, l, r.Prefix, rs),
                                    rd
                                )
                                
                        elif cc > 0 then
                            let lr = matchPrefixAndGetBit l.Prefix r.Prefix r.Mask
                            if lr = 0u then
                                let struct (ls, ld) = HashMapNode.visit2 x l r.Left
                                let struct (rs, rd) = r.Right.ChooseV2(onlyDelta)
                                struct (
                                    HashMapInner.Create(r.Prefix, r.Mask, ls, rs),
                                    HashMapInner.Create(r.Prefix, r.Mask, ld, rd)
                                )
                            elif lr = 1u then
                                let struct (ls, ld) = r.Left.ChooseV2(onlyDelta)
                                let struct (rs, rd) = HashMapNode.visit2 x l r.Right
                                struct (
                                    HashMapInner.Create(r.Prefix, r.Mask, ls, rs),
                                    HashMapInner.Create(r.Prefix, r.Mask, ld, rd)
                                )
                            else
                                let struct (rs, rd) = r.ChooseV2 onlyDelta
                                struct (
                                    HashMapInner.Join(l.Prefix, l, r.Prefix, rs),
                                    rd
                                )
                        else
                            let rl = matchPrefixAndGetBit r.Prefix l.Prefix l.Mask
                        
                            if rl = 0u then
                                let struct (ls, ld) = HashMapNode.visit2 x l.Left r
                                struct (
                                    HashMapInner.Create(l.Prefix, l.Mask, ls, l.Right),
                                    ld
                                )
                            elif rl = 1u then
                                let struct (rs, rd) = HashMapNode.visit2 x l.Right r
                                struct (
                                    HashMapInner.Create(l.Prefix, l.Mask, l.Left, rs),
                                    rd
                                )
                            else
                                let struct (rs, rd) = r.ChooseV2 onlyDelta
                                struct (
                                    HashMapInner.Join(l.Prefix, l, r.Prefix, rs),
                                    rd
                                )
                                
            }


    module HashSetNode = 

        
        let rec copyTo (array : 'T[]) (index : int) (n : HashSetNode<'T>) =
            match n with
            | :? HashSetEmpty<'T> ->
                index
            | :? HashSetNoCollisionLeaf<'T> as l ->
                array.[index] <- l.Value
                index + 1
            | :? HashSetCollisionLeaf<'T> as l ->
                array.[index] <- l.Value
                HashSetLinked.copyTo (index + 1) array l.Next
            | :? HashSetInner<'T> as n ->
                let i = copyTo array index n.Left
                copyTo array i n.Right
            | _ ->
                index
                
        let rec toList (acc : list<'T>) (n : HashSetNode<'T>) =
            match n with
            | :? HashSetEmpty<'T> ->
                acc
            | :? HashSetNoCollisionLeaf<'T> as l ->
                l.Value :: acc
            | :? HashSetCollisionLeaf<'T> as l ->
                let rec run (acc : list<_>) (n : HashSetLinked<_>) =
                    if isNull n then acc
                    else n.Value :: run acc n.Next
                l.Value :: run acc l.Next
            | :? HashSetInner<'T> as n ->
                let r = toList acc n.Right
                toList r n.Left
            | _ ->
                acc

        let visit2 (v : HashSetVisitor2<'T, 'R>) (l : HashSetNode<'T>) (r : HashSetNode<'T>) =
            l.Accept (HashSetVisit2Visitor(v, r))

        let visitMap2 (v : HashSetMapVisitor<'K, 'V, 'R>) (l : HashSetNode<'K>) (r : HashMapNode<'K, 'V>) =
            l.Accept (HashMapSetVisit2Visitor(v, r))

        let equals (cmp : IEqualityComparer<'T>) (l : HashSetNode<'T>) (r : HashSetNode<'T>) =
            let arr = ref (Array.zeroCreate 4)

            (l, r) ||> visit2 {
                new HashSetVisitor2<'T, bool>() with
                    member x.VisitEA(_, _) = false
                    member x.VisitAE(_, _) = false
                    member x.VisitLN(_, _) = false
                    member x.VisitNL(_, _) = false

                    member x.VisitEE(_, _) = 
                        true

                    member x.VisitLL(l, r) = 
                        if l == r then
                            true
                        elif l.LHash = r.LHash then
                            let mutable r = r :> HashSetNode<_>
                            let hash = l.LHash
                            ensureLength arr l.Count
                            let len = l.CopyTo(!arr, 0)

                            let mutable i = 0
                            let mutable eq = true
                            while eq && i < len do
                                let lv = arr.Value.[i]
                                match r.TryRemove(cmp, hash, lv) with
                                | ValueSome rest ->
                                    r <- rest
                                | ValueNone ->
                                    eq <- false
                                i <- i + 1

                            if eq then r.IsEmpty
                            else false
                        else
                            false

                    member x.VisitNN(l, r) = 
                        (l == r) || (
                            (l.Mask = r.Mask) &&
                            (l.Prefix = r.Prefix) &&
                            (visit2 x l.Left r.Left) &&
                            (visit2 x l.Right r.Right)
                        )
                                    
            }
        
        let union (cmp : IEqualityComparer<'T>) (l : HashSetNode<'T>) (r : HashSetNode<'T>)  =
            let arr = ref (Array.zeroCreate 4)

            (l, r) ||> visit2 {
                new HashSetVisitor2<'T, HashSetNode<'T>>() with

                    member x.VisitEE(_, _) = HashSetEmpty.Instance
                    member x.VisitEA(_, r) = r
                    member x.VisitAE(l, _) = l

                    member x.VisitLL(l, r) = 
                        if l == r then
                            r :> HashSetNode<_>
                        else
                            if l.LHash = r.LHash then
                                let mutable r = r :> HashSetNode<_>
                                let hash = l.LHash
                                ensureLength arr l.Count
                                let len = l.CopyTo(!arr, 0)
                                for i in 0 .. len - 1 do
                                    let lv = arr.Value.[i]
                                    r <- r.Add(cmp, hash, lv)
                                r
                            else
                                HashSetInner.Join(l.LHash, l, r.LHash, r)

                    member x.VisitLN(l, r) =
                        let b = matchPrefixAndGetBit l.LHash r.Prefix r.Mask
                        if b = 0u then
                            HashSetInner.Create(r.Prefix, r.Mask, visit2 x l r.Left, r.Right)
                        elif b = 1u then
                            HashSetInner.Create(r.Prefix, r.Mask, r.Left, visit2 x l r.Right)
                        else
                            HashSetInner.Join(l.LHash, l, r.Prefix, r)

                    member x.VisitNL(l, r) =
                        let b = matchPrefixAndGetBit r.LHash l.Prefix l.Mask
                        if b = 0u then
                            HashSetInner.Create(l.Prefix, l.Mask, visit2 x l.Left r, l.Right)
                        elif b = 1u then
                            HashSetInner.Create(l.Prefix, l.Mask, l.Left, visit2 x l.Right r)
                        else
                            HashSetInner.Join(l.Prefix, l, r.LHash, r)

                    member x.VisitNN(l, r) = 
                        if l == r then
                            r :> HashSetNode<_>
                        else
                            let cc = compareMasks l.Mask r.Mask
                            if cc = 0 then
                                if l.Prefix = r.Prefix then
                                    let l' = (l.Left, r.Left) ||> visit2 x
                                    let r' = (l.Right, r.Right) ||> visit2 x
                                    HashSetInner.Create(l.Prefix, l.Mask, l', r')
                                else
                                    HashSetInner.Join(l.Prefix, l, r.Prefix, r)
                            elif cc > 0 then
                                let lr = matchPrefixAndGetBit l.Prefix r.Prefix r.Mask
                                if lr = 0u then
                                    HashSetInner.Create(r.Prefix, r.Mask, visit2 x l r.Left, r.Right)
                                elif lr = 1u then
                                    HashSetInner.Create(r.Prefix, r.Mask, r.Left, visit2 x l r.Right)
                                else
                                    HashSetInner.Join(l.Prefix, l, r.Prefix, r)
                            else
                                let rl = matchPrefixAndGetBit r.Prefix l.Prefix l.Mask
                            
                                if rl = 0u then
                                    HashSetInner.Create(l.Prefix, l.Mask, visit2 x l.Left r, l.Right)
                                elif rl = 1u then
                                    HashSetInner.Create(l.Prefix, l.Mask, l.Left, visit2 x l.Right r)
                                else
                                    HashSetInner.Join(l.Prefix, l, r.Prefix, r)
                                    
            }
            
        let intersect (cmp : IEqualityComparer<'T>) (l : HashSetNode<'T>) (r : HashSetNode<'T>)  =
            let arr = ref (Array.zeroCreate 4)

            (l, r) ||> visit2 {
                new HashSetVisitor2<'T, HashSetNode<'T>>() with

                    member x.VisitEE(_, _) = HashSetEmpty.Instance
                    member x.VisitEA(_, _) = HashSetEmpty.Instance
                    member x.VisitAE(_, _) = HashSetEmpty.Instance

                    member x.VisitLL(l, r) = 
                        if l == r then
                            r :> HashSetNode<_>
                        else
                            if l.LHash = r.LHash then
                                let mutable res = HashSetEmpty.Instance
                                let mutable r = r :> HashSetNode<_>
                                let hash = l.LHash
                                ensureLength arr l.Count
                                let len = l.CopyTo(!arr, 0)
                                for i in 0 .. len - 1 do
                                    let lv = arr.Value.[i]
                                    match r.TryRemove(cmp, hash, lv) with
                                    | ValueSome rest ->
                                        r <- rest
                                        res <- res.AddInPlaceUnsafe(cmp, hash, lv)
                                    | ValueNone ->
                                        ()
                                res
                            else
                                HashSetEmpty.Instance

                    member x.VisitLN(l, r) =
                        let b = matchPrefixAndGetBit l.LHash r.Prefix r.Mask
                        if b = 0u then
                            visit2 x l r.Left
                        elif b = 1u then
                            visit2 x l r.Right
                        else
                            HashSetEmpty.Instance

                    member x.VisitNL(l, r) =
                        let b = matchPrefixAndGetBit r.LHash l.Prefix l.Mask
                        if b = 0u then
                            visit2 x l.Left r
                        elif b = 1u then
                            visit2 x l.Right r
                        else
                            HashSetEmpty.Instance

                    member x.VisitNN(l, r) = 
                        if l == r then
                            r :> HashSetNode<_>
                        else
                            let cc = compareMasks l.Mask r.Mask
                            if cc = 0 then
                                if l.Prefix = r.Prefix then
                                    let l' = (l.Left, r.Left) ||> visit2 x
                                    let r' = (l.Right, r.Right) ||> visit2 x
                                    HashSetInner.Create(l.Prefix, l.Mask, l', r')
                                else
                                    HashSetEmpty.Instance
                            elif cc > 0 then
                                let lr = matchPrefixAndGetBit l.Prefix r.Prefix r.Mask
                                if lr = 0u then
                                    visit2 x l r.Left
                                elif lr = 1u then
                                    visit2 x l r.Right
                                else
                                    HashSetEmpty.Instance
                            else
                                let rl = matchPrefixAndGetBit r.Prefix l.Prefix l.Mask
                                if rl = 0u then
                                    visit2 x l.Left r
                                elif rl = 1u then
                                    visit2 x l.Right r
                                else
                                    HashSetEmpty.Instance
                                    
            }
            
        let difference  (cmp : IEqualityComparer<'T>) (l : HashSetNode<'T>) (r : HashSetNode<'T>)  =
            let arr = ref (Array.zeroCreate 4)

            (l, r) ||> visit2 {
                new HashSetVisitor2<'T, HashSetNode<'T>>() with

                    member x.VisitEE(_, _) = HashSetEmpty.Instance
                    member x.VisitEA(_, _) = HashSetEmpty.Instance
                    member x.VisitAE(l, _) = l

                    member x.VisitLL(l, r) = 
                        if l == r then
                            HashSetEmpty.Instance
                        else
                            if l.LHash = r.LHash then
                                let mutable l = l :> HashSetNode<_>
                                let hash = r.LHash
                                ensureLength arr r.Count
                                let len = r.CopyTo(!arr, 0)
                                for i in 0 .. len - 1 do
                                    let lv = arr.Value.[i]
                                    l <- l.Remove(cmp, hash, lv)
                                l
                            else
                                l :> HashSetNode<_>

                    member x.VisitLN(l, r) =
                        let rest = l.LNext |> HashSetLinked.filter (fun v -> not (r.Contains(cmp, l.LHash, v)))
                        if not (r.Contains(cmp, l.LHash, l.LValue)) then 
                            HashSetLeaf.New(l.LHash, l.LValue, rest)
                        else
                            match HashSetLinked.destruct rest with
                            | ValueSome (struct (v, rest)) ->
                                HashSetLeaf.New(l.LHash, v, rest)
                            | ValueNone ->
                                HashSetEmpty.Instance

                    member x.VisitNL(l, r) =
                        let b = matchPrefixAndGetBit r.LHash l.Prefix l.Mask
                        if b = 0u then
                            HashSetInner.Create(l.Prefix, l.Mask, visit2 x l.Left r, l.Right)
                        elif b = 1u then
                            HashSetInner.Create(l.Prefix, l.Mask, l.Left, visit2 x l.Right r)
                        else
                            l :> HashSetNode<_>

                    member x.VisitNN(l, r) = 
                        if l == r then
                            HashSetEmpty.Instance
                        else
                            let cc = compareMasks l.Mask r.Mask
                            if cc = 0 then
                                if l.Prefix = r.Prefix then
                                    let l' = (l.Left, r.Left) ||> visit2 x
                                    let r' = (l.Right, r.Right) ||> visit2 x
                                    HashSetInner.Create(l.Prefix, l.Mask, l', r')
                                else
                                    l :> HashSetNode<_>
                            elif cc > 0 then
                                let lr = matchPrefixAndGetBit l.Prefix r.Prefix r.Mask
                                if lr = 0u then
                                    visit2 x l r.Left
                                elif lr = 1u then
                                    visit2 x l r.Right
                                else
                                    l :> HashSetNode<_>
                            else
                                let rl = matchPrefixAndGetBit r.Prefix l.Prefix l.Mask
                                if rl = 0u then
                                    HashSetInner.Create(l.Prefix, l.Mask, visit2 x l.Left r, l.Right)
                                elif rl = 1u then
                                    HashSetInner.Create(l.Prefix, l.Mask, l.Left, visit2 x l.Right r)
                                else
                                    l :> HashSetNode<_>
                                    
            }

        let xor (cmp : IEqualityComparer<'T>) (l : HashSetNode<'T>) (r : HashSetNode<'T>)  =
            let arr = ref (Array.zeroCreate 4)

            (l, r) ||> visit2 {
                new HashSetVisitor2<'T, HashSetNode<'T>>() with

                    member x.VisitEE(_, _) = HashSetEmpty.Instance
                    member x.VisitEA(_, r) = r
                    member x.VisitAE(l, _) = l

                    member x.VisitLL(l, r) = 
                        if l == r then
                            HashSetEmpty.Instance
                        else
                            if l.LHash = r.LHash then
                                let mutable r = r :> HashSetNode<_>
                                let hash = l.LHash
                                ensureLength arr l.Count
                                let len = l.CopyTo(!arr, 0)
                                for i in 0 .. len - 1 do
                                    let lv = arr.Value.[i]
                                    r <- r.Alter(cmp, hash, lv, not)
                                r
                            else
                                HashSetInner.Join(l.LHash, l, r.LHash, r)

                    member x.VisitLN(l, r) =
                        let b = matchPrefixAndGetBit l.LHash r.Prefix r.Mask
                        if b = 0u then
                            HashSetInner.Create(r.Prefix, r.Mask, visit2 x l r.Left, r.Right)
                        elif b = 1u then
                            HashSetInner.Create(r.Prefix, r.Mask, r.Left, visit2 x l r.Right)
                        else
                            HashSetInner.Join(l.LHash, l, r.Prefix, r)

                    member x.VisitNL(l, r) =
                        let b = matchPrefixAndGetBit r.LHash l.Prefix l.Mask
                        if b = 0u then
                            HashSetInner.Create(l.Prefix, l.Mask, visit2 x l.Left r, l.Right)
                        elif b = 1u then
                            HashSetInner.Create(l.Prefix, l.Mask, l.Left, visit2 x l.Right r)
                        else
                            HashSetInner.Join(l.Prefix, l, r.LHash, r)

                    member x.VisitNN(l, r) = 
                        if l == r then
                            HashSetEmpty.Instance
                        else
                            let cc = compareMasks l.Mask r.Mask
                            if cc = 0 then
                                if l.Prefix = r.Prefix then
                                    let l' = (l.Left, r.Left) ||> visit2 x
                                    let r' = (l.Right, r.Right) ||> visit2 x
                                    HashSetInner.Create(l.Prefix, l.Mask, l', r')
                                else
                                    HashSetInner.Join(l.Prefix, l, r.Prefix, r)
                            elif cc > 0 then
                                let lr = matchPrefixAndGetBit l.Prefix r.Prefix r.Mask
                                if lr = 0u then
                                    HashSetInner.Create(r.Prefix, r.Mask, visit2 x l r.Left, r.Right)
                                elif lr = 1u then
                                    HashSetInner.Create(r.Prefix, r.Mask, r.Left, visit2 x l r.Right)
                                else
                                    HashSetInner.Join(l.Prefix, l, r.Prefix, r)
                            else
                                let rl = matchPrefixAndGetBit r.Prefix l.Prefix l.Mask
                            
                                if rl = 0u then
                                    HashSetInner.Create(l.Prefix, l.Mask, visit2 x l.Left r, l.Right)
                                elif rl = 1u then
                                    HashSetInner.Create(l.Prefix, l.Mask, l.Left, visit2 x l.Right r)
                                else
                                    HashSetInner.Join(l.Prefix, l, r.Prefix, r)
                                    
            }
            
        let computeDelta 
            (cmp : IEqualityComparer<'T>)
            (add : 'T -> 'OP)
            (remove : 'T -> 'OP)
            (l : HashSetNode<'T>) 
            (r : HashSetNode<'T>)  =

            let arr = ref (Array.zeroCreate 4)

            (l, r) ||> visit2 {
                new HashSetVisitor2<'T, HashMapNode<'T, 'OP>>() with

                    member x.VisitEE(_, _) = HashMapEmpty.Instance
                    member x.VisitEA(_, r) = r.MapToMap(add)
                    member x.VisitAE(l, _) = l.MapToMap(remove)

                    member x.VisitLL(l, r) = 
                        if l == r then
                            HashMapEmpty.Instance
                        else
                            if l.LHash = r.LHash then
                                let mutable r = r :> HashSetNode<_>
                                let mutable res = HashMapEmpty.Instance
                                let hash = l.LHash

                                ensureLength arr l.Count
                                let len = l.CopyTo(!arr, 0)
                                for i in 0 .. len - 1 do
                                    let lv = arr.Value.[i]
                                    match r.TryRemove(cmp, hash, lv) with
                                    | ValueSome rest ->
                                        r <- rest
                                    | ValueNone ->
                                        res <- res.AddInPlaceUnsafe(cmp, hash, lv, remove lv)
                                        
                                ensureLength arr r.Count
                                let len = r.CopyTo(!arr, 0)
                                for i in 0 .. len - 1 do
                                    let rv = arr.Value.[i]
                                    res <- res.AddInPlaceUnsafe(cmp, hash, rv, add rv)
                        
                                res
                            else
                                let mutable res = l.MapToMap(remove)
                                ensureLength arr r.Count
                                let len = r.CopyTo(!arr, 0)
                                for i in 0 .. len - 1 do
                                    let rv = arr.Value.[i]
                                    res <- res.AddInPlaceUnsafe(cmp, r.LHash, rv, add rv)
                                res

                    member x.VisitLN(l, r) =
                        let b = matchPrefixAndGetBit l.LHash r.Prefix r.Mask
                        if b = 0u then
                            HashMapInner.Create(r.Prefix, r.Mask, visit2 x l r.Left, r.Right.MapToMap(add))
                        elif b = 1u then
                            HashMapInner.Create(r.Prefix, r.Mask, r.Left.MapToMap(add), visit2 x l r.Right)
                        else
                            HashMapInner.Join(l.LHash, l.MapToMap(remove), r.Prefix, r.MapToMap(add))

                    member x.VisitNL(l, r) =
                        let b = matchPrefixAndGetBit r.LHash l.Prefix l.Mask
                        if b = 0u then
                            HashMapInner.Create(l.Prefix, l.Mask, visit2 x l.Left r, l.Right.MapToMap(remove))
                        elif b = 1u then
                            HashMapInner.Create(l.Prefix, l.Mask, l.Left.MapToMap(remove), visit2 x l.Right r)
                        else
                            HashMapInner.Join(l.Prefix, l.MapToMap(remove), r.LHash, r.MapToMap(add))

                    member x.VisitNN(l, r) = 
                        if l == r then
                            HashMapEmpty.Instance
                        else
                            let cc = compareMasks l.Mask r.Mask
                            if cc = 0 then
                                if l.Prefix = r.Prefix then
                                    let l' = (l.Left, r.Left) ||> visit2 x
                                    let r' = (l.Right, r.Right) ||> visit2 x
                                    HashMapInner.Create(l.Prefix, l.Mask, l', r')
                                else
                                    HashMapInner.Join(l.Prefix, l.MapToMap(remove), r.Prefix, r.MapToMap(add))
                            elif cc > 0 then
                                let lr = matchPrefixAndGetBit l.Prefix r.Prefix r.Mask
                                if lr = 0u then
                                    HashMapInner.Create(r.Prefix, r.Mask, visit2 x l r.Left, r.Right.MapToMap(add))
                                elif lr = 1u then
                                    HashMapInner.Create(r.Prefix, r.Mask, r.Left.MapToMap(add), visit2 x l r.Right)
                                else
                                    HashMapInner.Join(l.Prefix, l.MapToMap(remove), r.Prefix, r.MapToMap(add))
                            else
                                let rl = matchPrefixAndGetBit r.Prefix l.Prefix l.Mask
                            
                                if rl = 0u then
                                    HashMapInner.Create(l.Prefix, l.Mask, visit2 x l.Left r, l.Right.MapToMap(remove))
                                elif rl = 1u then
                                    HashMapInner.Create(l.Prefix, l.Mask, l.Left.MapToMap(remove), visit2 x l.Right r)
                                else
                                    HashMapInner.Join(l.Prefix, l.MapToMap(remove), r.Prefix, r.MapToMap(add))
                                    
            }

        let applyDelta
            (cmp : IEqualityComparer<'T>) 
            (apply : 'T -> bool -> 'D -> struct(bool * voption<'DOut>))
            (state : HashSetNode<'T>)
            (delta : HashMapNode<'T, 'D>) =

            let arr1 = ref (Array.zeroCreate 4)
            let arr2 = ref (Array.zeroCreate 4)
            let apply = OptimizedClosures.FSharpFunc<_,_,_,_>.Adapt(apply)
            let onlyDelta = OptimizedClosures.FSharpFunc<_,_,_>.Adapt(fun k d -> apply.Invoke(k, false, d))
    
            (state, delta) ||> visitMap2 {
                new HashSetMapVisitor<'T, 'D, struct (HashSetNode<'T> * HashMapNode<'T, 'DOut>)>() with

                    member x.VisitEE(_, _) = 
                        struct (HashSetEmpty.Instance, HashMapEmpty.Instance)

                    member x.VisitEA(_, r) =    
                        r.ChooseSV2 onlyDelta

                    member x.VisitAE(l, _) = 
                        struct(l, HashMapEmpty.Instance)

                    member x.VisitLL(state, delta) = 
                        if state.LHash = delta.LHash then
                            let mutable delta = delta :> HashMapNode<_,_>
                            let mutable resState = HashSetEmpty.Instance
                            let mutable resDelta = HashMapEmpty.Instance
                            let hash = state.LHash
                            ensureLength arr1 state.Count
                            let len = state.CopyTo(!arr1, 0)
                            for i in 0 .. len - 1 do
                                let k = arr1.Value.[i]
                                match delta.TryRemove(cmp, hash, k) with
                                | ValueSome (dd, rest) ->
                                    delta <- rest
                                    let struct (s, d) = apply.Invoke(k, true, dd)

                                    if s then 
                                        resState <- resState.AddInPlaceUnsafe(cmp, hash, k)

                                    match d with
                                    | ValueSome v -> resDelta <- resDelta.AddInPlaceUnsafe(cmp, hash, k, v)
                                    | ValueNone -> ()

                                | ValueNone ->
                                    resState <- resState.AddInPlaceUnsafe(cmp, hash, k)
                                    
                            ensureLength arr2 delta.Count
                            let len = delta.CopyToV(!arr2, 0)
                            for i in 0 .. len - 1 do
                                let struct (k, rv) = arr2.Value.[i]
                                let struct (s, d) = onlyDelta.Invoke(k, rv)
                                if s then
                                    resState <- resState.AddInPlaceUnsafe(cmp, hash, k)

                                match d with
                                | ValueSome v -> resDelta <- resDelta.AddInPlaceUnsafe(cmp, hash, k, v)
                                | ValueNone -> ()
                    
                            struct(resState, resDelta)
                        else
                            let struct (ds, dd) = delta.ChooseSV2(onlyDelta)
                            struct (
                                HashSetInner.Join(state.LHash, state, delta.LHash, ds),
                                dd
                            )

                    member x.VisitLN(state, delta) =
                        let b = matchPrefixAndGetBit state.LHash delta.Prefix delta.Mask
                        if b = 0u then
                            let struct (ls, ld) = visitMap2 x state delta.Left
                            let struct (rs, rd) = delta.Right.ChooseSV2(onlyDelta)
                            struct(
                                HashSetInner.Create(delta.Prefix, delta.Mask, ls, rs),
                                HashMapInner.Create(delta.Prefix, delta.Mask, ld, rd)
                            )
                        elif b = 1u then
                            let struct (ls, ld) = delta.Left.ChooseSV2(onlyDelta)
                            let struct (rs, rd) = visitMap2 x state delta.Right
                            struct(
                                HashSetInner.Create(delta.Prefix, delta.Mask, ls, rs),
                                HashMapInner.Create(delta.Prefix, delta.Mask, ld, rd)
                            )
                        else
                            let struct (ds, dd) = delta.ChooseSV2(onlyDelta)
                            struct(
                                HashSetInner.Join(state.LHash, state, delta.Prefix, ds),
                                dd
                            )

                    member x.VisitNL(l, r) =
                        let b = matchPrefixAndGetBit r.LHash l.Prefix l.Mask
                        if b = 0u then
                            let struct (ls, ld) = visitMap2 x l.Left r
                            struct (
                                HashSetInner.Create(l.Prefix, l.Mask, ls, l.Right),
                                ld
                            )
                        elif b = 1u then
                            let struct (rs, rd) = visitMap2 x l.Right r
                            struct (
                                HashSetInner.Create(l.Prefix, l.Mask, l.Left, rs),
                                rd
                            )
                        else
                            let struct (rs, rd) = r.ChooseSV2(onlyDelta)
                            struct (
                                HashSetInner.Join(l.Prefix, l, r.LHash, rs),
                                rd
                            )

                    member x.VisitNN(l, r) = 
                        let cc = compareMasks l.Mask r.Mask
                        if cc = 0 then
                            if l.Prefix = r.Prefix then
                                let struct (ls, ld) = (l.Left, r.Left) ||> visitMap2 x
                                let struct (rs, rd) = (l.Right, r.Right) ||> visitMap2 x
                                struct (
                                    HashSetInner.Create(l.Prefix, l.Mask, ls, rs),
                                    HashMapInner.Create(l.Prefix, l.Mask, ld, rd)
                                )
                            else
                                let struct (rs, rd) = r.ChooseSV2 onlyDelta
                                struct (
                                    HashSetInner.Join(l.Prefix, l, r.Prefix, rs),
                                    rd
                                )
                        elif cc > 0 then
                            let lr = matchPrefixAndGetBit l.Prefix r.Prefix r.Mask
                            if lr = 0u then
                                let struct (ls, ld) = visitMap2 x l r.Left
                                let struct (rs, rd) = r.Right.ChooseSV2(onlyDelta)
                                struct (
                                    HashSetInner.Create(r.Prefix, r.Mask, ls, rs),
                                    HashMapInner.Create(r.Prefix, r.Mask, ld, rd)
                                )
                            elif lr = 1u then
                                let struct (ls, ld) = r.Left.ChooseSV2(onlyDelta)
                                let struct (rs, rd) = visitMap2 x l r.Right
                                struct (
                                    HashSetInner.Create(r.Prefix, r.Mask, ls, rs),
                                    HashMapInner.Create(r.Prefix, r.Mask, ld, rd)
                                )
                            else
                                let struct (rs, rd) = r.ChooseSV2 onlyDelta
                                struct (
                                    HashSetInner.Join(l.Prefix, l, r.Prefix, rs),
                                    rd
                                )
                        else
                            let rl = matchPrefixAndGetBit r.Prefix l.Prefix l.Mask
                        
                            if rl = 0u then
                                let struct (ls, ld) = visitMap2 x l.Left r
                                struct (
                                    HashSetInner.Create(l.Prefix, l.Mask, ls, l.Right),
                                    ld
                                )
                            elif rl = 1u then
                                let struct (rs, rd) = visitMap2 x l.Right r
                                struct (
                                    HashSetInner.Create(l.Prefix, l.Mask, l.Left, rs),
                                    rd
                                )
                            else
                                let struct (rs, rd) = r.ChooseSV2 onlyDelta
                                struct (
                                    HashSetInner.Join(l.Prefix, l, r.Prefix, rs),
                                    rd
                                )
                                
            }



[<Struct; CustomEquality; NoComparison; StructuredFormatDisplay("{AsString}"); CompiledName("FSharpHashSet`1")>]
type HashSet<'T> internal(cmp: IEqualityComparer<'T>, root: HashSetNode<'T>) =
    
    static member Empty = HashSet<'T>(DefaultEqualityComparer<'T>.Instance, HashSetEmpty.Instance)

    member x.Count = root.Count
    member x.IsEmpty = root.IsEmpty

    member internal x.Comparer = cmp
    member internal x.Root = root
    
    member private x.AsString = x.ToString()

    override x.ToString() =
        if x.Count > 8 then
            x |> Seq.take 8 |> Seq.map (sprintf "%A") |> String.concat "; " |> sprintf "HashSet [%s; ...]"
        else
            x |> Seq.map (sprintf "%A") |> String.concat "; " |> sprintf "HashSet [%s]"


    override x.GetHashCode() = 
        root.ComputeHash()

    override x.Equals o =
        match o with
        | :? HashSet<'T> as o ->
            if System.Object.ReferenceEquals(root, o.Root) then 
                true
            else
                HashSetNode.equals cmp root o.Root
        | _ -> false


    member x.Equals(o : HashSet<'T>) =
        if System.Object.ReferenceEquals(root, o.Root) then 
            true
        else 
            HashSetNode.equals cmp root o.Root

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Single(value : 'T) =  
        let cmp = DefaultEqualityComparer<'T>.Instance
        HashSet(cmp, HashSetNoCollisionLeaf.New(uint32 (cmp.GetHashCode value), value))
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member OfSeq(elements: seq<'T>) =  
        match elements with
        | :? HashSet<'T> as set ->
            set
        | _ ->
            let cmp = DefaultEqualityComparer<'T>.Instance
            let mutable r = HashMapImplementation.HashSetEmpty.Instance 
            for v in elements do
                let hash = cmp.GetHashCode v |> uint32
                r <- r.AddInPlaceUnsafe(cmp, hash, v)
            HashSet<'T>(cmp, r)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member OfList(elements: list<'T>) =  
        let cmp = DefaultEqualityComparer<'T>.Instance
        let mutable r = HashMapImplementation.HashSetEmpty.Instance 
        for v in elements do
            let hash = cmp.GetHashCode v |> uint32
            r <- r.AddInPlaceUnsafe(cmp, hash, v)
        HashSet<'T>(cmp, r)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member OfArray(elements: array<'T>) =  
        let cmp = DefaultEqualityComparer<'T>.Instance
        let mutable r = HashMapImplementation.HashSetEmpty.Instance 
        for v in elements do
            let hash = cmp.GetHashCode v |> uint32
            r <- r.AddInPlaceUnsafe(cmp, hash, v)
        HashSet<'T>(cmp, r)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member OfArrayRange(elements: array<'T>, offset: int, length: int) =  
        let cmp = DefaultEqualityComparer<'T>.Instance
        let mutable r = HashMapImplementation.HashSetEmpty.Instance 
        for i in offset .. offset + length - 1 do
            let v = elements.[i]
            let hash = cmp.GetHashCode v |> uint32
            r <- r.AddInPlaceUnsafe(cmp, hash, v)

        HashSet<'T>(cmp, r)
    member inline x.ToSeq() =
        x :> seq<_>

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.ToList() = root.ToList []
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member internal x.ToListMatch() = HashSetNode.toList [] root
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.ToArray() =
        let arr = Array.zeroCreate root.Count
        root.CopyTo(arr, 0) |> ignore
        arr   

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member internal x.ToArrayMatch() =
        let arr = Array.zeroCreate root.Count
        HashSetNode.copyTo arr 0 root |> ignore
        arr

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Add(value: 'T) =
        let hash = cmp.GetHashCode value |> uint32
        let newRoot = root.Add(cmp, hash, value)
        HashSet(cmp, newRoot)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Remove(value: 'T) =
        let hash = cmp.GetHashCode value |> uint32
        let newRoot = root.Remove(cmp, hash, value)
        HashSet(cmp, newRoot)

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.TryRemove(value: 'T) =
        let hash = cmp.GetHashCode value |> uint32
        match root.TryRemove(cmp, hash, value) with
        | ValueSome newRoot -> Some (HashSet(cmp, newRoot))
        | ValueNone -> None
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Contains(value: 'T) =
        let hash = cmp.GetHashCode value |> uint32
        root.Contains(cmp, hash, value)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Alter(value: 'T, update: bool -> bool) =
        let hash = cmp.GetHashCode value |> uint32
        let newRoot = root.Alter(cmp, hash, value, update)
        HashSet(cmp, newRoot)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Map(mapping: 'T -> 'R) =
        let cmp = DefaultEqualityComparer<'R>.Instance
        let mutable res = HashSetEmpty.Instance
        for o in x do
            let v = mapping o
            let hash = cmp.GetHashCode v
            res <- res.AddInPlaceUnsafe(cmp, uint32 hash, v)
        HashSet(cmp, res)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.MapToMap(mapping: 'T -> 'R) =
        let cmp = DefaultEqualityComparer<'T>.Instance
        HashMap(cmp, root.MapToMap(mapping))
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Choose(mapping: 'T -> option<'R>) =
        let cmp = DefaultEqualityComparer<'R>.Instance
        let mutable res = HashSetEmpty.Instance
        for o in x do
            match mapping o with
            | Some v -> 
                let hash = cmp.GetHashCode v
                res <- res.AddInPlaceUnsafe(cmp, uint32 hash, v)
            | None ->
                ()
        HashSet(cmp, res)

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.ChooseV(mapping: 'T -> voption<'R>) =
        let cmp = DefaultEqualityComparer<'R>.Instance
        let mutable res = HashSetEmpty.Instance
        for o in x do
            match mapping o with
            | ValueSome v -> 
                let hash = cmp.GetHashCode v
                res <- res.AddInPlaceUnsafe(cmp, uint32 hash, v)
            | ValueNone ->
                ()
        HashSet(cmp, res)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Filter(predicate: 'T -> bool) =
        let newRoot = root.Filter(predicate)
        HashSet(cmp, newRoot)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Iter(action: 'T -> unit) =
        root.Iter(action)

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Fold(acc: 'S -> 'T -> 'S, seed : 'S) =
        let acc = OptimizedClosures.FSharpFunc<'S, 'T, 'S>.Adapt acc
        root.Fold(acc, seed)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Exists(predicate: 'T -> bool) =
        root.Exists predicate
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Forall(predicate: 'T -> bool) =
        root.Forall predicate
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member ComputeDelta(l : HashSet<'T>, r : HashSet<'T>, add : 'T -> 'OP, remove : 'T -> 'OP) =   
        let cmp = DefaultEqualityComparer<'T>.Instance
        let result = HashSetNode.computeDelta cmp add remove l.Root r.Root
        HashMap(cmp, result)
 
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member ApplyDelta(state : HashSet<'T>, delta : HashMap<'T, 'D>, apply : 'T -> bool -> 'D -> struct(bool * voption<'DOut>)) =   
        let cmp = DefaultEqualityComparer<'T>.Instance
        let struct(ns, nd) = HashSetNode.applyDelta cmp apply state.Root delta.Root
        HashSet(cmp, ns), HashMap(cmp, nd)
     
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Union(l : HashSet<'T>, r : HashSet<'T>) =   
        let cmp = DefaultEqualityComparer<'T>.Instance
        let result = HashSetNode.union cmp l.Root r.Root
        HashSet(cmp, result)
 
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Difference(l : HashSet<'T>, r : HashSet<'T>) =   
        let cmp = DefaultEqualityComparer<'T>.Instance
        let result = HashSetNode.difference cmp l.Root r.Root
        HashSet(cmp, result)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Xor(l : HashSet<'T>, r : HashSet<'T>) =   
        let cmp = DefaultEqualityComparer<'T>.Instance
        let result = HashSetNode.xor cmp l.Root r.Root
        HashSet(cmp, result)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Intersect(l : HashSet<'T>, r : HashSet<'T>) =   
        if l.IsEmpty || r.IsEmpty then HashSet<'T>.Empty
        else
            let cmp = DefaultEqualityComparer<'T>.Instance
            let result = HashSetNode.intersect cmp l.Root r.Root
            HashSet(cmp, result)
 
    member x.CopyTo(array : 'T[], arrayIndex : int) =
        if arrayIndex < 0 || arrayIndex + x.Count > array.Length then raise <| System.IndexOutOfRangeException()
        root.CopyTo(array, arrayIndex) |> ignore

        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Overlaps(other : HashSet<'T>) =
        let x = x
        other.Exists (fun e -> x.Contains e)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.SetEquals(other : HashSet<'T>) =
        x.Equals other
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.SetEquals(other : seq<'T>) =
        match other with
        | :? HashSet<'T> as other -> x.SetEquals other
        | other -> x.SetEquals(HashSet.OfSeq other)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Overlaps(other : seq<'T>) =
        match other with
        | :? HashSet<'T> as other -> x.Overlaps other
        | other -> 
            let x = x
            other |> Seq.exists (fun e -> x.Contains e)
            
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.IsProperSubsetOf(other : HashSet<'T>) =
        other.Count > x.Count && x.Forall(fun e -> other.Contains e)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.IsSubsetOf(other : HashSet<'T>) =
        other.Count >= x.Count && x.Forall(fun e -> other.Contains e)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.IsProperSupersetOf(other : HashSet<'T>) =
        let x = x
        other.Count < x.Count && other.Forall(fun e -> x.Contains e)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.IsSupersetOf(other : HashSet<'T>) =
        let x = x
        other.Count <= x.Count && other.Forall(fun e -> x.Contains e)

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.IsProperSubsetOf(other : ISet<'T>) =
        other.Count > x.Count && x.Forall(fun e -> other.Contains e)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.IsSubsetOf(other : ISet<'T>) =
        other.Count >= x.Count && x.Forall(fun e -> other.Contains e)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.IsProperSupersetOf(other : ISet<'T>) =
        let x = x
        other.Count < x.Count && other |> Seq.forall (fun e -> x.Contains e)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.IsSupersetOf(other : ISet<'T>) =
        let x = x
        other.Count <= x.Count && other |> Seq.forall (fun e -> x.Contains e)

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.IsProperSubsetOf(other : seq<'T>) =
        match other with
        | :? HashSet<'T> as other -> x.IsProperSubsetOf other
        #if !FABLE_COMPILER
        | :? ISet<'T> as other -> x.IsProperSubsetOf other
        #endif
        | other -> x.IsProperSubsetOf (HashSet<'T>.OfSeq other)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.IsSubsetOf(other : seq<'T>) =
        match other with
        | :? HashSet<'T> as other -> x.IsSubsetOf other
        #if !FABLE_COMPILER
        | :? ISet<'T> as other -> x.IsSubsetOf other
        #endif
        | other -> x.IsSubsetOf (HashSet<'T>.OfSeq other)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.IsProperSupersetOf(other : seq<'T>) =
        match other with
        | :? HashSet<'T> as other -> x.IsProperSupersetOf other
        #if !FABLE_COMPILER
        | :? ISet<'T> as other -> x.IsProperSupersetOf other
        #endif
        | other -> x.IsProperSupersetOf (HashSet<'T>.OfSeq other)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.IsSupersetOf(other : seq<'T>) =
        match other with
        | :? HashSet<'T> as other -> x.IsSupersetOf other
        #if !FABLE_COMPILER
        | :? ISet<'T> as other -> x.IsSupersetOf other
        #endif
        | other -> x.IsSupersetOf (HashSet<'T>.OfSeq other)

    member x.GetEnumerator() = new HashSetEnumerator<_>(x)

    interface System.Collections.IEnumerable with 
        member x.GetEnumerator() = new HashSetEnumerator<_>(x) :> _
        
    interface System.Collections.Generic.IEnumerable<'T> with 
        member x.GetEnumerator() = new HashSetEnumerator<_>(x) :> _

    interface System.Collections.Generic.ICollection<'T> with
        member x.Count = x.Count
        member x.Contains o = x.Contains o
        member x.IsReadOnly = true
        member x.Add _ = failwith "readonly"
        member x.Remove _ = failwith "readonly"
        member x.Clear() = failwith "readonly"
        member x.CopyTo(array : 'T[], arrayIndex : int) =
            x.CopyTo(array, arrayIndex) |> ignore

    #if !FABLE_COMPILER
    interface System.Collections.Generic.ISet<'T> with
        member x.Add _ = failwith "readonly"
        member x.UnionWith _ = failwith "readonly"
        member x.ExceptWith _ = failwith "readonly"
        member x.IntersectWith _ = failwith "readonly"
        member x.SymmetricExceptWith _ = failwith "readonly"
        member x.IsProperSubsetOf(other : seq<'T>) = x.IsProperSubsetOf other
        member x.IsSubsetOf(other : seq<'T>) = x.IsSubsetOf other
        member x.IsProperSupersetOf(other : seq<'T>) = x.IsProperSupersetOf other
        member x.IsSupersetOf(other : seq<'T>) = x.IsSupersetOf other
        member x.Overlaps(other : seq<'T>) = x.Overlaps other
        member x.SetEquals(other : seq<'T>) = x.SetEquals other
    #endif

    new(elements : seq<'T>) = 
        let o = HashSet.OfSeq elements
        HashSet<'T>(o.Comparer, o.Root)
        
    new(elements : HashSet<'T>) = 
        HashSet<'T>(elements.Comparer, elements.Root)
        
    new(elements : 'T[]) = 
        let o = HashSet.OfArray elements
        HashSet<'T>(o.Comparer, o.Root)
        

and HashSetEnumerator<'T> =
    struct
        val mutable private Root : HashSetNode<'T>
        val mutable private Head : HashSetNode<'T>
        val mutable private Tail : list<HashSetNode<'T>>
        val mutable private BufferValueCount : int
        val mutable private Values : 'T[]
        val mutable private Index : int
        val mutable private Next : 'T
        
        member x.Collect() =
            match x.Head with
            | :? HashSetNoCollisionLeaf<'T> as h ->
                x.Next <- h.Value
                x.Index <- 0
                x.BufferValueCount <- 0
                if x.Tail.IsEmpty then
                    x.Head <- Unchecked.defaultof<_>
                    x.Tail <- []
                else
                    x.Head <- x.Tail.Head
                    x.Tail <- x.Tail.Tail    
                true

            | :? HashSetCollisionLeaf<'T> as h ->
                                    
                let cnt = h.Count
                if isNull x.Values || x.Values.Length < cnt-1 then
                    x.Values <- Array.zeroCreate (cnt-1)
            
                x.Next <- h.Value
                let mutable c = h.Next
                let mutable i = 0
                while not (isNull c) do
                    x.Values.[i] <- c.Value
                    c <- c.Next
                    i <- i + 1
                x.BufferValueCount <- i
                x.Index <- 0
                if x.Tail.IsEmpty then
                    x.Head <- Unchecked.defaultof<_>
                    x.Tail <- []
                else
                    x.Head <- x.Tail.Head
                    x.Tail <- x.Tail.Tail
                true

            | :? HashSetInner<'T> as h ->
                if typesize<'T> <= 64 && h._Count <= 16 then
                    h.CopyTo(x.Values, 0) |> ignore
                    x.BufferValueCount <- h._Count
                    x.Next <- x.Values.[0]
                    x.Index <- 1 // skip first element of array
                    if x.Tail.IsEmpty then
                        x.Head <- Unchecked.defaultof<_>
                        x.Tail <- []
                    else
                        x.Head <- x.Tail.Head
                        x.Tail <- x.Tail.Tail
                    true
                else
                    x.Head <- h.Left
                    x.Tail <- h.Right :: x.Tail
                    x.Collect()
                
            | _ -> false

        member x.MoveNext() =
            if x.Index < x.BufferValueCount then
                x.Next <- x.Values.[x.Index]
                x.Index <- x.Index + 1
                true
            else
                x.Collect()

        member x.Reset() =
            x.Index <- -1
            x.Head <- x.Root
            x.Tail <- []
            x.BufferValueCount <- -1

        member x.Dispose() =
            x.Values <- null
            x.Index <- -1
            x.Head <- Unchecked.defaultof<_>
            x.Tail <- []
            x.Root <- Unchecked.defaultof<_>
            x.Next <- Unchecked.defaultof<_>

        member x.Current = x.Next

        interface System.Collections.IEnumerator with
            member x.MoveNext() = x.MoveNext()
            member x.Reset() = x.Reset()
            member x.Current = x.Current :> obj
            
        interface System.Collections.Generic.IEnumerator<'T> with
            member x.Current = x.Current
            member x.Dispose() = x.Dispose()

        new (map : HashSet<'T>) =
            let cnt = map.Count
            {
                Root = map.Root
                Head = map.Root
                Tail = []
                Values = if typesize<'T> <= 64 && cnt > 1 then Array.zeroCreate (min cnt 16) else null
                Index = -1
                BufferValueCount = -1
                Next = Unchecked.defaultof<_>
            }
    end

[<Struct; CustomEquality; NoComparison; StructuredFormatDisplay("{AsString}"); CompiledName("FSharpHashMap`2")>]
type HashMap<'K, [<EqualityConditionalOn>] 'V> internal(cmp: IEqualityComparer<'K>, root: HashMapNode<'K, 'V>) =

    static member Empty = HashMap<'K, 'V>(DefaultEqualityComparer<'K>.Instance, HashMapEmpty.Instance)

    member x.Count = root.Count
    member x.IsEmpty = root.IsEmpty

    member internal x.Comparer = cmp
    member internal x.Root : HashMapNode<'K, 'V> = root
    
    member x.Item
        with get(k : 'K) : 'V =
            let hash = cmp.GetHashCode k
            match root.TryFindV(cmp, uint32 hash, k) with
            | ValueSome v -> v
            | ValueNone -> raise <| KeyNotFoundException()
            
    member private x.AsString = x.ToString()

    override x.ToString() =
        if x.Count > 8 then
            x |> Seq.take 8 |> Seq.map (sprintf "%A") |> String.concat "; " |> sprintf "HashMap [%s; ...]"
        else
            x |> Seq.map (sprintf "%A") |> String.concat "; " |> sprintf "HashMap [%s]"

    override x.GetHashCode() = 
        root.ComputeHash()

    override x.Equals o =
        match o with
        | :? HashMap<'K, 'V> as o -> 
            if System.Object.ReferenceEquals(root, o.Root) then
                true
            else
                HashMapNode.equals cmp root o.Root
        | _ -> false
        
    member x.Equals(o : HashMap<'K, 'V>) =
        if System.Object.ReferenceEquals(root, o.Root) then
            true
        else
            HashMapNode.equals cmp root o.Root

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Single(key: 'K, value : 'V) =  
        let cmp = DefaultEqualityComparer<'K>.Instance
        HashMap(cmp, HashMapNoCollisionLeaf.New(uint32 (cmp.GetHashCode key), key, value))
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member OfSeq(elements: seq<'K * 'V>) = 
        match elements with
        | :? HashMap<'K, 'V> as map ->
            map
        | _ -> 
            let cmp = DefaultEqualityComparer<'K>.Instance
            let mutable r = HashMapImplementation.HashMapEmpty.Instance 
            for (k, v) in elements do
                let hash = cmp.GetHashCode k |> uint32
                r <- r.AddInPlaceUnsafe(cmp, hash, k, v)
            HashMap<'K, 'V>(cmp, r)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member OfList(elements: list<'K * 'V>) =  
        let cmp = DefaultEqualityComparer<'K>.Instance
        let mutable r = HashMapImplementation.HashMapEmpty.Instance 
        for (k, v) in elements do
            let hash = cmp.GetHashCode k |> uint32
            r <- r.AddInPlaceUnsafe(cmp, hash, k, v)
        HashMap<'K, 'V>(cmp, r)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member OfArray(elements: array<'K * 'V>) =  
        let cmp = DefaultEqualityComparer<'K>.Instance
        let mutable r = HashMapImplementation.HashMapEmpty.Instance 
        for (k, v) in elements do
            let hash = cmp.GetHashCode k |> uint32
            r <- r.AddInPlaceUnsafe(cmp, hash, k, v)
        HashMap<'K, 'V>(cmp, r)
        
    
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member OfSeqV(elements: seq<struct ('K * 'V)>) =  
        let cmp = DefaultEqualityComparer<'K>.Instance
        let mutable r = HashMapImplementation.HashMapEmpty.Instance 
        for struct (k, v) in elements do
            let hash = cmp.GetHashCode k |> uint32
            r <- r.AddInPlaceUnsafe(cmp, hash, k, v)
        HashMap<'K, 'V>(cmp, r)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member OfListV(elements: list<struct ('K * 'V)>) =  
        let cmp = DefaultEqualityComparer<'K>.Instance
        let mutable r = HashMapImplementation.HashMapEmpty.Instance 
        for struct (k, v) in elements do
            let hash = cmp.GetHashCode k |> uint32
            r <- r.AddInPlaceUnsafe(cmp, hash, k, v)
        HashMap<'K, 'V>(cmp, r)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member OfArrayV(elements: array<struct ('K * 'V)>) =  
        let cmp = DefaultEqualityComparer<'K>.Instance
        let mutable r = HashMapImplementation.HashMapEmpty.Instance 
        for struct (k, v) in elements do
            let hash = cmp.GetHashCode k |> uint32
            r <- r.AddInPlaceUnsafe(cmp, hash, k, v)
        HashMap<'K, 'V>(cmp, r)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member OfArrayRangeV(elements: array<struct ('K * 'V)>, offset: int, length: int) =  
        let cmp = DefaultEqualityComparer<'K>.Instance
        let mutable r = HashMapImplementation.HashMapEmpty.Instance 
        for i in offset .. offset + length - 1 do
            let struct (k, v) = elements.[i]
            let hash = cmp.GetHashCode k |> uint32
            r <- r.AddInPlaceUnsafe(cmp, hash, k, v)
        HashMap<'K, 'V>(cmp, r)



    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Add(key: 'K, value: 'V) =
        let hash = cmp.GetHashCode key |> uint32
        let newRoot = root.Add(cmp, hash, key, value)
        HashMap(cmp, newRoot)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Remove(key: 'K) =
        let hash = cmp.GetHashCode key |> uint32
        let newRoot = root.Remove(cmp, hash, key)
        HashMap(cmp, newRoot)
         
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.TryRemove(key: 'K) =
        let hash = cmp.GetHashCode key |> uint32
        match root.TryRemove(cmp, hash, key) with
        | ValueSome (struct(value, newRoot)) ->
            Some (value, HashMap(cmp, newRoot))
        | ValueNone ->
            None
         
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.TryRemoveV(key: 'K) =
        let hash = cmp.GetHashCode key |> uint32
        match root.TryRemove(cmp, hash, key) with
        | ValueSome (struct(value, newRoot)) ->
            ValueSome (value, HashMap(cmp, newRoot))
        | ValueNone ->
            ValueNone

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.TryFind(key: 'K) =
        let hash = cmp.GetHashCode key |> uint32
        root.TryFind(cmp, hash, key)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.TryFindV(key: 'K) =
        let hash = cmp.GetHashCode key |> uint32
        root.TryFindV(cmp, hash, key)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.ContainsKey(key: 'K) =
        let hash = cmp.GetHashCode key |> uint32
        root.ContainsKey(cmp, hash, key)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Alter(key: 'K, update: option<'V> -> option<'V>) =
        let hash = cmp.GetHashCode key |> uint32
        let newRoot = root.Alter(cmp, hash, key, update)
        HashMap(cmp, newRoot)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.AlterV(key: 'K, update: voption<'V> -> voption<'V>) =
        let hash = cmp.GetHashCode key |> uint32
        let newRoot = root.AlterV(cmp, hash, key, update)
        HashMap(cmp, newRoot)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Map(mapping: 'K -> 'V -> 'T) =
        let mapping = OptimizedClosures.FSharpFunc<'K, 'V, 'T>.Adapt mapping
        let newRoot = root.Map(mapping)
        HashMap(cmp, newRoot)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Choose(mapping: 'K -> 'V -> option<'T>) =
        let mapping = OptimizedClosures.FSharpFunc<'K, 'V, option<'T>>.Adapt mapping
        let newRoot = root.Choose(mapping)
        HashMap(cmp, newRoot)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.ChooseV(mapping: 'K -> 'V -> voption<'T>) =
        let mapping = OptimizedClosures.FSharpFunc<'K, 'V, voption<'T>>.Adapt mapping
        let newRoot = root.ChooseV(mapping)
        HashMap(cmp, newRoot)

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Filter(predicate: 'K -> 'V -> bool) =
        let predicate = OptimizedClosures.FSharpFunc<'K, 'V, bool>.Adapt predicate
        let newRoot = root.Filter(predicate)
        HashMap(cmp, newRoot)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Iter(action: 'K -> 'V -> unit) =
        let action = OptimizedClosures.FSharpFunc<'K, 'V, unit>.Adapt action
        root.Iter(action)

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Fold(acc: 'S -> 'K -> 'V -> 'S, seed : 'S) =
        let acc = OptimizedClosures.FSharpFunc<'S, 'K, 'V, 'S>.Adapt acc
        root.Fold(acc, seed)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Exists(predicate: 'K -> 'V -> bool) =
        let predicate = OptimizedClosures.FSharpFunc<'K, 'V, bool>.Adapt predicate
        root.Exists predicate
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.Forall(predicate: 'K -> 'V -> bool) =
        let predicate = OptimizedClosures.FSharpFunc<'K, 'V, bool>.Adapt predicate
        root.Forall predicate
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member inline x.ToSeq() =
        x :> seq<_>
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.ToSeqV() =
        let x = x
        Seq.ofEnumerator(fun () -> new HashMapStructEnumerator<_,_>(x))
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.ToListV() = root.ToListV []
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.ToKeyList() = root.ToKeyList []
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.ToValueList() = root.ToValueList []

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.ToArrayV() =
        let arr = Array.zeroCreate root.Count
        root.CopyToV(arr, 0) |> ignore
        arr
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.ToKeyArray() =
        let arr = Array.zeroCreate root.Count
        root.CopyToKeys(arr, 0) |> ignore
        arr
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.ToValueArray() =
        let arr = Array.zeroCreate root.Count
        root.CopyToValues(arr, 0) |> ignore
        arr
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.ToList() = root.ToList []

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.ToArray() =
        let arr = Array.zeroCreate root.Count
        root.CopyTo(arr, 0) |> ignore
        arr
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    member x.GetKeys() = 
        HashSet(cmp, root.GetKeys())

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member ComputeDelta(l : HashMap<'K, 'V>, r : HashMap<'K, 'V>, add : 'K -> 'V -> 'OP, update : 'K -> 'V -> 'V -> ValueOption<'OP>, remove : 'K -> 'V -> 'OP) =   
        let cmp = DefaultEqualityComparer<'K>.Instance
        let result = HashMapNode.computeDelta cmp add update remove l.Root r.Root
        HashMap(cmp, result)
 
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member UnionWith(l : HashMap<'K, 'V>, r : HashMap<'K, 'V>, resolve : 'K -> 'V -> 'V -> 'V) =   
        let cmp = DefaultEqualityComparer<'K>.Instance
        let result = HashMapNode.unionWith cmp (fun k l r -> resolve k l r |> ValueSome) l.Root r.Root
        HashMap(cmp, result)
  
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member UnionWithValueOption(l : HashMap<'K, 'V>, r : HashMap<'K, 'V>, resolve : 'K -> 'V -> 'V -> ValueOption<'V>) =   
        let cmp = DefaultEqualityComparer<'K>.Instance
        let result = HashMapNode.unionWith cmp resolve l.Root r.Root
        HashMap(cmp, result)

    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Union(l : HashMap<'K, 'V>, r : HashMap<'K, 'V>) =   
        let cmp = DefaultEqualityComparer<'K>.Instance
        let result = HashMapNode.union cmp l.Root r.Root
        HashMap(cmp, result)
  
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member ApplyDelta(state : HashMap<'K, 'V>, delta : HashMap<'K, 'D>, apply : 'K -> voption<'V> -> 'D -> struct(voption<'V> * voption<'DOut>)) =   
        let cmp = DefaultEqualityComparer<'K>.Instance
        let struct (ns, nd) = HashMapNode.applyDelta cmp apply state.Root delta.Root
        HashMap(cmp, ns), HashMap(cmp, nd)
        
    [<MethodImpl(MethodImplOptions.AggressiveInlining)>]
    static member Choose2(l : HashMap<'K, 'V>, r : HashMap<'K, 'T>, resolve : 'K -> voption<'V> -> voption<'T> -> voption<'R>) =   
        let cmp = DefaultEqualityComparer<'K>.Instance
        let result = HashMapNode.choose2 cmp resolve l.Root r.Root
        HashMap(cmp, result)
        
    member x.CopyTo(array : ('K * 'V)[], arrayIndex : int) =
        root.CopyTo(array, arrayIndex) |> ignore

    member x.GetEnumerator() = new HashMapEnumerator<_,_>(x)
    member x.GetStructEnumerator() = new HashMapStructEnumerator<_,_>(x)

    interface System.Collections.IEnumerable with 
        member x.GetEnumerator() = new HashMapEnumerator<_,_>(x) :> _
        
    interface System.Collections.Generic.IEnumerable<'K * 'V> with 
        member x.GetEnumerator() = new HashMapEnumerator<_,_>(x) :> _
        
    interface System.Collections.Generic.ICollection<'K * 'V> with
        member x.Count = x.Count
        member x.Contains((k, v)) = 
            match x.TryFindV k with
            | ValueSome v1 -> Unchecked.equals v v1
            | _ -> false
        member x.IsReadOnly = true
        member x.Add _ = failwith "readonly"
        member x.Remove _ = failwith "readonly"
        member x.Clear() = failwith "readonly"
        member x.CopyTo(array : ('K * 'V)[], arrayIndex : int) =
            x.CopyTo(array, arrayIndex)
        
        
    new(elements : seq<'K * 'V>) = 
        let o = HashMap.OfSeq elements
        HashMap<'K, 'V>(o.Comparer, o.Root)

    new(elements : HashMap<'K, 'V>) = 
        HashMap<'K, 'V>(elements.Comparer, elements.Root)

    new(elements : array<'K * 'V>) = 
        let o = HashMap.OfArray elements
        HashMap<'K, 'V>(o.Comparer, o.Root)  
        
    #if !FABLE_COMPILER
    new(elements : seq<struct ('K * 'V)>) = 
        let o = HashMap.OfSeqV elements
        HashMap<'K, 'V>(o.Comparer, o.Root)

    new(elements : array<struct ('K * 'V)>) = 
        let o = HashMap.OfArrayV elements
        HashMap<'K, 'V>(o.Comparer, o.Root)
    #endif

and HashMapEnumerator<'K, 'V> =
    struct // Array Buffer (with re-use) + Inline Stack Head 
        val mutable private Root : HashMapNode<'K, 'V>
        val mutable private Head : HashMapNode<'K, 'V>
        val mutable private Tail : list<HashMapNode<'K, 'V>>
        val mutable private Values : ('K * 'V)[]
        val mutable private BufferValueCount : int
        val mutable private Index : int
        
        member x.Collect() =
            match x.Head with
            | :? HashMapNoCollisionLeaf<'K, 'V> as h ->
                x.Values.[0] <- (h.Key, h.Value)
                x.BufferValueCount <- 1
                x.Index <- 0
                if x.Tail.IsEmpty then
                    x.Head <- Unchecked.defaultof<_>
                    x.Tail <- []
                else
                    x.Head <- x.Tail.Head
                    x.Tail <- x.Tail.Tail    
                true

            | :? HashMapCollisionLeaf<'K, 'V> as h ->
                                    
                let cnt = h.Count
                if x.Values.Length < cnt then
                    x.Values <- Array.zeroCreate cnt

                x.Values.[0] <- (h.Key, h.Value)
                let mutable c = h.Next
                let mutable i = 1
                while not (isNull c) do
                    x.Values.[i] <- (c.Key, c.Value)
                    c <- c.Next
                    i <- i + 1
                x.BufferValueCount <- cnt
                x.Index <- 0
                if x.Tail.IsEmpty then
                    x.Head <- Unchecked.defaultof<_>
                    x.Tail <- []
                else
                    x.Head <- x.Tail.Head
                    x.Tail <- x.Tail.Tail
                true

            | :? HashMapInner<'K, 'V> as h ->
                if h._Count <= 16 then
                    h.CopyTo(x.Values, 0) |> ignore
                    x.BufferValueCount <- h._Count
                    x.Index <- 0
                    if x.Tail.IsEmpty then
                        x.Head <- Unchecked.defaultof<_>
                        x.Tail <- []
                    else
                        x.Head <- x.Tail.Head
                        x.Tail <- x.Tail.Tail
                    true
                else
                    x.Head <- h.Left
                    x.Tail <- h.Right :: x.Tail
                    x.Collect()
                
            | _ -> false

        member x.MoveNext() =
            x.Index <- x.Index + 1
            if x.Index < x.BufferValueCount then
                true
            else
                x.Collect()

        member x.Reset() =
            x.Index <- -1
            x.Head <- x.Root
            x.Tail <- []
            x.BufferValueCount <- -1

        member x.Dispose() =
            x.Values <- null
            x.Index <- -1
            x.Head <- Unchecked.defaultof<_>
            x.Tail <- []
            x.Root <- Unchecked.defaultof<_>

        member x.Current = x.Values.[x.Index]

        interface System.Collections.IEnumerator with
            member x.MoveNext() = x.MoveNext()
            member x.Reset() = x.Reset()
            member x.Current = x.Current :> obj
            
        interface System.Collections.Generic.IEnumerator<'K * 'V> with
            member x.Current = x.Current
            member x.Dispose() = x.Dispose()

        new (map : HashMap<'K, 'V>) =
            let cnt = map.Count
            if cnt <= 16 then
                {
                    Root = map.Root
                    Head = Unchecked.defaultof<_>
                    Tail = []
                    Values = if cnt > 0 then map.ToArray() else null
                    BufferValueCount = cnt
                    Index = -1
                }
            else
                {
                    Root = map.Root
                    Head = map.Root
                    Tail = []
                    Values = Array.zeroCreate 16
                    BufferValueCount = -1
                    Index = -1
                }
    end

and HashMapStructEnumerator<'K, 'V> =
    struct // Array Buffer (with re-use) + Inline Stack Head 
        val mutable private Root : HashMapNode<'K, 'V>
        val mutable private Head : HashMapNode<'K, 'V>
        val mutable private Tail : list<HashMapNode<'K, 'V>>
        val mutable private Values : struct('K * 'V)[]
        val mutable private BufferValueCount : int
        val mutable private Index : int
        
        member x.Collect() =
            match x.Head with
            | :? HashMapNoCollisionLeaf<'K, 'V> as h ->
                x.Values.[0] <- struct(h.Key, h.Value)
                x.BufferValueCount <- 1
                x.Index <- 0
                if x.Tail.IsEmpty then
                    x.Head <- Unchecked.defaultof<_>
                    x.Tail <- []
                else
                    x.Head <- x.Tail.Head
                    x.Tail <- x.Tail.Tail    
                true

            | :? HashMapCollisionLeaf<'K, 'V> as h ->
                                    
                let cnt = h.Count
                if x.Values.Length < cnt then
                    x.Values <- Array.zeroCreate cnt

                x.Values.[0] <- struct(h.Key, h.Value)
                let mutable c = h.Next
                let mutable i = 1
                while not (isNull c) do
                    x.Values.[i] <- struct(c.Key, c.Value)
                    c <- c.Next
                    i <- i + 1
                x.BufferValueCount <- cnt
                x.Index <- 0
                if x.Tail.IsEmpty then
                    x.Head <- Unchecked.defaultof<_>
                    x.Tail <- []
                else
                    x.Head <- x.Tail.Head
                    x.Tail <- x.Tail.Tail
                true

            | :? HashMapInner<'K, 'V> as h ->
                if h._Count <= 16 then
                    h.CopyToV(x.Values, 0) |> ignore
                    x.BufferValueCount <- h._Count
                    x.Index <- 0
                    if x.Tail.IsEmpty then
                        x.Head <- Unchecked.defaultof<_>
                        x.Tail <- []
                    else
                        x.Head <- x.Tail.Head
                        x.Tail <- x.Tail.Tail
                    true
                else
                    x.Head <- h.Left
                    x.Tail <- h.Right :: x.Tail
                    x.Collect()
                
            | _ -> false

        member x.MoveNext() =
            x.Index <- x.Index + 1
            if x.Index < x.BufferValueCount then
                true
            else
                x.Collect()

        member x.Reset() =
            x.Index <- -1
            x.Head <- x.Root
            x.Tail <- []
            x.BufferValueCount <- -1

        member x.Dispose() =
            x.Values <- null
            x.Index <- -1
            x.Head <- Unchecked.defaultof<_>
            x.Tail <- []
            x.Root <- Unchecked.defaultof<_>

        member x.Current = x.Values.[x.Index]

        interface System.Collections.IEnumerator with
            member x.MoveNext() = x.MoveNext()
            member x.Reset() = x.Reset()
            member x.Current = x.Current :> obj
            
        interface System.Collections.Generic.IEnumerator<struct('K * 'V)> with
            member x.Current = x.Current
            member x.Dispose() = x.Dispose()

        new (map : HashMap<'K, 'V>) =
            let cnt = map.Count
            if cnt <= 16 then
                {
                    Root = map.Root
                    Head = Unchecked.defaultof<_>
                    Tail = []
                    Values = if cnt > 0 then map.ToArrayV() else null
                    BufferValueCount = cnt
                    Index = -1
                }
            else
                {
                    Root = map.Root
                    Head = map.Root
                    Tail = []
                    Values = Array.zeroCreate 16
                    BufferValueCount = -1
                    Index = -1
                }
    end

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
[<RequireQualifiedAccess>]
module HashSet =

    /// The empty set.
    [<GeneralizableValue>]
    let empty<'T> = HashSet<'T>.Empty

    /// The number of elements in the set `O(1)`
    let inline count (set: HashSet<'T>) = set.Count
    
    /// Is the set empty? `O(1)`
    let inline isEmpty (set: HashSet<'T>) = set.IsEmpty

    /// Are the sets equal? `O(N)`
    let inline equals (a : HashSet<'T>) (b : HashSet<'T>) =
        a.Equals(b)

    /// Creates a set with a single entry.
    /// `O(1)`
    let inline single (value: 'T) =
        HashSet<'T>.Single(value)

    /// Creates a set with all entries from the seq.
    /// `O(N * log N)`
    let inline ofSeq (seq: seq<'T>) =
        HashSet<'T>.OfSeq seq

    /// Creates a set with all entries from the Set.
    /// `O(N * log N)`
    let inline ofSet (set: Set<'T>) = 
        set |> ofSeq

    /// Creates a set with all entries from the list.
    /// `O(N * log N)`
    let inline ofList (list: list<'T>) = 
        HashSet<'T>.OfList list

    /// Creates a set with all entries from the array.
    /// `O(N * log N)`
    let inline ofArray (arr: array<'T>) = 
        HashSet<'T>.OfArray arr

    /// Creates a seq holding all values.
    /// `O(N)`
    let inline toSeq (set: HashSet<'T>) = 
        set.ToSeq()

    /// Creates a list holding all values.
    /// `O(N)`
    let inline toList (set: HashSet<'T>) = 
        set.ToList()

    /// Creates an array holding all values.
    /// `O(N)`
    let inline toArray (set: HashSet<'T>) = 
        set.ToArray()

    /// Creates a Set holding all entries contained in the HashSet.
    /// `O(N)`
    let inline toSet (set: HashSet<'T>) =
        set.ToSeq() |> Set.ofSeq

    /// Adds the given value. `O(log N)`
    let inline add (value: 'T) (set: HashSet<'T>) =
        set.Add(value)

    /// Removes the given value. `O(log N)`
    let inline remove (value: 'T) (set: HashSet<'T>) =
        set.Remove(value)
 
    /// Tries to remove the given value from the set and returns the rest of the set.
    /// `O(log N)`       
    let inline tryRemove (value: 'T) (set: HashSet<'T>) =
        set.TryRemove(value)


    /// Tests if an entry for the given key exists. `O(log N)`
    let inline contains (value: 'T) (set: HashSet<'T>) =
        set.Contains(value)


    let inline alter (value: 'T) (update: bool -> bool) (set: HashSet<'T>) =
        set.Alter(value, update)
    
    /// Creates a new map (with the same keys) by applying the given function to all entries.
    /// `O(N)`
    let inline map (mapping: 'T -> 'R) (set: HashSet<'T>) =
        set.Map mapping
    
    /// Creates a new map (with the same keys) by applying the given function to all entries.
    /// `O(N)`
    let inline choose (mapping: 'T -> option<'R>) (set: HashSet<'T>) =
        set.Choose mapping
    
    /// Creates a new map (with the same keys) that contains all entries for which predicate was true.
    /// `O(N)`
    let inline filter (predicate: 'T -> bool) (set: HashSet<'T>) =
        set.Filter predicate

    /// Applies the iter function to all entries of the map.
    /// `O(N)`
    let inline iter (iter: 'T -> unit) (set: HashSet<'T>) =
        set.Iter iter

    /// Folds over all entries of the map.
    /// Note that the order for elements is undefined.
    /// `O(N)`
    let inline fold (folder: 'State -> 'T -> 'State) (seed: 'State) (set: HashSet<'T>) =
        set.Fold(folder, seed)
        
    /// Tests whether an entry making the predicate true exists.
    /// `O(N)`
    let inline exists (predicate: 'T -> bool) (set: HashSet<'T>) =
        set.Exists(predicate)

    /// Tests whether all entries fulfil the given predicate.
    /// `O(N)`
    let inline forall (predicate: 'T -> bool) (set: HashSet<'T>) =
        set.Forall(predicate)

    /// Creates a new map containing all elements from l and r.
    /// Colliding entries are taken from r.
    /// `O(N + M)`        
    let inline union (l : HashSet<'T>) (r : HashSet<'T>) =
        HashSet<'T>.Union(l, r)
             
    let inline difference (l : HashSet<'T>) (r : HashSet<'T>) =
        HashSet<'T>.Difference(l, r)
               
    let inline xor (l : HashSet<'T>) (r : HashSet<'T>) =
        HashSet<'T>.Xor(l, r)
               
    let inline intersect (l : HashSet<'T>) (r : HashSet<'T>) =
        HashSet<'T>.Intersect(l, r)
             
    let inline collect (mapping: 'T -> HashSet<'R>) (set: HashSet<'T>) =
        let mutable result = HashSet<'R>.Empty
        for a in set do
            result <- union result (mapping a)
        result

    let inline computeDelta (l : HashSet<'T>) (r : HashSet<'T>) =
        let inline add _v = 1
        let inline remove _v = -1

        HashSet<'T>.ComputeDelta(l, r, add, remove)

    let inline applyDelta (l : HashSet<'T>) (r : HashMap<'T, int>) =
        let inline apply _ (o : bool) (n : int) =
            if n < 0 then
                if o then struct (false, ValueSome -1)
                else struct(false, ValueNone)
            elif n > 0 then
                if o then struct (true, ValueNone)
                else struct (true, ValueSome 1)
            else
                struct(o, ValueNone)

        HashSet<'T>.ApplyDelta(l, r, apply)

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
[<RequireQualifiedAccess>]
module HashMap =

    /// The empty map.
    [<GeneralizableValue>]
    let empty<'K, 'V> : HashMap<'K, 'V> = HashMap<'K, 'V>.Empty

    /// The number of elements in the map `O(1)`
    let inline count (map: HashMap<'K, 'V>) = map.Count
    
    /// Is the map empty? `O(1)`
    let inline isEmpty (map: HashMap<'K, 'V>) = map.IsEmpty

    let inline equals (a : HashMap<'K, 'V>) (b : HashMap<'K, 'V>) =
        a.Equals(b)

    let inline keys (map: HashMap<'K, 'V>) = map.GetKeys()

    /// Creates a map with a single entry.
    /// `O(1)`
    let inline single (key: 'K) (value: 'V) =
        HashMap<'K,'V>.Single(key, value)

    /// Creates a map with all entries from the seq.
    /// `O(N * log N)`
    let inline ofSeq (seq: seq<'K * 'V>) =
        HashMap<'K, 'V>.OfSeq seq

    /// Creates a map with all entries from the map.
    /// `O(N * log N)`
    let inline ofMap (map: Map<'K, 'V>) = 
        map |> Map.toSeq |> ofSeq

    /// Creates a map with all entries from the list.
    /// `O(N * log N)`
    let inline ofList (list: list<'K * 'V>) = 
        HashMap<'K, 'V>.OfList list

    /// Creates a map with all entries from the array.
    /// `O(N * log N)`
    let inline ofArray (arr: array<'K * 'V>) = 
        HashMap<'K, 'V>.OfArray arr

    /// Creates a seq holding all tuples contained in the map.
    /// `O(N)`
    let inline toSeq (map: HashMap<'K, 'V>) = 
        map.ToSeq()

    /// Creates a list holding all tuples contained in the map.
    /// `O(N)`
    let inline toList (map: HashMap<'K, 'V>) = 
        map.ToList()
        
    /// Creates an array holding all tuples contained in the map.
    /// `O(N)`
    let inline toArray (map: HashMap<'K, 'V>) = 
        map.ToArray()

    /// Creates a Map holding all entries contained in the HashMap.
    /// `O(N)`
    let inline toMap (map: HashMap<'K, 'V>) =
        map.ToSeq() |> Map.ofSeq

    /// Creates a list holding all keys contained in the map.
    /// `O(N)`
    let inline toKeyList (map: HashMap<'K, 'V>) = 
        map.ToKeyList()

    /// Creates a list holding all values contained in the map.
    /// `O(N)`
    let inline toValueList (map: HashMap<'K, 'V>) = 
        map.ToValueList()

    /// Creates an array holding all keys contained in the map.
    /// `O(N)`
    let inline toKeyArray (map: HashMap<'K, 'V>) = 
        map.ToKeyArray()

    /// Creates an array holding all values contained in the map.
    /// `O(N)`
    let inline toValueArray (map: HashMap<'K, 'V>) = 
        map.ToValueArray()

    /// Adds or updates the entry for the given key. `O(log N)`
    let inline add (key: 'K) (value: 'V) (map: HashMap<'K, 'V>) : HashMap<'K, 'V> =
        map.Add(key, value)

    /// Removes the entry for the given key. `O(log N)`
    let inline remove (key: 'K) (map: HashMap<'K, 'V>) : HashMap<'K, 'V>=
        map.Remove(key)
 
    /// Tries to remove the entry for the given key from the map and returns its value and the rest of the map.
    /// `O(log N)`       
    let inline tryRemove (key: 'K) (map: HashMap<'K, 'V>) =
        map.TryRemove(key)

    /// Tries to remove the entry for the given key from the map and returns its value and the rest of the map.
    /// `O(log N)`       
    let inline tryRemoveV (key: 'K) (map: HashMap<'K, 'V>) =
        map.TryRemoveV(key)

    /// Tries to find the value for the given key.
    /// `O(log N)`
    let inline tryFind (key: 'K) (map: HashMap<'K, 'V>) =
        map.TryFind(key)
        
    /// Tries to find the value for the given key.
    /// `O(log N)`
    let inline tryFindV (key: 'K) (map: HashMap<'K, 'V>) =
        map.TryFindV(key)

    /// Finds the value for the given key and raises KeyNotFoundException on failure.
    /// `O(log N)`
    let inline find (key: 'K) (map: HashMap<'K, 'V>) =
        match map.TryFindV key with
        | ValueSome v -> v
        | ValueNone -> raise <| KeyNotFoundException()

    /// Tests if an entry for the given key exists. `O(log N)`
    let inline containsKey (key: 'K) (map: HashMap<'K, 'V>) =
        map.ContainsKey(key)

    /// Adds, deletes or updates the entry for the given key.
    /// The update functions gets the optional old value and may optionally return
    /// A new value (or None for deleting the entry).
    /// `O(log N)`
    let inline alter (key: 'K) (update: option<'V> -> option<'V>) (map: HashMap<'K, 'V>) =
        map.Alter(key, update)
    
    /// Adds, deletes or updates the entry for the given key.
    /// The update functions gets the optional old value and may optionally return
    /// A new value (or None for deleting the entry).
    /// `O(log N)`
    let inline update (key: 'K) (update: option<'V> -> 'V) (map: HashMap<'K, 'V>) =
        map.Alter(key, update >> Some)

    /// Creates a new map (with the same keys) by applying the given function to all entries.
    /// `O(N)`
    let inline map (mapping: 'K -> 'V -> 'T) (map: HashMap<'K, 'V>) =
        map.Map mapping
    
    /// Creates a new map (with the same keys) by applying the given function to all entries.
    /// `O(N)`
    let inline choose (mapping: 'K -> 'V -> option<'T>) (map: HashMap<'K, 'V>) =
        map.Choose mapping
    
    /// Creates a new map (with the same keys) by applying the given function to all entries.
    /// `O(N)`
    let inline chooseV (mapping: 'K -> 'V -> voption<'T>) (map: HashMap<'K, 'V>) =
        map.ChooseV mapping

    /// Creates a new map (with the same keys) that contains all entries for which predicate was true.
    /// `O(N)`
    let inline filter (predicate: 'K -> 'V -> bool) (map: HashMap<'K, 'V>) =
        map.Filter predicate

    /// Applies the iter function to all entries of the map.
    /// `O(N)`
    let inline iter (iter: 'K -> 'V -> unit) (map: HashMap<'K, 'V>) =
        map.Iter iter

    /// Folds over all entries of the map.
    /// Note that the order for elements is undefined.
    /// `O(N)`
    let inline fold (folder: 'State -> 'K -> 'V -> 'State) (seed: 'State) (map: HashMap<'K, 'V>) =
        map.Fold(folder, seed)
        
    /// Tests whether an entry making the predicate true exists.
    /// `O(N)`
    let inline exists (predicate: 'K -> 'V -> bool) (map: HashMap<'K, 'V>) =
        map.Exists(predicate)

    /// Tests whether all entries fulfil the given predicate.
    /// `O(N)`
    let inline forall (predicate: 'K -> 'V -> bool) (map: HashMap<'K, 'V>) =
        map.Forall(predicate)

    /// Creates a new map containing all elements from l and r.
    /// The resolve function is used to resolve conflicts.
    /// `O(N + M)`
    let inline unionWith (resolve : 'K -> 'V -> 'V -> 'V) (l : HashMap<'K, 'V>) (r : HashMap<'K, 'V>) =
        HashMap<'K, 'V>.UnionWith(l, r, resolve)
    
    let inline choose2V (mapping : 'K -> voption<'T1> -> voption<'T2> -> voption<'R>) (l : HashMap<'K, 'T1>) (r : HashMap<'K, 'T2>) =
        HashMap<'K, 'T1>.Choose2(l, r, mapping)

    let inline choose2 (mapping : 'K -> option<'T1> -> option<'T2> -> option<'R>) (l : HashMap<'K, 'T1>) (r : HashMap<'K, 'T2>) =
        let mapping = OptimizedClosures.FSharpFunc<_,_,_,_>.Adapt(mapping)
        
        let mapping k l r =
            let l = match l with | ValueSome v -> Some v | ValueNone -> None
            let r = match r with | ValueSome v -> Some v | ValueNone -> None
            match mapping.Invoke(k, l, r) with
            | Some v -> ValueSome v
            | None -> ValueNone

        HashMap<'K, 'T1>.Choose2(l, r, mapping)
        
    let inline map2V (mapping : 'K -> voption<'T1> -> voption<'T2> -> 'R) (l : HashMap<'K, 'T1>) (r : HashMap<'K, 'T2>) =
        let mapping = OptimizedClosures.FSharpFunc<_,_,_,_>.Adapt(mapping)
        HashMap<'K, 'T1>.Choose2(l, r, fun k l r -> mapping.Invoke(k,l,r) |> ValueSome)

    let inline map2 (mapping : 'K -> option<'T1> -> option<'T2> -> 'R) (l : HashMap<'K, 'T1>) (r : HashMap<'K, 'T2>) =
        let mapping = OptimizedClosures.FSharpFunc<_,_,_,_>.Adapt(mapping)
        
        let mapping k l r =
            let l = match l with | ValueSome v -> Some v | ValueNone -> None
            let r = match r with | ValueSome v -> Some v | ValueNone -> None
            mapping.Invoke(k, l, r) |> ValueSome

        HashMap<'K, 'T1>.Choose2(l, r, mapping)

    /// Creates a new map containing all elements from l and r.
    /// Colliding entries are taken from r.
    /// `O(N + M)`        
    let inline union (l : HashMap<'K, 'V>) (r : HashMap<'K, 'V>) =
        HashMap<'K, 'V>.Union(l, r)

    let unionMany (xs : seq<HashSet<'a>>) = 
        Seq.fold HashSet.union HashSet.empty xs




